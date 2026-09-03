using System.Formats.Tar;
using System.IO.Compression;
using Valleysoft.DockerRegistryClient;
using Valleysoft.DockerRegistryClient.Models.Images;
using Valleysoft.DockerRegistryClient.Models.Manifests;
using Valleysoft.Dredge.Commands;

namespace Valleysoft.Dredge;

internal sealed class ImageFileSystem
{
    private const int MaximumLinkHops = 40;

    private readonly IDockerRegistryClient client;
    private readonly ImageName imageName;
    private readonly IImageManifest manifest;
    private readonly Dictionary<string, ImageFileSystemEntry> entries =
        new(StringComparer.Ordinal);
    private readonly Dictionary<string, ImageFileSystemEntry> deletedEntries =
        new(StringComparer.Ordinal);

    private ImageFileSystem(
        IDockerRegistryClient client,
        ImageName imageName,
        IImageManifest manifest)
    {
        this.client = client;
        this.imageName = imageName;
        this.manifest = manifest;
    }

    public static async Task<ImageFileSystem> CreateAsync(
        IDockerRegistryClient client,
        ImageName imageName,
        PlatformOptionsBase options,
        CancellationToken cancellationToken)
    {
        ResolvedManifest resolved =
            await ManifestHelper.GetResolvedManifestAsync(client, imageName, options, cancellationToken);
        IImageManifest manifest = resolved.Manifest;
        string configDigest = manifest.Config?.Digest ??
            throw new NotSupportedException(
                $"Could not resolve the image config digest of '{imageName}'.");
        Image config = await client.Blobs.GetImageAsync(
            imageName.Repo,
            configDigest,
            cancellationToken);
        if (string.Equals(config.Os, "windows", StringComparison.OrdinalIgnoreCase))
        {
            throw new NotSupportedException(
                "Image filesystem commands support Linux image layers only; Windows image layers are not supported.");
        }

        ImageFileSystem fileSystem = new(client, imageName, manifest);
        await fileSystem.BuildIndexAsync(cancellationToken);
        return fileSystem;
    }

    public IReadOnlyList<ImageFileSystemEntry> List(
        string? requestedPath,
        bool recursive,
        bool showDeleted)
    {
        string path = ImagePath.NormalizeRequested(requestedPath);
        ImageFileSystemEntry? selected = null;
        if (path.Length > 0)
        {
            string lookupPath = ResolveParentComponents(path);
            entries.TryGetValue(lookupPath, out selected);
            if (selected is null && showDeleted)
            {
                deletedEntries.TryGetValue(lookupPath, out selected);
            }
            if (selected is null)
            {
                throw new FileNotFoundException($"Path '/{path}' does not exist in the image.");
            }
        }

        if (selected is not null && selected.Type != ImageFileType.Directory)
        {
            return [selected.Path == path ? selected : selected with { Path = path }];
        }

        IEnumerable<ImageFileSystemEntry> results = entries.Values;
        if (showDeleted)
        {
            results = results.Concat(deletedEntries.Values);
        }

        string resolvedPath = selected?.Path ?? path;
        string prefix = resolvedPath.Length == 0 ? string.Empty : $"{resolvedPath}/";
        return results
            .Where(entry =>
            {
                if (!entry.Path.StartsWith(prefix, StringComparison.Ordinal) ||
                    entry.Path.Length == prefix.Length)
                {
                    return false;
                }

                string relative = entry.Path[prefix.Length..];
                return recursive || !relative.Contains('/');
            })
            .Select(entry => resolvedPath == path
                ? entry
                : entry with { Path = $"{path}/{entry.Path[prefix.Length..]}" })
            .OrderBy(entry => entry.Path, StringComparer.Ordinal)
            .ToArray();
    }

    public async Task CopyFileToAsync(
        string requestedPath,
        Stream destination,
        CancellationToken cancellationToken)
    {
        ImageFileSystemEntry entry = ResolveContentEntry(requestedPath);
        await CopyContentEntriesAsync(
            [(entry, destination)],
            cancellationToken);
    }

    public async Task ExtractAsync(
        string requestedPath,
        string outputPath,
        CancellationToken cancellationToken)
    {
        ExtractionPlan plan = CreateExtractionPlan(requestedPath, outputPath);
        ExtractionState state = new();
        try
        {
            if (plan.ExtractingRoot || plan.Source!.Type == ImageFileType.Directory)
            {
                Directory.CreateDirectory(plan.OutputPath);
                state.OutputCreated = true;
            }
            CreateExtractionSubdirectories(plan, cancellationToken);
            List<(ImageFileSystemEntry Entry, string Destination)> content =
                GetContentExtractionRequests(plan);
            state.OutputCreated |= content.Count > 0;
            await ExtractContentEntriesAsync(content, cancellationToken);
            CreatePreservedHardLinks(plan, state, cancellationToken);
            CreateSymbolicLinks(plan, state, cancellationToken);
            CreateSymbolicHardLinks(plan, state, cancellationToken);
            ApplyExtractionMetadata(plan);
        }
        catch (Exception exception)
        {
            CleanupFailedExtraction(plan, state.OutputCreated, exception);
            throw;
        }
    }

    private ExtractionPlan CreateExtractionPlan(string requestedPath, string outputPath)
    {
        string sourcePath = ImagePath.NormalizeRequested(requestedPath);
        bool extractingRoot = sourcePath.Length == 0;
        ImageFileSystemEntry? source = extractingRoot
            ? null
            : GetExtractionSource(sourcePath);
        if (source is not null)
        {
            sourcePath = source.Path;
        }

        string fullOutputPath = Path.GetFullPath(outputPath);
        ValidateNewDestination(fullOutputPath);
        string? missingParentRoot = GetMissingParentRoot(fullOutputPath);
        List<ImageFileSystemEntry> selected =
            SelectExtractionEntries(sourcePath, source, extractingRoot);
        ValidateExtractionEntries(selected);
        Dictionary<string, string> destinations = CreateExtractionDestinations(
            selected,
            sourcePath,
            fullOutputPath,
            extractingRoot);
        Dictionary<string, string> hardLinkTargets = GetExtractionHardLinkTargets(selected);
        HashSet<string> preservableHardLinks = GetPreservableHardLinks(
            selected,
            destinations,
            hardLinkTargets);
        return new(
            source,
            extractingRoot,
            fullOutputPath,
            missingParentRoot,
            selected,
            destinations,
            hardLinkTargets,
            preservableHardLinks);
    }

    private ImageFileSystemEntry GetExtractionSource(string sourcePath)
    {
        string lookupPath = ResolveParentComponents(sourcePath);
        if (!entries.TryGetValue(lookupPath, out ImageFileSystemEntry? source))
        {
            throw new FileNotFoundException(
                $"Path '/{sourcePath}' does not exist in the image.");
        }
        if (source.Type == ImageFileType.Other)
        {
            throw new NotSupportedException(
                $"Path '/{source.Path}' has unsupported file type '{source.Type}'.");
        }
        return source;
    }

    private List<ImageFileSystemEntry> SelectExtractionEntries(
        string sourcePath,
        ImageFileSystemEntry? source,
        bool extractingRoot) =>
        extractingRoot
            ? OrderExtractionEntries(entries.Values)
            : source!.Type == ImageFileType.Directory
                ? OrderExtractionEntries(entries.Values.Where(entry =>
                    entry.Path == sourcePath ||
                    entry.Path.StartsWith($"{sourcePath}/", StringComparison.Ordinal)))
                : [source];

    private static List<ImageFileSystemEntry> OrderExtractionEntries(
        IEnumerable<ImageFileSystemEntry> selected) =>
        selected
            .OrderBy(entry => entry.Path.Count(c => c == '/'))
            .ThenBy(entry => entry.Path, StringComparer.Ordinal)
            .ToList();

    private static void ValidateExtractionEntries(IEnumerable<ImageFileSystemEntry> selected)
    {
        ImageFileSystemEntry? unsupported = selected.FirstOrDefault(
            entry => entry.Type == ImageFileType.Other);
        if (unsupported is not null)
        {
            throw new NotSupportedException(
                $"Path '/{unsupported.Path}' has unsupported file type '{unsupported.Type}'.");
        }
    }

    private static Dictionary<string, string> CreateExtractionDestinations(
        IEnumerable<ImageFileSystemEntry> selected,
        string sourcePath,
        string outputPath,
        bool extractingRoot) =>
        selected.ToDictionary(
            entry => entry.Path,
            entry => !extractingRoot && entry.Path == sourcePath
                ? outputPath
                : GetContainedDestination(
                    outputPath,
                    extractingRoot
                        ? entry.Path
                        : entry.Path[(sourcePath.Length + 1)..]),
            StringComparer.Ordinal);

    private Dictionary<string, string> GetExtractionHardLinkTargets(
        IEnumerable<ImageFileSystemEntry> selected) =>
        selected
            .Where(entry => entry.Type == ImageFileType.HardLink)
            .Select(entry => (Entry: entry, Target: TryGetHardLinkTargetPath(entry)))
            .Where(item => item.Target is not null)
            .ToDictionary(
                item => item.Entry.Path,
                item => item.Target!,
                StringComparer.Ordinal);

    private HashSet<string> GetPreservableHardLinks(
        IEnumerable<ImageFileSystemEntry> selected,
        IReadOnlyDictionary<string, string> destinations,
        IReadOnlyDictionary<string, string> hardLinkTargets) =>
        selected
            .Where(entry =>
                entry.Type == ImageFileType.HardLink &&
                entry.ContentLinkTarget is null)
            .Where(entry =>
                hardLinkTargets.TryGetValue(entry.Path, out string? targetPath) &&
                destinations.ContainsKey(targetPath) &&
                entries.TryGetValue(targetPath, out ImageFileSystemEntry? target) &&
                target.ContentLayerIndex == entry.ContentLayerIndex &&
                target.ContentPath == entry.ContentPath &&
                target.ContentEntryIndex == entry.ContentEntryIndex)
            .Select(entry => entry.Path)
            .ToHashSet(StringComparer.Ordinal);

    private static void CreateExtractionSubdirectories(
        ExtractionPlan plan,
        CancellationToken cancellationToken)
    {
        foreach (ImageFileSystemEntry directory in plan.Entries
            .Where(entry => entry.Type == ImageFileType.Directory))
        {
            cancellationToken.ThrowIfCancellationRequested();
            Directory.CreateDirectory(plan.Destinations[directory.Path]);
        }
    }

    private static List<(ImageFileSystemEntry Entry, string Destination)>
        GetContentExtractionRequests(ExtractionPlan plan) =>
        plan.Entries
            .Where(entry =>
                entry.Type == ImageFileType.File ||
                (entry.Type == ImageFileType.HardLink &&
                    !plan.PreservableHardLinks.Contains(entry.Path) &&
                    entry.ContentLinkTarget is null))
            .Select(entry => (entry, plan.Destinations[entry.Path]))
            .ToList();

    private static void CreatePreservedHardLinks(
        ExtractionPlan plan,
        ExtractionState state,
        CancellationToken cancellationToken)
    {
        List<ImageFileSystemEntry> pending = plan.Entries
            .Where(entry =>
                entry.Type == ImageFileType.HardLink &&
                plan.PreservableHardLinks.Contains(entry.Path))
            .ToList();
        // Multiple passes allow hard-link chains whose immediate target has not been
        // materialized yet, without replacing them with independent file copies.
        while (pending.Count > 0)
        {
            int createdCount = 0;
            for (int index = pending.Count - 1; index >= 0; index--)
            {
                cancellationToken.ThrowIfCancellationRequested();
                ImageFileSystemEntry hardLink = pending[index];
                string target = plan.HardLinkTargets[hardLink.Path];
                if (!File.Exists(plan.Destinations[target]))
                {
                    continue;
                }
                FileHelper.CreateHardLink(
                    plan.Destinations[hardLink.Path],
                    plan.Destinations[target]);
                state.OutputCreated = true;
                pending.RemoveAt(index);
                createdCount++;
            }
            if (createdCount == 0)
            {
                throw new InvalidDataException(
                    $"Unable to create hard link '/{pending[0].Path}'.");
            }
        }
    }

    private void CreateSymbolicLinks(
        ExtractionPlan plan,
        ExtractionState state,
        CancellationToken cancellationToken)
    {
        foreach (ImageFileSystemEntry symbolicLink in plan.Entries
            .Where(entry => entry.Type == ImageFileType.SymbolicLink))
        {
            cancellationToken.ThrowIfCancellationRequested();
            string target = symbolicLink.LinkTarget ??
                throw new InvalidDataException(
                    $"Symbolic link '/{symbolicLink.Path}' has no target.");
            bool targetsDirectory = TryResolvePath(symbolicLink.Path)?.Type ==
                ImageFileType.Directory;
            FileHelper.CreateSymbolicLink(
                plan.Destinations[symbolicLink.Path],
                target,
                targetsDirectory);
            state.OutputCreated = true;
        }
    }

    private void CreateSymbolicHardLinks(
        ExtractionPlan plan,
        ExtractionState state,
        CancellationToken cancellationToken)
    {
        foreach (ImageFileSystemEntry hardLink in plan.Entries
            .Where(entry =>
                entry.Type == ImageFileType.HardLink &&
                entry.ContentLinkTarget is not null))
        {
            cancellationToken.ThrowIfCancellationRequested();
            string? targetPath = TryGetHardLinkTargetPath(hardLink);
            bool targetsDirectory = targetPath is not null &&
                TryResolvePath(targetPath)?.Type == ImageFileType.Directory;
            FileHelper.CreateSymbolicLink(
                plan.Destinations[hardLink.Path],
                hardLink.ContentLinkTarget!,
                targetsDirectory);
            state.OutputCreated = true;
        }
    }

    private static void ApplyExtractionMetadata(ExtractionPlan plan)
    {
        foreach (ImageFileSystemEntry entry in plan.Entries
            .Where(entry =>
                entry.Type is ImageFileType.File or ImageFileType.Directory ||
                (entry.Type == ImageFileType.HardLink &&
                    !plan.PreservableHardLinks.Contains(entry.Path) &&
                    entry.ContentLinkTarget is null))
            .OrderByDescending(entry => entry.Path.Count(c => c == '/')))
        {
            ApplyMetadata(plan.Destinations[entry.Path], entry);
        }
    }

    private static void CleanupFailedExtraction(
        ExtractionPlan plan,
        bool outputCreated,
        Exception exception)
    {
        // Preserve the extraction failure as the primary exception; cleanup failures
        // remain available as diagnostics without masking the original cause.
        if (outputCreated)
        {
            try
            {
                DeleteOutput(plan.OutputPath);
            }
            catch (Exception cleanupException)
            {
                exception.Data["ExtractionCleanupException"] = cleanupException;
            }
        }
        if (plan.MissingParentRoot is not null &&
            Directory.Exists(plan.MissingParentRoot))
        {
            try
            {
                Directory.Delete(plan.MissingParentRoot, recursive: true);
            }
            catch (Exception cleanupException)
            {
                exception.Data["ExtractionParentCleanupException"] = cleanupException;
            }
        }
    }

    private async Task BuildIndexAsync(CancellationToken cancellationToken)
    {
        for (int layerIndex = 0; layerIndex < manifest.Layers.Length; layerIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            IDescriptor layer = manifest.Layers[layerIndex];
            string digest = layer.Digest ??
                throw new InvalidDataException(
                    $"Layer digest not set for image '{imageName}'.");
            ImageLayerReference layerReference = new(layerIndex, digest);
            using Stream blob = await client.Blobs.GetAsync(
                imageName.Repo,
                digest,
                cancellationToken);
            LayerChanges changes = await ImageLayerScanner.ScanAsync(
                blob,
                layerReference,
                cancellationToken);
            ApplyLayer(changes, layerReference, cancellationToken);
        }
    }

    private void ApplyLayer(
        LayerChanges changes,
        ImageLayerReference layer,
        CancellationToken cancellationToken)
    {
        // OCI whiteouts affect only lower layers, and opaque markers take effect before
        // same-layer additions regardless of their position in the tar stream.
        foreach (string directory in changes.OpaqueDirectories)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RemovePath(directory, includePath: false, layer);
        }

        foreach (string path in changes.Whiteouts)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RemovePath(path, includePath: true, layer);
        }

        foreach (ScannedEntry scanned in changes.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            EnsureParentDirectories(scanned.Path, layer);

            if (entries.TryGetValue(scanned.Path, out ImageFileSystemEntry? previous) &&
                previous.Type == ImageFileType.Directory &&
                scanned.Type != ImageFileType.Directory)
            {
                RemovePath(scanned.Path, includePath: false, layer);
            }

            ImageLayerReference introduced =
                entries.TryGetValue(scanned.Path, out previous)
                    ? previous.IntroducedLayer
                    : layer;
            ImageLayerReference? modified =
                previous is not null && previous.IntroducedLayer.Index != layer.Index
                    ? layer
                    : previous?.ModifiedLayer;
            ImageFileSystemEntry current = scanned.ToEntry(introduced, modified);
            if (current.Type == ImageFileType.HardLink)
            {
                string targetPath = GetHardLinkTargetPath(current);
                if (!entries.TryGetValue(targetPath, out ImageFileSystemEntry? target) ||
                    target.Type == ImageFileType.Directory)
                {
                    throw new InvalidDataException(
                        $"Hard link '/{current.Path}' targets missing or invalid path '/{targetPath}'.");
                }
                current = current with
                {
                    Size = target.Size,
                    ContentLayerIndex = target.ContentLayerIndex,
                    ContentPath = target.ContentPath,
                    ContentEntryIndex = target.ContentEntryIndex,
                    ContentLinkTarget = target.Type == ImageFileType.SymbolicLink
                        ? target.LinkTarget
                        : target.ContentLinkTarget
                };
            }
            entries[scanned.Path] = current;
            deletedEntries.Remove(scanned.Path);
        }
    }

    private void EnsureParentDirectories(string path, ImageLayerReference layer)
    {
        string parent = ImagePath.GetDirectoryName(path);
        if (parent.Length == 0)
        {
            return;
        }

        EnsureParentDirectories(parent, layer);
        if (!entries.TryGetValue(parent, out ImageFileSystemEntry? entry) ||
            entry.Type != ImageFileType.Directory)
        {
            entries[parent] = new ImageFileSystemEntry
            {
                Path = parent,
                Type = ImageFileType.Directory,
                Mode = 0x1ED,
                IntroducedLayer = layer,
                ContentLayerIndex = layer.Index
            };
            deletedEntries.Remove(parent);
        }
    }

    private void RemovePath(
        string path,
        bool includePath,
        ImageLayerReference layer)
    {
        string prefix = $"{path}/";
        string[] affected = entries.Keys
            .Where(candidate =>
                (includePath && candidate == path) ||
                candidate.StartsWith(prefix, StringComparison.Ordinal))
            .ToArray();
        foreach (string candidate in affected)
        {
            ImageFileSystemEntry removed = entries[candidate] with
            {
                DeletedLayer = layer
            };
            entries.Remove(candidate);
            deletedEntries[candidate] = removed;
        }

        if (includePath && affected.Length == 0)
        {
            if (deletedEntries.TryGetValue(path, out ImageFileSystemEntry? alreadyDeleted))
            {
                deletedEntries[path] = alreadyDeleted with { DeletedLayer = layer };
            }
            else
            {
                deletedEntries[path] = new ImageFileSystemEntry
                {
                    Path = path,
                    Type = ImageFileType.Other,
                    IntroducedLayer = layer,
                    DeletedLayer = layer,
                    ContentLayerIndex = layer.Index
                };
            }
        }
    }

    private ImageFileSystemEntry ResolveContentEntry(string requestedPath)
    {
        string path = ImagePath.NormalizeRequested(requestedPath);
        if (path.Length == 0)
        {
            throw new InvalidDataException("The image root is a directory.");
        }

        ImageFileSystemEntry entry = ResolvePath(path);
        if (entry.Type == ImageFileType.Directory)
        {
            throw new InvalidDataException($"Path '/{path}' is a directory.");
        }
        if (entry.Type == ImageFileType.HardLink &&
            entry.ContentLinkTarget is string linkTarget)
        {
            string basePath = ImagePath.IsAbsolute(linkTarget)
                ? string.Empty
                : ImagePath.GetDirectoryName(entry.Path);
            string targetPath = ImagePath.ResolveLinkTarget(
                basePath,
                linkTarget,
                string.Empty,
                entry.Path);
            entry = ResolvePath(targetPath);
        }
        if (entry.Type == ImageFileType.HardLink)
        {
            return entry with { Type = ImageFileType.File };
        }
        if (entry.Type != ImageFileType.File)
        {
            throw new NotSupportedException(
                $"Path '/{path}' has unsupported file type '{entry.Type}'.");
        }
        return entry;
    }

    private ImageFileSystemEntry ResolvePath(string requestedPath)
    {
        string current = ImagePath.NormalizeRequested(requestedPath);
        for (int hop = 0; hop < MaximumLinkHops; hop++)
        {
            string[] segments = current.Split('/', StringSplitOptions.RemoveEmptyEntries);
            bool followedLink = false;
            for (int i = 0; i < segments.Length; i++)
            {
                string candidate = string.Join('/', segments.Take(i + 1));
                if (!entries.TryGetValue(candidate, out ImageFileSystemEntry? entry))
                {
                    throw new FileNotFoundException(
                        $"Path '/{requestedPath}' resolves to missing path '/{candidate}'.");
                }

                if (entry.Type != ImageFileType.SymbolicLink)
                {
                    continue;
                }

                string target = entry.LinkTarget ??
                    throw new InvalidDataException(
                        $"Link '/{candidate}' has no target.");
                string basePath = entry.Type == ImageFileType.SymbolicLink &&
                    !ImagePath.IsAbsolute(target)
                        ? ImagePath.GetDirectoryName(candidate)
                        : string.Empty;
                string remainder = string.Join('/', segments.Skip(i + 1));
                current = ImagePath.ResolveLinkTarget(basePath, target, remainder, candidate);
                followedLink = true;
                break;
            }

            if (!followedLink)
            {
                if (!entries.TryGetValue(current, out ImageFileSystemEntry? result))
                {
                    throw new FileNotFoundException(
                        $"Path '/{requestedPath}' does not exist in the image.");
                }
                return result;
            }
        }

        throw new InvalidDataException(
            $"Link resolution for '/{requestedPath}' exceeded {MaximumLinkHops} hops.");
    }

    private string ResolveParentComponents(string path)
    {
        string parentPath = ImagePath.GetDirectoryName(path);
        if (parentPath.Length == 0)
        {
            return path;
        }

        ImageFileSystemEntry parent = ResolvePath(parentPath);
        if (parent.Type != ImageFileType.Directory)
        {
            throw new InvalidDataException(
                $"Path '/{path}' has a non-directory parent '/{parent.Path}'.");
        }
        return $"{parent.Path}/{ImagePath.GetFileName(path)}";
    }

    private ImageFileSystemEntry? TryResolvePath(string requestedPath)
    {
        // Extraction must preserve dangling links; resolution here is only a best-effort
        // probe for choosing the host symlink type.
        try
        {
            return ResolvePath(requestedPath);
        }
        catch (FileNotFoundException)
        {
            return null;
        }
        catch (InvalidDataException)
        {
            return null;
        }
    }

    private string GetHardLinkTargetPath(ImageFileSystemEntry entry)
    {
        string target = entry.LinkTarget ??
            throw new InvalidDataException($"Hard link '/{entry.Path}' has no target.");
        string targetPath = ImagePath.ResolveLinkTarget(
            string.Empty,
            target,
            string.Empty,
            entry.Path);
        string parentPath = ImagePath.GetDirectoryName(targetPath);
        if (parentPath.Length == 0)
        {
            return targetPath;
        }

        ImageFileSystemEntry parent = ResolvePath(parentPath);
        if (parent.Type != ImageFileType.Directory)
        {
            throw new InvalidDataException(
                $"Hard link '/{entry.Path}' targets path '/{targetPath}' with a non-directory parent.");
        }
        return $"{parent.Path}/{ImagePath.GetFileName(targetPath)}";
    }

    private string? TryGetHardLinkTargetPath(ImageFileSystemEntry entry)
    {
        try
        {
            return GetHardLinkTargetPath(entry);
        }
        catch (FileNotFoundException)
        {
            return null;
        }
        catch (InvalidDataException)
        {
            return null;
        }
    }

    private async Task CopyContentEntriesAsync(
        IEnumerable<(ImageFileSystemEntry Entry, Stream Destination)> requests,
        CancellationToken cancellationToken)
    {
        List<(ImageFileSystemEntry Entry, Stream Destination)> requestList = requests.ToList();
        foreach (IGrouping<int, (ImageFileSystemEntry Entry, Stream Destination)> group in
            requestList.GroupBy(request => request.Entry.ContentLayerIndex))
        {
            cancellationToken.ThrowIfCancellationRequested();
            IDescriptor layer = manifest.Layers[group.Key];
            string digest = layer.Digest ??
                throw new InvalidDataException($"Layer {group.Key} has no digest.");
            Dictionary<(string Path, int EntryIndex), Queue<Stream>> destinations = group
                .GroupBy(request => (
                    Path: request.Entry.ContentPath ?? request.Entry.Path,
                    EntryIndex: request.Entry.ContentEntryIndex))
                .ToDictionary(
                    item => item.Key,
                    item => new Queue<Stream>(item.Select(request => request.Destination)));

            using Stream blob = await client.Blobs.GetAsync(
                imageName.Repo,
                digest,
                cancellationToken);
            using GZipStream gzip = new(blob, CompressionMode.Decompress, leaveOpen: true);
            using TarReader tar = new(gzip, leaveOpen: true);
            int entryIndex = 0;
            while (destinations.Count > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                TarEntry? tarEntry = await ImageTarReader.GetNextEntryAsync(
                    tar,
                    new(group.Key, digest),
                    cancellationToken);
                if (tarEntry is null)
                {
                    break;
                }

                int currentEntryIndex = entryIndex++;
                string path = ImagePath.NormalizeArchive(tarEntry.Name);
                if (!destinations.Remove(
                    (path, currentEntryIndex),
                    out Queue<Stream>? outputs))
                {
                    await ImageTarReader.DrainEntryAsync(
                        tarEntry,
                        new(group.Key, digest),
                        cancellationToken);
                    continue;
                }

                using MemoryStream? buffer = outputs.Count > 1 ? new MemoryStream() : null;
                Stream first = buffer ?? outputs.Dequeue();
                await ImageTarReader.CopyEntryAsync(
                    tarEntry,
                    first,
                    new(group.Key, digest),
                    path,
                    cancellationToken);
                if (buffer is not null)
                {
                    foreach (Stream output in outputs)
                    {
                        buffer.Position = 0;
                        await buffer.CopyToAsync(output, cancellationToken);
                    }
                }
            }

            if (destinations.Count > 0)
            {
                throw new InvalidDataException(
                    $"Could not locate effective content for '/{destinations.Keys.First().Path}' in layer {group.Key}.");
            }
        }
    }

    private async Task ExtractContentEntriesAsync(
        IEnumerable<(ImageFileSystemEntry Entry, string Destination)> requests,
        CancellationToken cancellationToken)
    {
        foreach (IGrouping<int, (ImageFileSystemEntry Entry, string Destination)> group in
            requests.GroupBy(request => request.Entry.ContentLayerIndex))
        {
            cancellationToken.ThrowIfCancellationRequested();
            IDescriptor layer = manifest.Layers[group.Key];
            string digest = layer.Digest ??
                throw new InvalidDataException($"Layer {group.Key} has no digest.");
            Dictionary<(string Path, int EntryIndex), Queue<string>> destinations = group
                .GroupBy(request => (
                    Path: request.Entry.ContentPath ?? request.Entry.Path,
                    EntryIndex: request.Entry.ContentEntryIndex))
                .ToDictionary(
                    item => item.Key,
                    item => new Queue<string>(item.Select(request => request.Destination)));

            using Stream blob = await client.Blobs.GetAsync(
                imageName.Repo,
                digest,
                cancellationToken);
            using GZipStream gzip = new(blob, CompressionMode.Decompress, leaveOpen: true);
            using TarReader tar = new(gzip, leaveOpen: true);
            int entryIndex = 0;
            while (destinations.Count > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                TarEntry? tarEntry = await ImageTarReader.GetNextEntryAsync(
                    tar,
                    new(group.Key, digest),
                    cancellationToken);
                if (tarEntry is null)
                {
                    break;
                }

                int currentEntryIndex = entryIndex++;
                string path = ImagePath.NormalizeArchive(tarEntry.Name);
                if (!destinations.Remove(
                    (path, currentEntryIndex),
                    out Queue<string>? outputs))
                {
                    await ImageTarReader.DrainEntryAsync(
                        tarEntry,
                        new(group.Key, digest),
                        cancellationToken);
                    continue;
                }

                string first = outputs.Dequeue();
                Directory.CreateDirectory(Path.GetDirectoryName(first)!);
                await using (FileStream destination = new(
                    first,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    bufferSize: 81920,
                    FileOptions.Asynchronous))
                {
                    await ImageTarReader.CopyEntryAsync(
                        tarEntry,
                        destination,
                        new(group.Key, digest),
                        path,
                        cancellationToken);
                }

                foreach (string output in outputs)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    Directory.CreateDirectory(Path.GetDirectoryName(output)!);
                    File.Copy(first, output);
                }
            }

            if (destinations.Count > 0)
            {
                throw new InvalidDataException(
                    $"Could not locate effective content for '/{destinations.Keys.First().Path}' in layer {group.Key}.");
            }
        }
    }

    private static void ValidateNewDestination(string outputPath)
    {
        if (PathExists(outputPath))
        {
            throw new IOException($"Destination '{outputPath}' already exists.");
        }
    }

    private static bool PathExists(string path)
    {
        try
        {
            _ = File.GetAttributes(path);
            return true;
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

    private static string? GetMissingParentRoot(string outputPath)
    {
        string? missingRoot = null;
        string? parent = Path.GetDirectoryName(outputPath);
        while (parent is not null && !PathExists(parent))
        {
            missingRoot = parent;
            parent = Path.GetDirectoryName(parent);
        }
        return missingRoot;
    }

    private static string GetContainedDestination(string root, string relativePath)
    {
        if (OperatingSystem.IsWindows())
        {
            ValidateWindowsDestinationPath(relativePath);
        }

        string result = Path.GetFullPath(
            Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        string relative = Path.GetRelativePath(root, result);
        if (Path.IsPathRooted(relative) ||
            relative == ".." ||
            relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Extraction path '{relativePath}' is outside the destination.");
        }
        return result;
    }

    private static void ValidateWindowsDestinationPath(string relativePath)
    {
        foreach (string segment in relativePath.Split('/'))
        {
            string stem = segment.Split('.')[0];
            if (segment.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
                segment.EndsWith('.') ||
                segment.EndsWith(' ') ||
                stem.Equals("CON", StringComparison.OrdinalIgnoreCase) ||
                stem.Equals("PRN", StringComparison.OrdinalIgnoreCase) ||
                stem.Equals("AUX", StringComparison.OrdinalIgnoreCase) ||
                stem.Equals("NUL", StringComparison.OrdinalIgnoreCase) ||
                stem.Equals("CONIN$", StringComparison.OrdinalIgnoreCase) ||
                stem.Equals("CONOUT$", StringComparison.OrdinalIgnoreCase) ||
                IsWindowsNumberedDevice(stem, "COM") ||
                IsWindowsNumberedDevice(stem, "LPT"))
            {
                throw new InvalidDataException(
                    $"Extraction path '{relativePath}' is not a valid Windows path.");
            }
        }
    }

    private static bool IsWindowsNumberedDevice(string value, string prefix) =>
        value.Length == prefix.Length + 1 &&
        value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
        value[^1] is >= '1' and <= '9';

    private static void ApplyMetadata(string path, ImageFileSystemEntry entry)
    {
        if (entry.ModifiedTime is DateTime modifiedTime)
        {
            DateTime utcModifiedTime = DateTime.SpecifyKind(modifiedTime, DateTimeKind.Utc);
            if (entry.Type == ImageFileType.Directory)
            {
                Directory.SetLastWriteTimeUtc(path, utcModifiedTime);
            }
            else
            {
                File.SetLastWriteTimeUtc(path, utcModifiedTime);
            }
        }

        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, (UnixFileMode)(entry.Mode & 0xFFF));
        }
    }

    private static void DeleteOutput(string path)
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }
        else if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    private sealed record ExtractionPlan(
        ImageFileSystemEntry? Source,
        bool ExtractingRoot,
        string OutputPath,
        string? MissingParentRoot,
        IReadOnlyList<ImageFileSystemEntry> Entries,
        IReadOnlyDictionary<string, string> Destinations,
        IReadOnlyDictionary<string, string> HardLinkTargets,
        IReadOnlySet<string> PreservableHardLinks);

    private sealed class ExtractionState
    {
        public bool OutputCreated { get; set; }
    }

}
