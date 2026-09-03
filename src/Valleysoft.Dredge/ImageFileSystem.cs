using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using System.Formats.Tar;
using System.IO.Compression;
using Valleysoft.DockerRegistryClient;
using Valleysoft.DockerRegistryClient.Models.Images;
using Valleysoft.DockerRegistryClient.Models.Manifests;
using Valleysoft.Dredge.Commands;

namespace Valleysoft.Dredge;

[JsonConverter(typeof(StringEnumConverter))]
public enum ImageFileType
{
    File,
    Directory,
    SymbolicLink,
    HardLink,
    Other
}

public sealed record ImageLayerReference(int Index, string Digest);

public sealed record ImageFileSystemEntry
{
    public required string Path { get; init; }
    public required ImageFileType Type { get; init; }
    public int Mode { get; init; }
    public int UserId { get; init; }
    public int GroupId { get; init; }
    public long Size { get; init; }
    public DateTime? ModifiedTime { get; init; }
    public string? LinkTarget { get; init; }
    public required ImageLayerReference IntroducedLayer { get; init; }
    public ImageLayerReference? ModifiedLayer { get; init; }
    public ImageLayerReference? DeletedLayer { get; init; }

    [JsonIgnore]
    internal int ContentLayerIndex { get; init; }

    [JsonIgnore]
    internal string? ContentPath { get; init; }

    [JsonIgnore]
    // The ordinal disambiguates duplicate paths in non-conforming layers so content reads
    // reopen the exact entry that produced the index metadata.
    internal int ContentEntryIndex { get; init; }

    [JsonIgnore]
    internal string? ContentLinkTarget { get; init; }

    [JsonIgnore]
    internal bool IsDeleted => DeletedLayer is not null;
}

internal sealed class ImageFileSystem
{
    private const string WhiteoutPrefix = ".wh.";
    private const string OpaqueWhiteout = ".wh..wh..opq";
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
        string sourcePath = ImagePath.NormalizeRequested(requestedPath);
        bool extractingRoot = sourcePath.Length == 0;
        ImageFileSystemEntry? source = null;
        if (!extractingRoot)
        {
            string lookupPath = ResolveParentComponents(sourcePath);
            if (!entries.TryGetValue(lookupPath, out source))
            {
                throw new FileNotFoundException(
                    $"Path '/{sourcePath}' does not exist in the image.");
            }
            sourcePath = source.Path;
        }
        if (source?.Type == ImageFileType.Other)
        {
            throw new NotSupportedException(
                $"Path '/{sourcePath}' has unsupported file type '{source.Type}'.");
        }

        string fullOutputPath = Path.GetFullPath(outputPath);
        ValidateNewDestination(fullOutputPath);
        string? missingParentRoot = GetMissingParentRoot(fullOutputPath);

        List<ImageFileSystemEntry> selected = extractingRoot
            ? entries.Values
                .OrderBy(entry => entry.Path.Count(c => c == '/'))
                .ThenBy(entry => entry.Path, StringComparer.Ordinal)
                .ToList()
            : source!.Type == ImageFileType.Directory
            ? entries.Values
                .Where(entry =>
                    entry.Path == sourcePath ||
                    entry.Path.StartsWith($"{sourcePath}/", StringComparison.Ordinal))
                .OrderBy(entry => entry.Path.Count(c => c == '/'))
                .ThenBy(entry => entry.Path, StringComparer.Ordinal)
                .ToList()
            : [source];
        ImageFileSystemEntry? unsupported = selected.FirstOrDefault(
            entry => entry.Type == ImageFileType.Other);
        if (unsupported is not null)
        {
            throw new NotSupportedException(
                $"Path '/{unsupported.Path}' has unsupported file type '{unsupported.Type}'.");
        }

        Dictionary<string, string> destinations = selected.ToDictionary(
            entry => entry.Path,
            entry => !extractingRoot && entry.Path == sourcePath
                ? fullOutputPath
                : GetContainedDestination(
                    fullOutputPath,
                    extractingRoot
                        ? entry.Path
                        : entry.Path[(sourcePath.Length + 1)..]),
            StringComparer.Ordinal);

        Dictionary<string, string> hardLinkTargets = selected
            .Where(entry => entry.Type == ImageFileType.HardLink)
            .Select(entry => (Entry: entry, Target: TryGetHardLinkTargetPath(entry)))
            .Where(item => item.Target is not null)
            .ToDictionary(
                item => item.Entry.Path,
                item => item.Target!,
                StringComparer.Ordinal);
        HashSet<string> preservableHardLinks = selected
            .Where(entry =>
                entry.Type == ImageFileType.HardLink &&
                entry.ContentLinkTarget is null)
            .Where(entry =>
            {
                return hardLinkTargets.TryGetValue(entry.Path, out string? targetPath) &&
                    destinations.ContainsKey(targetPath) &&
                    entries.TryGetValue(targetPath, out ImageFileSystemEntry? target) &&
                    target.ContentLayerIndex == entry.ContentLayerIndex &&
                    target.ContentPath == entry.ContentPath &&
                    target.ContentEntryIndex == entry.ContentEntryIndex;
            })
            .Select(entry => entry.Path)
            .ToHashSet(StringComparer.Ordinal);

        bool outputCreated = false;
        try
        {
            if (extractingRoot || source!.Type == ImageFileType.Directory)
            {
                Directory.CreateDirectory(fullOutputPath);
                outputCreated = true;
            }

            foreach (ImageFileSystemEntry directory in selected
                .Where(entry => entry.Type == ImageFileType.Directory))
            {
                cancellationToken.ThrowIfCancellationRequested();
                Directory.CreateDirectory(destinations[directory.Path]);
            }

            List<(ImageFileSystemEntry Entry, string Destination)> content = selected
                .Where(entry =>
                    entry.Type == ImageFileType.File ||
                    (entry.Type == ImageFileType.HardLink &&
                        !preservableHardLinks.Contains(entry.Path) &&
                        entry.ContentLinkTarget is null))
                .Select(entry => (entry, destinations[entry.Path]))
                .ToList();
            outputCreated |= content.Count > 0;
            await ExtractContentEntriesAsync(content, cancellationToken);

            List<ImageFileSystemEntry> pendingHardLinks = selected
                .Where(entry =>
                    entry.Type == ImageFileType.HardLink &&
                    preservableHardLinks.Contains(entry.Path))
                .ToList();
            // Multiple passes allow hard-link chains whose immediate target has not been
            // materialized yet, without replacing them with independent file copies.
            while (pendingHardLinks.Count > 0)
            {
                int createdCount = 0;
                for (int index = pendingHardLinks.Count - 1; index >= 0; index--)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    ImageFileSystemEntry hardLink = pendingHardLinks[index];
                    string target = hardLinkTargets[hardLink.Path];
                    if (!File.Exists(destinations[target]))
                    {
                        continue;
                    }
                    FileHelper.CreateHardLink(destinations[hardLink.Path], destinations[target]);
                    pendingHardLinks.RemoveAt(index);
                    createdCount++;
                    outputCreated = true;
                }
                if (createdCount == 0)
                {
                    throw new InvalidDataException(
                        $"Unable to create hard link '/{pendingHardLinks[0].Path}'.");
                }
            }

            foreach (ImageFileSystemEntry symbolicLink in selected
                .Where(entry => entry.Type == ImageFileType.SymbolicLink))
            {
                cancellationToken.ThrowIfCancellationRequested();
                string destination = destinations[symbolicLink.Path];
                string target = symbolicLink.LinkTarget ??
                    throw new InvalidDataException(
                        $"Symbolic link '/{symbolicLink.Path}' has no target.");
                bool targetsDirectory = TryResolvePath(symbolicLink.Path)?.Type ==
                    ImageFileType.Directory;
                FileHelper.CreateSymbolicLink(destination, target, targetsDirectory);
                outputCreated = true;
            }

            foreach (ImageFileSystemEntry hardLink in selected
                .Where(entry =>
                    entry.Type == ImageFileType.HardLink &&
                    entry.ContentLinkTarget is not null))
            {
                cancellationToken.ThrowIfCancellationRequested();
                string? targetPath = TryGetHardLinkTargetPath(hardLink);
                bool targetsDirectory = targetPath is not null &&
                    TryResolvePath(targetPath)?.Type ==
                    ImageFileType.Directory;
                FileHelper.CreateSymbolicLink(
                    destinations[hardLink.Path],
                    hardLink.ContentLinkTarget!,
                    targetsDirectory);
                outputCreated = true;
            }

            foreach (ImageFileSystemEntry entry in selected
                .Where(entry =>
                    entry.Type is ImageFileType.File or ImageFileType.Directory ||
                    (entry.Type == ImageFileType.HardLink &&
                        !preservableHardLinks.Contains(entry.Path) &&
                        entry.ContentLinkTarget is null))
                .OrderByDescending(entry => entry.Path.Count(c => c == '/')))
            {
                ApplyMetadata(destinations[entry.Path], entry);
            }
        }
        catch (Exception exception)
        {
            // Preserve the extraction failure as the primary exception; cleanup failures
            // remain available as diagnostics without masking the original cause.
            if (outputCreated)
            {
                try
                {
                    DeleteOutput(fullOutputPath);
                }
                catch (Exception cleanupException)
                {
                    exception.Data["ExtractionCleanupException"] = cleanupException;
                }
            }
            if (missingParentRoot is not null && Directory.Exists(missingParentRoot))
            {
                try
                {
                    Directory.Delete(missingParentRoot, recursive: true);
                }
                catch (Exception cleanupException)
                {
                    exception.Data["ExtractionParentCleanupException"] = cleanupException;
                }
            }
            throw;
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
            LayerChanges changes = await ScanLayerAsync(
                blob,
                layerReference,
                cancellationToken);
            ApplyLayer(changes, layerReference, cancellationToken);
        }
    }

    private static async Task<LayerChanges> ScanLayerAsync(
        Stream blob,
        ImageLayerReference layer,
        CancellationToken cancellationToken)
    {
        List<ScannedEntry> entries = [];
        List<string> whiteouts = [];
        List<string> opaqueDirectories = [];

        using GZipStream gzip = new(blob, CompressionMode.Decompress, leaveOpen: true);
        using TarReader tar = new(gzip, leaveOpen: true);
        int entryIndex = 0;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            TarEntry? tarEntry = await GetNextTarEntryAsync(
                tar,
                layer,
                cancellationToken);
            if (tarEntry is null)
            {
                break;
            }
            int currentEntryIndex = entryIndex++;
            await DrainTarEntryAsync(tarEntry, layer, cancellationToken);

            string path = ImagePath.NormalizeArchive(tarEntry.Name);
            if (path.Length == 0)
            {
                continue;
            }

            string fileName = ImagePath.GetFileName(path);
            string parent = ImagePath.GetDirectoryName(path);
            if (string.Equals(fileName, OpaqueWhiteout, StringComparison.Ordinal))
            {
                if (parent.Length == 0)
                {
                    throw new InvalidDataException(
                        "The opaque whiteout marker cannot exist at the image root.");
                }
                opaqueDirectories.Add(parent);
                continue;
            }

            if (fileName.StartsWith(WhiteoutPrefix, StringComparison.Ordinal))
            {
                string targetName = fileName[WhiteoutPrefix.Length..];
                ImagePath.ValidateSegment(targetName, "whiteout target");
                whiteouts.Add(
                    parent.Length == 0 ? targetName : $"{parent}/{targetName}");
                continue;
            }

            ImageFileType type = GetFileType(tarEntry);
            string? linkTarget = type is ImageFileType.SymbolicLink or ImageFileType.HardLink
                ? tarEntry.LinkName
                : null;
            if (linkTarget is not null && ImagePath.ContainsControlCharacter(linkTarget))
            {
                throw new InvalidDataException(
                    $"Link '/{path}' has an invalid target.");
            }
            if (type == ImageFileType.SymbolicLink)
            {
                string basePath = ImagePath.IsAbsolute(linkTarget!)
                    ? string.Empty
                    : parent;
                _ = ImagePath.ResolveLinkTarget(
                    basePath,
                    linkTarget!,
                    string.Empty,
                    path);
            }

            entries.Add(new ScannedEntry(
                path,
                type,
                (int)tarEntry.Mode,
                tarEntry.Uid,
                tarEntry.Gid,
                type == ImageFileType.File ? tarEntry.Length : 0,
                tarEntry.ModificationTime.UtcDateTime,
                linkTarget,
                currentEntryIndex,
                layer));
        }
        return new LayerChanges(entries, whiteouts, opaqueDirectories);
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
                TarEntry? tarEntry = await GetNextTarEntryAsync(
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
                    await DrainTarEntryAsync(
                        tarEntry,
                        new(group.Key, digest),
                        cancellationToken);
                    continue;
                }

                using MemoryStream? buffer = outputs.Count > 1 ? new MemoryStream() : null;
                Stream first = buffer ?? outputs.Dequeue();
                await CopyTarEntryAsync(
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
                TarEntry? tarEntry = await GetNextTarEntryAsync(
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
                    await DrainTarEntryAsync(
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
                    await CopyTarEntryAsync(
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

    private static ImageFileType GetFileType(TarEntry entry) =>
        entry.EntryType switch
        {
            TarEntryType.RegularFile or
                TarEntryType.V7RegularFile or
                TarEntryType.ContiguousFile => ImageFileType.File,
            TarEntryType.SymbolicLink => ImageFileType.SymbolicLink,
            TarEntryType.HardLink => ImageFileType.HardLink,
            TarEntryType.Directory => ImageFileType.Directory,
            _ => ImageFileType.Other
        };

    private static async ValueTask<TarEntry?> GetNextTarEntryAsync(
        TarReader reader,
        ImageLayerReference layer,
        CancellationToken cancellationToken)
    {
        try
        {
            return await reader.GetNextEntryAsync(
                copyData: false,
                cancellationToken);
        }
        catch (Exception exception) when (
            exception is InvalidDataException or NotSupportedException)
        {
            throw CreateInvalidLayerException(layer, exception);
        }
    }

    private static async Task DrainTarEntryAsync(
        TarEntry entry,
        ImageLayerReference layer,
        CancellationToken cancellationToken)
    {
        if (entry.DataStream is null)
        {
            return;
        }

        // On .NET 9, GetNextEntryAsync(copyData: false) does not reliably advance past
        // skipped data, so every unconsumed entry must be drained before reading the next.
        try
        {
            await entry.DataStream.CopyToAsync(Stream.Null, cancellationToken);
        }
        catch (Exception exception) when (
            exception is InvalidDataException or NotSupportedException)
        {
            throw CreateInvalidLayerException(layer, exception);
        }
    }

    private static async Task CopyTarEntryAsync(
        TarEntry entry,
        Stream destination,
        ImageLayerReference layer,
        string path,
        CancellationToken cancellationToken)
    {
        Stream data = entry.DataStream ??
            throw new InvalidDataException($"File '/{path}' has no content stream.");
        try
        {
            await data.CopyToAsync(destination, cancellationToken);
        }
        catch (Exception exception) when (
            exception is InvalidDataException or NotSupportedException)
        {
            throw CreateInvalidLayerException(layer, exception);
        }
    }

    private static InvalidDataException CreateInvalidLayerException(
        ImageLayerReference layer,
        Exception innerException) =>
        new(
            $"Layer {layer.Index} ('{layer.Digest}') is not a supported gzip-compressed Linux tar layer.",
            innerException);

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

    private sealed record LayerChanges(
        IReadOnlyList<ScannedEntry> Entries,
        IReadOnlyList<string> Whiteouts,
        IReadOnlyList<string> OpaqueDirectories);

    private sealed record ScannedEntry(
        string Path,
        ImageFileType Type,
        int Mode,
        int UserId,
        int GroupId,
        long Size,
        DateTime ModifiedTime,
        string? LinkTarget,
        int EntryIndex,
        ImageLayerReference Layer)
    {
        public ImageFileSystemEntry ToEntry(
            ImageLayerReference introduced,
            ImageLayerReference? modified) =>
            new()
            {
                Path = Path,
                Type = Type,
                Mode = Mode,
                UserId = UserId,
                GroupId = GroupId,
                Size = Size,
                ModifiedTime = ModifiedTime,
                LinkTarget = LinkTarget,
                IntroducedLayer = introduced,
                ModifiedLayer = modified,
                ContentLayerIndex = Layer.Index,
                ContentPath = Type == ImageFileType.File ? Path : null,
                ContentEntryIndex = EntryIndex
            };
    }
}

internal static class ImagePath
{
    public static string NormalizeArchive(string value)
    {
        if (string.IsNullOrEmpty(value) || ContainsControlCharacter(value))
        {
            throw new InvalidDataException($"Invalid archive entry path '{value}'.");
        }
        if (IsAbsolute(value) || value.Contains('\\'))
        {
            throw new InvalidDataException(
                $"Archive entry path '{value}' must be a relative Linux path.");
        }
        return Normalize(value, allowParentSegments: false, "archive entry");
    }

    public static string NormalizeRequested(string? value)
    {
        value = string.IsNullOrEmpty(value) ? string.Empty : value;
        if (value.Contains('\\') || ContainsControlCharacter(value))
        {
            throw new InvalidDataException($"Invalid image path '{value}'.");
        }
        return Normalize(value.TrimStart('/'), allowParentSegments: false, "image");
    }

    public static string ResolveLinkTarget(
        string basePath,
        string target,
        string remainder,
        string linkPath)
    {
        if (target.Contains('\\') || ContainsControlCharacter(target))
        {
            throw new InvalidDataException(
                $"Link '/{linkPath}' has invalid target '{target}'.");
        }

        string combined = IsAbsolute(target)
            ? target.TrimStart('/')
            : Join(basePath, target);
        combined = Join(combined, remainder);
        return Normalize(combined, allowParentSegments: true, $"link target for '/{linkPath}'");
    }

    public static bool IsAbsolute(string path) => path.StartsWith('/');

    public static string GetDirectoryName(string path)
    {
        int separator = path.LastIndexOf('/');
        return separator < 0 ? string.Empty : path[..separator];
    }

    public static string GetFileName(string path)
    {
        int separator = path.LastIndexOf('/');
        return separator < 0 ? path : path[(separator + 1)..];
    }

    public static void ValidateSegment(string value, string description)
    {
        if (string.IsNullOrEmpty(value) ||
            value is "." or ".." ||
            value.Contains('/') ||
            value.Contains('\\') ||
            ContainsControlCharacter(value))
        {
            throw new InvalidDataException($"Invalid {description} '{value}'.");
        }
    }

    private static string Normalize(
        string value,
        bool allowParentSegments,
        string description)
    {
        List<string> segments = [];
        foreach (string segment in value.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (segment == ".")
            {
                continue;
            }
            if (segment == "..")
            {
                if (!allowParentSegments)
                {
                    throw new InvalidDataException(
                        $"The {description} path '{value}' escapes the image root.");
                }
                if (segments.Count > 0)
                {
                    segments.RemoveAt(segments.Count - 1);
                }
                // Linux clamps excess ".." segments at the filesystem root.
                continue;
            }
            ValidateSegment(segment, $"{description} path segment");
            segments.Add(segment);
        }
        return string.Join('/', segments);
    }

    private static string Join(string first, string second) =>
        first.Length == 0 ? second :
        second.Length == 0 ? first :
        $"{first}/{second}";

    internal static bool ContainsControlCharacter(string value) =>
        value.Any(char.IsControl);
}
