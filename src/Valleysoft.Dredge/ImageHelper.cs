using System.Formats.Tar;
using Valleysoft.DockerRegistryClient;
using Valleysoft.DockerRegistryClient.Models;
using Valleysoft.DockerRegistryClient.Models.Manifests;
using Valleysoft.Dredge.Commands;

namespace Valleysoft.Dredge;

internal static class ImageHelper
{
    // See https://github.com/opencontainers/image-spec/blob/main/layer.md#whiteouts
    private const string WhiteoutMarkerPrefix = ".wh.";
    private const string OpaqueWhiteoutMarker = ".wh..wh..opq";

    public static async Task SaveImageLayersToDiskAsync(
        IDockerRegistryClientFactory dockerRegistryClientFactory, string image, string destPath, int? layerIndex,
        string layerIndexOptionName, bool noSquash, PlatformOptionsBase options,
        CancellationToken cancellationToken = default,
        IDredgePathProvider? pathProvider = null,
        bool overwriteExisting = false,
        ResolvedImage? resolvedImage = null)
    {
        // Spec for OCI image layer filesystem changeset: https://github.com/opencontainers/image-spec/blob/main/layer.md

        ValidateDestinationPath(destPath);

        Console.Error.WriteLine($"Getting layers for {image}");

        ImageSourceReference reference = ImageSourceReference.Parse(image);
        await using ResolvedImage? ownedImage = resolvedImage is null && reference.IsDaemon
            ? await new ImageSourceFactory(dockerRegistryClientFactory).OpenAsync(image, options, cancellationToken)
            : null;
        ResolvedImage? source = resolvedImage ?? ownedImage;
        if (ownedImage is not null)
        {
            ownedImage.Report(Console.Error);
        }
        ImageName imageName = source?.Name ?? ImageName.Parse(reference.Value);
        using IDockerRegistryClient? registryClient = source is null
            ? await dockerRegistryClientFactory.GetClientAsync(imageName.Registry, cancellationToken)
            : null;
        IImageContentSource content = source?.Content ??
            new RegistryImageContentSource(registryClient!, imageName);
        IImageManifest manifest = source?.Resolved.Manifest ??
            (await ManifestHelper.GetResolvedManifestAsync(registryClient!, imageName, options, cancellationToken)).Manifest;
        if (source?.IsDaemon == true &&
            string.Equals(source.Configuration.Os, "windows", StringComparison.OrdinalIgnoreCase))
        {
            throw new NotSupportedException(
                "Saving extracted Docker daemon layers supports Linux images only; Windows filesystem extraction is not supported.");
        }

        pathProvider ??= new DredgePathProvider();
        await using LayerStore store = LayerStore.Create(pathProvider);

        int startIndex = 0;
        int layerCount = manifest.Layers.Length;
        if (layerIndex is not null)
        {
            if (layerIndex < 0 || layerIndex >= manifest.Layers.Length)
            {
                string message = manifest.Layers.Length is 0
                    ? $"The image has no layers, so the '{layerIndexOptionName}' option cannot be used."
                    : $"Value for the '{layerIndexOptionName}' option must be in the range 0-{manifest.Layers.Length - 1}.";
                throw new Exception(message);
            }
            layerCount = layerIndex.Value + 1;

            if (noSquash)
            {
                startIndex = layerIndex.Value;
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        Directory.CreateDirectory(destPath);
        Dictionary<string, string> archivePaths = new(
            OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        Dictionary<string, ImageFileSystemEntry> mergedMetadata = new(
            OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

        for (int i = startIndex; i < layerCount; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (noSquash)
            {
                archivePaths.Clear();
            }
            IDescriptor layer = manifest.Layers[i];
            if (string.IsNullOrEmpty(layer.Digest))
            {
                throw new Exception($"Layer digest not set for image '{imageName}'");
            }

            Console.Error.WriteLine($"Layer {layer.Digest}");

            string layerName = layer.Digest[(layer.Digest.IndexOf(':') + 1)..];
            ValidateLayerDigest(layerName);
            string layerDir = await store.CreateScratchDirectoryAsync(cancellationToken);
            bool completed = false;
            try
            {
                using Stream layerStream =
                    await store.OpenExtractionBlobAsync(content, layer.Digest, layer.Size, cancellationToken);
                Dictionary<string, ImageFileSystemEntry> metadata = await ExtractLayerAsync(
                    layerStream, layerDir, new(i, layer.Digest), noSquash ? null : destPath,
                    archivePaths, mergedMetadata, cancellationToken);

                if (noSquash)
                {
                    string layerOutputPath = GetContainedPath(
                        destPath,
                        $"layer{i}-{layerName}",
                        allowFinalLink: overwriteExisting);
                    if (overwriteExisting)
                    {
                        DeleteDestinationEntry(layerOutputPath);
                    }
                    await CopyLayerAsync(layerDir, layerOutputPath, metadata, cancellationToken);
                }
                else
                {
                    await ApplyLayerAsync(layerDir, destPath, metadata, mergedMetadata, cancellationToken);
                }
                completed = true;
            }
            finally
            {
                try
                {
                    Directory.Delete(layerDir, recursive: true);
                }
                catch (Exception exception) when (!completed &&
                    exception is IOException or UnauthorizedAccessException)
                {
                    Console.Error.WriteLine($"Failed to delete layer scratch directory '{layerDir}': {exception.Message}");
                }
            }
        }
        if (!noSquash)
        {
            RebaseRootClampedLinks(destPath, mergedMetadata, cancellationToken);
            ApplyLayerMetadata(destPath, mergedMetadata, cancellationToken);
        }
    }

    private static async Task<Dictionary<string, ImageFileSystemEntry>> ExtractLayerAsync(
        Stream layerStream,
        string layerDir,
        ImageLayerReference layer,
        string? mergedDirectory,
        Dictionary<string, string> archivePaths,
        IReadOnlyDictionary<string, ImageFileSystemEntry> mergedMetadata,
        CancellationToken cancellationToken)
    {
        Console.Error.WriteLine($"\tExtracting layer...");

        Directory.CreateDirectory(layerDir);
        List<(string Path, string Target)> hardLinks = [];
        List<(string Path, string Target)> symbolicLinks = [];
        StringComparer pathComparer = OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        HashSet<string> whiteoutTargets = new(pathComparer);
        HashSet<string> opaqueDirectories = new(pathComparer);
        Dictionary<string, ImageFileSystemEntry> metadata = new(StringComparer.Ordinal);

        using ImageTarReader.LayerArchive archive = ImageTarReader.Open(layerStream);
        TarReader tarReader = archive.Reader;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            TarEntry? entry = await tarReader.GetNextEntryAsync(
                copyData: false,
                cancellationToken);

            if (entry is null)
            {
                break;
            }

            string originalName = ImagePath.NormalizeArchive(entry.Name);
            if (originalName.Length == 0)
            {
                continue;
            }

            string entryFileName = ImagePath.GetFileName(originalName);

            if (entryFileName.StartsWith(WhiteoutMarkerPrefix, StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(entryFileName, OpaqueWhiteoutMarker, StringComparison.OrdinalIgnoreCase))
            {
                ValidatePathSegment(entryFileName[WhiteoutMarkerPrefix.Length..], "whiteout target");
                whiteoutTargets.Add(GetHostArchivePath(ImagePath.Join(
                    ImagePath.GetDirectoryName(originalName),
                    entryFileName[WhiteoutMarkerPrefix.Length..]), archivePaths));
            }
            else if (string.Equals(entryFileName, OpaqueWhiteoutMarker, StringComparison.OrdinalIgnoreCase))
            {
                opaqueDirectories.Add(GetHostArchivePath(
                    ImagePath.GetDirectoryName(originalName), archivePaths));
            }

            string entryName = GetHostArchivePath(originalName, archivePaths);
            metadata[entryName] = new()
            {
                Path = originalName,
                Type = ImageLayerScanner.GetFileType(entry),
                IntroducedLayer = layer,
                Mode = (int)entry.Mode,
                ModifiedTime = entry.ModificationTime.UtcDateTime
            };
            if (entry.EntryType == TarEntryType.Directory)
            {
                string directoryPath = GetContainedPath(layerDir, entryName);
                if (File.Exists(directoryPath))
                {
                    File.Delete(directoryPath);
                }
                Directory.CreateDirectory(directoryPath);
                continue;
            }
            await ExtractTarEntry(
                layerDir, entry, entryName, hardLinks, symbolicLinks, archivePaths, cancellationToken);
        }

        while (hardLinks.Count > 0)
        {
            int copiedLinkCount = 0;
            for (int i = hardLinks.Count - 1; i >= 0; i--)
            {
                (string path, string target) = hardLinks[i];
                string relativeTarget = GetHostArchivePath(
                    ImagePath.NormalizeArchive(target.TrimStart('/')), archivePaths);
                string targetPath = GetContainedPath(layerDir, relativeTarget);
                if (!File.Exists(targetPath) && mergedDirectory is not null &&
                    !metadata.ContainsKey(relativeTarget) &&
                    !IsHiddenLowerTarget(relativeTarget, whiteoutTargets, opaqueDirectories))
                {
                    targetPath = GetContainedPath(mergedDirectory, relativeTarget);
                }
                if (File.Exists(targetPath))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    await FileHelper.CopyFileAsync(
                        new FileInfo(targetPath), path, overwrite: true, cancellationToken);
                    string relativePath = Path.GetRelativePath(layerDir, path);
                    int targetMode = (metadata.TryGetValue(relativeTarget, out ImageFileSystemEntry? targetMetadata) ||
                        mergedMetadata.TryGetValue(relativeTarget, out targetMetadata))
                        ? targetMetadata.Mode
                        : OperatingSystem.IsWindows() ? 0 : (int)File.GetUnixFileMode(targetPath);
                    metadata[relativePath] = metadata[relativePath] with { Mode = targetMode };
                    hardLinks.RemoveAt(i);
                    copiedLinkCount++;
                }
            }

            if (copiedLinkCount == 0)
            {
                throw new InvalidDataException($"Unable to resolve hard link target '{hardLinks[0].Target}'.");
            }
        }
        foreach ((string path, string target) in symbolicLinks)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (ImageFileSystemExtractor.PathExists(path))
            {
                throw new InvalidDataException(
                    $"Layer path '{path}' conflicts with a symbolic link.");
            }
            string targetPath = GetLinkTargetPath(layerDir, path, target);
            bool isDirectory = Directory.Exists(targetPath);
            if (!isDirectory && mergedDirectory is not null)
            {
                isDirectory = Directory.Exists(GetContainedPath(
                    mergedDirectory, Path.GetRelativePath(layerDir, targetPath),
                    allowFinalLink: true, validateLinkParents: false));
            }
            FileHelper.CreateSymbolicLink(path, target, isDirectory);
        }
        return metadata;
    }

    private static bool IsHiddenLowerTarget(
        string path, IReadOnlySet<string> whiteouts, IReadOnlySet<string> opaqueDirectories)
    {
        while (path.Length > 0)
        {
            if (whiteouts.Contains(path))
            {
                return true;
            }
            path = Path.GetDirectoryName(path) ?? string.Empty;
            if (opaqueDirectories.Contains(path))
            {
                return true;
            }
        }
        return false;
    }

    private static async Task ApplyLayerAsync(
        string layerDir,
        string workingDir,
        IReadOnlyDictionary<string, ImageFileSystemEntry> metadata,
        Dictionary<string, ImageFileSystemEntry> mergedMetadata,
        CancellationToken cancellationToken)
    {
        Console.Error.WriteLine($"\tApplying layer...");

        FileSystemInfo[] layerFiles = [.. GetLayerEntries(new DirectoryInfo(layerDir))];

        foreach (FileSystemInfo layerFile in layerFiles
            .Where(IsOpaqueWhiteout)
            .OrderBy(file => Path.GetRelativePath(layerDir, file.FullName).Count(c => c == Path.DirectorySeparatorChar)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            string? layerFileDirName = Path.GetDirectoryName(Path.GetRelativePath(layerDir, layerFile.FullName));
            if (string.IsNullOrEmpty(layerFileDirName))
            {
                foreach (string child in Directory.EnumerateFileSystemEntries(workingDir))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    DeleteDestinationEntry(child);
                }
                mergedMetadata.Clear();
                continue;
            }

            string fullDirPath = GetContainedPath(
                workingDir,
                layerFileDirName,
                allowFinalLink: true);
            DeleteDestinationEntry(fullDirPath);
            RemoveLayerMetadata(mergedMetadata, layerFileDirName, includePath: false);
        }

        foreach (FileSystemInfo layerFile in layerFiles.Where(IsWhiteout))
        {
            cancellationToken.ThrowIfCancellationRequested();
            string layerFileRelativePath = Path.GetRelativePath(layerDir, layerFile.FullName);
            string? layerFileDirName = Path.GetDirectoryName(layerFileRelativePath);
            string actualFileName = layerFile.Name[WhiteoutMarkerPrefix.Length..];
            ValidatePathSegment(actualFileName, "whiteout target");
            string fullPath = GetContainedPath(
                workingDir,
                Path.Combine(layerFileDirName ?? string.Empty, actualFileName),
                allowFinalLink: true);
            DeleteDestinationEntry(fullPath);
            RemoveLayerMetadata(mergedMetadata,
                Path.Combine(layerFileDirName ?? string.Empty, actualFileName), includePath: true);
        }

        await CopyLayerAsync(
            layerDir, workingDir, metadata, cancellationToken,
            skipWhiteouts: true, mergedMetadata: mergedMetadata);
    }

    private static async Task CopyLayerAsync(
        string layerDir,
        string workingDir,
        IReadOnlyDictionary<string, ImageFileSystemEntry> metadata,
        CancellationToken cancellationToken,
        bool skipWhiteouts = false,
        Dictionary<string, ImageFileSystemEntry>? mergedMetadata = null)
    {
        Directory.CreateDirectory(workingDir);
        FileSystemInfo[] layerEntries = [.. GetLayerEntries(new DirectoryInfo(layerDir))];
        foreach (FileSystemInfo layerFile in layerEntries
            .Where(file => !skipWhiteouts || (!IsOpaqueWhiteout(file) && !IsWhiteout(file))))
        {
            cancellationToken.ThrowIfCancellationRequested();
            string layerFileRelativePath = Path.GetRelativePath(layerDir, layerFile.FullName);
            string dest = GetContainedPath(
                workingDir,
                layerFileRelativePath,
                allowFinalLink: true);
            if (mergedMetadata is not null)
            {
                if (layerFile.LinkTarget is not null || layerFile is not DirectoryInfo)
                {
                    if (Directory.Exists(dest) && !IsReparsePoint(dest))
                    {
                        RemoveLayerMetadata(mergedMetadata, layerFileRelativePath, includePath: true);
                    }
                    else
                    {
                        mergedMetadata.Remove(layerFileRelativePath);
                    }
                }
                else if (mergedMetadata.TryGetValue(layerFileRelativePath, out ImageFileSystemEntry? previous) &&
                    previous.Type != ImageFileType.Directory)
                {
                    mergedMetadata.Remove(layerFileRelativePath);
                }
                if (metadata.TryGetValue(layerFileRelativePath, out ImageFileSystemEntry? current))
                {
                    mergedMetadata[layerFileRelativePath] = current;
                }
            }
            string destDir = Path.GetDirectoryName(dest)!;
            if (!Directory.Exists(destDir))
            {
                CreateDestinationDirectory(workingDir, destDir);
            }

            if (layerFile.LinkTarget is not null)
            {
                DeleteDestinationEntry(dest);

                FileHelper.CreateSymbolicLink(
                    dest, layerFile.LinkTarget, layerFile is DirectoryInfo);
            }
            else if (layerFile is DirectoryInfo)
            {
                if (File.Exists(dest) || IsReparsePoint(dest))
                {
                    DeleteDestinationEntry(dest);
                }
                Directory.CreateDirectory(dest);
            }
            else
            {
                DeleteDestinationEntry(dest);

                await FileHelper.CopyFileAsync(
                    (FileInfo)layerFile, dest, overwrite: true, cancellationToken);
            }
        }
        if (mergedMetadata is null)
        {
            RebaseRootClampedLinks(workingDir, metadata, cancellationToken);
            ApplyLayerMetadata(workingDir, metadata, cancellationToken);
        }
    }

    private static void RebaseRootClampedLinks(
        string root, IReadOnlyDictionary<string, ImageFileSystemEntry> metadata,
        CancellationToken cancellationToken)
    {
        string[] links = [.. metadata.Where(item => item.Value.Type == ImageFileType.SymbolicLink)
            .Select(item => item.Key.Replace(Path.DirectorySeparatorChar, '/'))];
        if (links.Length == 0)
        {
            return;
        }
        Dictionary<string, FileSystemInfo> entries = new(
            OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        foreach (FileSystemInfo entry in GetLayerEntries(new DirectoryInfo(root)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            entries.Add(Path.GetRelativePath(root, entry.FullName).Replace(Path.DirectorySeparatorChar, '/'), entry);
        }
        foreach (string link in links)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string? clampedTarget;
            string destination = GetContainedPath(root, link, allowFinalLink: true);
            try
            {
                clampedTarget = GetRootClampedTarget(link, entries, cancellationToken);
            }
            catch (InvalidDataException)
            {
                DeleteDestinationEntry(destination);
                throw;
            }
            if (clampedTarget is null)
            {
                continue;
            }
            string target = string.Concat(Enumerable.Repeat(
                $"..{Path.DirectorySeparatorChar}", link.Count(character => character == '/'))) +
                clampedTarget.Replace('/', Path.DirectorySeparatorChar);
            bool targetIsDirectory = clampedTarget.Length == 0 ||
                (entries.TryGetValue(clampedTarget, out FileSystemInfo? targetEntry) && targetEntry is DirectoryInfo);
            DeleteDestinationEntry(destination);
            FileHelper.CreateSymbolicLink(destination, target.Length == 0 ? "." : target, targetIsDirectory);
        }
    }

    private static string? GetRootClampedTarget(
        string link, IReadOnlyDictionary<string, FileSystemInfo> entries,
        CancellationToken cancellationToken)
    {
        string current = link;
        bool clamped = false;
        int hops = 0;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string[] segments = current.Split('/', StringSplitOptions.RemoveEmptyEntries);
            List<string> resolved = [];
            bool restarted = false;
            for (int index = 0; index < segments.Length; index++)
            {
                if (segments[index] == ".")
                {
                    continue;
                }
                if (segments[index] == "..")
                {
                    if (resolved.Count == 0)
                    {
                        // An image root clamps ".."; a host export directory does not.
                        clamped = true;
                        current = string.Join('/', segments.Skip(index + 1));
                        restarted = true;
                        break;
                    }
                    resolved.RemoveAt(resolved.Count - 1);
                    continue;
                }
                resolved.Add(segments[index]);
                string candidate = string.Join('/', resolved);
                if (!entries.TryGetValue(candidate, out FileSystemInfo? entry))
                {
                    return clamped ? current : null;
                }
                if (entry.LinkTarget is string target)
                {
                    if (++hops >= ImagePathResolver.MaximumLinkHops)
                    {
                        if (clamped)
                        {
                            throw new InvalidDataException(
                                $"Link '/{link}' cannot be safely rebased within {ImagePathResolver.MaximumLinkHops} hops.");
                        }
                        return null;
                    }
                    string remainder = string.Join('/', segments.Skip(index + 1));
                    if (Path.IsPathRooted(target))
                    {
                        throw new InvalidDataException(
                            $"Link '/{link}' traverses an absolute host link '/{candidate}'.");
                    }
                    current = ImagePath.ResolveLinkTarget(ImagePath.GetDirectoryName(candidate),
                        target.Replace(Path.DirectorySeparatorChar, '/'), remainder, candidate);
                    restarted = true;
                    break;
                }
                if (entry is not DirectoryInfo && index < segments.Length - 1)
                {
                    return clamped ? current : null;
                }
            }
            if (!restarted)
            {
                return clamped ? string.Join('/', resolved) : null;
            }
        }
    }

    private static void RemoveLayerMetadata(
        Dictionary<string, ImageFileSystemEntry> metadata, string path, bool includePath)
    {
        if (includePath && metadata.TryGetValue(path, out ImageFileSystemEntry? entry) &&
            entry.Type != ImageFileType.Directory)
        {
            metadata.Remove(path);
            return;
        }
        StringComparison comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        string prefix = path + Path.DirectorySeparatorChar;
        foreach (string candidate in metadata.Keys.Where(candidate =>
            (includePath && candidate.Equals(path, comparison)) ||
            candidate.StartsWith(prefix, comparison)).ToArray())
        {
            metadata.Remove(candidate);
        }
    }

    private static void ApplyLayerMetadata(
        string root, IReadOnlyDictionary<string, ImageFileSystemEntry> metadata,
        CancellationToken cancellationToken)
    {
        // Restrictive archived modes must not block later layers from reading or replacing content.
        foreach ((string path, ImageFileSystemEntry attributes) in metadata
            .Where(item => item.Value.Type != ImageFileType.SymbolicLink)
            .OrderByDescending(item => item.Key.Count(character => character == Path.DirectorySeparatorChar)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            ImageFileSystemExtractor.ApplyMetadata(GetContainedPath(root, path), attributes);
        }
    }

    private static void CreateDestinationDirectory(
        string workingDir,
        string directoryPath)
    {
        string currentPath = Path.GetFullPath(workingDir);
        Directory.CreateDirectory(currentPath);
        string relativePath = Path.GetRelativePath(currentPath, directoryPath);
        if (relativePath == ".")
        {
            return;
        }

        foreach (string segment in relativePath.Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries))
        {
            currentPath = Path.Combine(currentPath, segment);
            if (File.Exists(currentPath))
            {
                File.Delete(currentPath);
            }

            Directory.CreateDirectory(currentPath);
        }
    }

    private static void DeleteDestinationEntry(string path)
    {
        if (IsReparsePoint(path))
        {
            FileAttributes attributes = File.GetAttributes(path);
            if (attributes.HasFlag(FileAttributes.Directory))
            {
                Directory.Delete(path);
            }
            else
            {
                File.Delete(path);
            }
        }
        else if (File.Exists(path))
        {
            File.Delete(path);
        }
        else if (Directory.Exists(path))
        {
            bool isLink = File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint);
            Directory.Delete(path, recursive: !isLink);
        }
    }

    private static bool IsOpaqueWhiteout(FileSystemInfo file) =>
        string.Equals(file.Name, OpaqueWhiteoutMarker, StringComparison.OrdinalIgnoreCase);

    private static IEnumerable<FileSystemInfo> GetLayerEntries(DirectoryInfo directory)
    {
        foreach (FileSystemInfo entry in directory.EnumerateFileSystemInfos())
        {
            yield return entry;
            if (entry is DirectoryInfo subdirectory && entry.LinkTarget is null)
            {
                foreach (FileSystemInfo child in GetLayerEntries(subdirectory))
                {
                    yield return child;
                }
            }
        }
    }

    private static bool IsWhiteout(FileSystemInfo file) =>
        !IsOpaqueWhiteout(file) &&
        file.Name.StartsWith(WhiteoutMarkerPrefix, StringComparison.OrdinalIgnoreCase);

    private static async Task ExtractTarEntry(
        string workingDir,
        TarEntry entry,
        string entryName,
        List<(string Path, string Target)> hardLinks,
        List<(string Path, string Target)> symbolicLinks,
        Dictionary<string, string> archivePaths,
        CancellationToken cancellationToken)
    {
        string filePath = GetContainedPath(workingDir, entryName);
        string? directoryPath = Path.GetDirectoryName(filePath);
        if (directoryPath is not null && !Directory.Exists(directoryPath))
        {
            Directory.CreateDirectory(directoryPath);
        }

        if (entry.EntryType == TarEntryType.HardLink && !string.IsNullOrEmpty(entry.LinkName))
        {
            hardLinks.Add((filePath, entry.LinkName));
        }
        else if (entry.EntryType == TarEntryType.SymbolicLink && !string.IsNullOrEmpty(entry.LinkName))
        {
            if (File.Exists(filePath))
            {
                File.Delete(filePath);
            }

            _ = ImagePath.ResolveLinkTarget(
                ImagePath.GetDirectoryName(ImagePath.NormalizeArchive(entry.Name)),
                entry.LinkName, string.Empty, entry.Name);
            string target = string.Join(Path.DirectorySeparatorChar,
                entry.LinkName.Split('/').Select(segment =>
                    segment is "" or "." or ".." ? segment : SanitizeHostSegment(segment)));
            if (ImagePath.IsAbsolute(entry.LinkName))
            {
                int depth = ImagePath.GetDirectoryName(ImagePath.NormalizeArchive(entry.Name))
                    .Split('/', StringSplitOptions.RemoveEmptyEntries).Length;
                target = string.Concat(Enumerable.Repeat($"..{Path.DirectorySeparatorChar}", depth)) +
                    GetHostArchivePath(entry.LinkName.TrimStart('/'), archivePaths);
            }
            _ = GetLinkTargetPath(workingDir, filePath, target);
            symbolicLinks.Add((filePath, target));
        }
        else
        {
            if (Directory.Exists(filePath))
            {
                Directory.Delete(filePath, recursive: true);
            }
            using FileStream outputStream = File.Create(filePath);
            if (entry.DataStream is not null)
            {
                await entry.DataStream.CopyToAsync(outputStream, cancellationToken);
            }
        }
    }

    private static string SanitizeHostSegment(string segment)
    {
        if (OperatingSystem.IsWindows())
        {
            foreach (char invalid in Path.GetInvalidFileNameChars())
            {
                segment = segment.Replace(invalid, '-');
            }
        }
        return segment;
    }

    private static string GetHostArchivePath(string original, Dictionary<string, string> archivePaths)
    {
        if (!OperatingSystem.IsWindows())
        {
            return original.Replace('/', Path.DirectorySeparatorChar);
        }
        string hostPath = string.Empty;
        string imagePath = string.Empty;
        foreach (string segment in original.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            imagePath = ImagePath.Join(imagePath, segment);
            hostPath = Path.Combine(hostPath, SanitizeHostSegment(segment));
            if (archivePaths.TryGetValue(hostPath, out string? previous) && previous != imagePath)
            {
                throw new InvalidDataException(
                    $"Archive paths '{previous}' and '{imagePath}' map to the same destination '{hostPath}'.");
            }
            archivePaths[hostPath] = imagePath;
        }
        if (OperatingSystem.IsWindows())
        {
            ImageFileSystemExtractor.ValidateWindowsDestinationPath(hostPath.Replace('\\', '/'));
        }
        return hostPath;
    }

    private static string GetLinkTargetPath(string workingDir, string linkPath, string linkTarget)
    {
        string target = Path.IsPathRooted(linkTarget)
            ? linkTarget.TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            : Path.Combine(Path.GetDirectoryName(Path.GetRelativePath(workingDir, linkPath)) ?? string.Empty, linkTarget);

        return GetContainedPath(
            workingDir, target, allowFinalLink: true, validateLinkParents: false);
    }

    private static string GetContainedPath(
        string rootPath,
        string relativePath,
        bool allowFinalLink = false,
        bool validateLinkParents = true)
    {
        string fullRootPath = Path.GetFullPath(rootPath);
        string fullPath = Path.GetFullPath(Path.Combine(fullRootPath, relativePath));
        string pathFromRoot = Path.GetRelativePath(fullRootPath, fullPath);

        if (Path.IsPathRooted(pathFromRoot) ||
            pathFromRoot.Equals("..", StringComparison.Ordinal) ||
            pathFromRoot.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal) ||
            pathFromRoot.StartsWith($"..{Path.AltDirectorySeparatorChar}", StringComparison.Ordinal))
        {
            throw new InvalidDataException($"Archive entry path '{relativePath}' is outside the layer directory.");
        }

        if (validateLinkParents)
        {
            ValidateNoLinkParents(fullRootPath, fullPath);
        }
        if (!allowFinalLink && IsReparsePoint(fullPath))
        {
            throw new InvalidDataException($"Path '{fullPath}' is a symbolic link.");
        }
        return fullPath;
    }

    internal static void ValidateDestinationPath(string destinationPath)
    {
        string fullPath = Path.GetFullPath(destinationPath);
        if (IsReparsePoint(fullPath))
        {
            throw new InvalidDataException(
                $"Output path '{fullPath}' cannot be a symbolic link.");
        }
    }

    private static void ValidateNoLinkParents(string rootPath, string fullPath)
    {
        string? path = Path.GetDirectoryName(fullPath);
        while (path is not null)
        {
            if (IsReparsePoint(path))
            {
                throw new InvalidDataException($"Path '{fullPath}' traverses a symbolic link.");
            }

            if (Path.GetFullPath(path).Equals(rootPath, StringComparison.OrdinalIgnoreCase))
            {
                break;
            }

            path = Path.GetDirectoryName(path);
        }
    }

    private static bool IsReparsePoint(string path)
    {
        try
        {
            return File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint);
        }
        catch (FileNotFoundException)
        {
            return false;
        }
        catch (DirectoryNotFoundException)
        {
            return false;
        }
    }

    private static void ValidatePathSegment(string value, string description)
    {
        if (string.IsNullOrEmpty(value) ||
            value is "." or ".." ||
            Path.IsPathRooted(value) ||
            value.IndexOfAny([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]) >= 0)
        {
            throw new InvalidDataException($"Invalid {description} '{value}'.");
        }
    }

    private static void ValidateLayerDigest(string value)
    {
        ValidatePathSegment(value, "layer digest");

        if (value.Contains(':'))
        {
            throw new InvalidDataException($"Invalid layer digest '{value}'.");
        }
    }
}
