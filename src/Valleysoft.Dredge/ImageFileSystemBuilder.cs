namespace Valleysoft.Dredge;

using Valleysoft.DockerRegistryClient;
using Valleysoft.DockerRegistryClient.Models.Images;
using Valleysoft.DockerRegistryClient.Models.Manifests;

internal sealed class ImageFileSystemBuilder
{
    private readonly IImageContentSource content;
    private readonly ImageName imageName;
    private readonly IImageManifest manifest;
    private readonly LayerStore store;
    private readonly Dictionary<int, StoredLayerIndex> indexes;
    private readonly Dictionary<string, ImageFileSystemEntry> entries;
    private readonly Dictionary<string, ImageFileSystemEntry> deletedEntries;
    private readonly ImagePathResolver pathResolver;
    private readonly ImageFileSystemSemantics semantics;
    private readonly WindowsAlternateStreams? alternateStreams;
    private readonly SortedSet<string>? paths;
    private readonly Dictionary<ImageContentId, ImageContentPaths>? contentPaths;
    private readonly HashSet<ImageContentId>? unreferencedContents;

    public IReadOnlyList<string>? ExpectedDiffIds { get; set; }

    public ImageFileSystemBuilder(
        IImageContentSource content,
        ImageName imageName,
        IImageManifest manifest,
        LayerStore store,
        Dictionary<int, StoredLayerIndex> indexes,
        Dictionary<string, ImageFileSystemEntry> entries,
        Dictionary<string, ImageFileSystemEntry> deletedEntries,
        ImageFileSystemSemantics? semantics = null)
    {
        this.content = content;
        this.imageName = imageName;
        this.manifest = manifest;
        this.store = store;
        this.indexes = indexes;
        this.entries = entries;
        this.deletedEntries = deletedEntries;
        this.semantics = semantics ?? ImageFileSystemSemantics.Linux;
        alternateStreams = this.semantics.IsWindows ? new() : null;
        if (this.semantics.IsWindows)
        {
            paths = new(entries.Keys, this.semantics.Comparer);
            contentPaths = [];
            unreferencedContents = [];
            foreach (ImageFileSystemEntry entry in entries.Values) TrackContentPath(entry);
        }
        this.pathResolver = new(entries, this.semantics);
    }

    public ImageFileSystemBuilder(
        IDockerRegistryClient client,
        ImageName imageName,
        IImageManifest manifest,
        LayerStore store,
        Dictionary<int, StoredLayerIndex> indexes,
        Dictionary<string, ImageFileSystemEntry> entries,
        Dictionary<string, ImageFileSystemEntry> deletedEntries,
        ImageFileSystemSemantics? semantics = null)
        : this(new RegistryImageContentSource(client, imageName), imageName, manifest, store,
            indexes, entries, deletedEntries, semantics)
    {
    }

    public async Task<bool> BuildAsync(
        string? contentPath,
        bool extracting,
        CancellationToken cancellationToken,
        IProgress<ImageIndexProgress>? progress = null,
        int? maxLayerIndex = null,
        bool allowMissingContent = false)
    {
        string normalizedPath = semantics.NormalizeRequested(contentPath);
        if (!semantics.IsWindows && normalizedPath.Length > 0)
        {
            for (int firstLayer = manifest.Layers.Length - 1; firstLayer >= 0; firstLayer--)
            {
                await GetIndexAsync(firstLayer, cancellationToken);
                entries.Clear();
                deletedEntries.Clear();
                try
                {
                    for (int i = firstLayer; i < manifest.Layers.Length; i++)
                    {
                        ApplyLayer(indexes[i].Changes, new(i, indexes[i].Digest), cancellationToken);
                    }
                    try
                    {
                        if (!extracting || pathResolver.GetExtractionSource(normalizedPath, entries).Type == ImageFileType.File)
                        {
                            _ = pathResolver.ResolveContentEntry(normalizedPath, entries);
                            return firstLayer == 0;
                        }
                    }
                    catch (FileNotFoundException) when (firstLayer == 0 && allowMissingContent)
                    {
                        return true;
                    }
                }
                catch (Exception exception) when (firstLayer > 0 &&
                    exception is FileNotFoundException or InvalidDataException)
                {
                    // A suffix cannot resolve hard-link snapshots or parent links supplied by older layers.
                }
            }
            return true;
        }

        int lastLayer = maxLayerIndex ?? (manifest.Layers.Length - 1);
        for (int i = 0; i <= lastLayer; i++)
        {
            int layer = i;
            IProgress<long>? bytes = progress is null ? null :
                new InlineProgress<long>(value => progress.Report(
                    new ImageIndexProgress(layer, manifest.Layers.Length, value, false)));
            StoredLayerIndex index = await GetIndexAsync(i, cancellationToken, bytes);
            ApplyLayer(index.Changes, new(i, index.Digest), cancellationToken);
            progress?.Report(new ImageIndexProgress(i, manifest.Layers.Length, index.BlobLength, true));
        }
        return maxLayerIndex is null;
    }

    public async Task<StoredLayerIndex> GetIndexAsync(int layerIndex, CancellationToken cancellationToken,
        IProgress<long>? progress = null)
    {
        if (!indexes.TryGetValue(layerIndex, out StoredLayerIndex? index))
        {
            IDescriptor descriptor = manifest.Layers[layerIndex];
            string digest = descriptor.Digest ??
                throw new InvalidDataException($"Layer digest not set for image '{imageName}'.");
            string? expectedDiffId = ExpectedDiffIds is { } diffIds && diffIds.Count == manifest.Layers.Length
                ? diffIds[layerIndex]
                : null;
            index = await store.GetIndexAsync(content, new(layerIndex, digest),
                descriptor.Size, cancellationToken, progress, expectedDiffId, semantics);
            indexes.Add(layerIndex, index);
        }

        return index;
    }

    public void ApplyLayer(
        LayerChanges changes,
        ImageLayerReference layer,
        CancellationToken cancellationToken)
    {
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
            if (previous is not null &&
                (previous.Type != ImageFileType.Directory || scanned.Type != ImageFileType.Directory))
            {
                RemoveOwnerStreams(scanned.Path, layer);
            }
            ImageFileSystemEntry current = scanned.ToEntry(introduced, modified, layer);
            if (current.Type == ImageFileType.HardLink)
            {
                string targetPath = pathResolver.GetHardLinkTargetPath(current, entries);
                entries.TryGetValue(targetPath, out ImageFileSystemEntry? foundTarget);
                ImageFileSystemSemantics.ValidateHardLinkTarget(current.Path, targetPath, foundTarget?.Type);
                ImageFileSystemEntry target = foundTarget!;
                current = current with
                {
                    Type = semantics.IsWindows && target.Type == ImageFileType.Other
                        ? ImageFileType.Other : current.Type,
                    Windows = semantics.IsWindows && target.Type == ImageFileType.Other
                        ? ImageFileSystemSemantics.UnsupportedHardLink(targetPath, current.Windows) : current.Windows,
                    Size = target.Size,
                    ContentLayerIndex = target.ContentLayerIndex,
                    ContentPath = target.ContentPath,
                    ContentEntryIndex = target.ContentEntryIndex,
                    ContentLinkTarget = target.Type == ImageFileType.SymbolicLink
                        ? target.LinkTarget
                        : target.ContentLinkTarget
                };
            }
            if (alternateStreams is not null && current.Windows is not null &&
                current.Type is ImageFileType.File or ImageFileType.HardLink &&
                current.Windows.HasAlternateStreams != alternateStreams.HasStreams(current.ContentId))
            {
                current = current with
                {
                    Windows = current.Windows with { HasAlternateStreams = alternateStreams.HasStreams(current.ContentId) }
                };
            }
            SetEntry(current);
            deletedEntries.Remove(scanned.Path);
            if (alternateStreams is not null && scanned.Path.Contains(':'))
            {
                alternateStreams.Track(scanned.Path, GetStreamOwner(scanned.Path));
            }
        }
        if (alternateStreams is not null)
        {
            foreach (ScannedEntry scanned in changes.Entries)
            {
                if (entries.TryGetValue(scanned.Path, out ImageFileSystemEntry? owner) &&
                    owner.Type is ImageFileType.File or ImageFileType.HardLink && owner.ContentLinkTarget is null)
                {
                    alternateStreams.BindPending(owner.Path, owner.ContentId);
                }
            }
            // A replacement can briefly remove its own last alias before capturing the same content again.
            foreach (ImageContentId content in unreferencedContents!)
            {
                if (!contentPaths!.ContainsKey(content)) alternateStreams.ForgetContent(content);
            }
            unreferencedContents!.Clear();
            foreach (ImageContentId content in alternateStreams.TakePresenceChanges())
            {
                bool hasStreams = alternateStreams.HasStreams(content);
                if (!contentPaths!.TryGetValue(content, out ImageContentPaths aliases)) continue;
                foreach (string path in aliases)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    ImageFileSystemEntry entry = entries[path];
                    if (entry.Windows is not null && entry.Windows.HasAlternateStreams != hasStreams)
                    {
                        entries[path] = entry with
                        {
                            Windows = entry.Windows with { HasAlternateStreams = hasStreams },
                            ModifiedLayer = entry.IntroducedLayer.Index < layer.Index ? layer : entry.ModifiedLayer
                        };
                    }
                }
            }
        }
    }

    private ImageContentId? GetStreamOwner(string path)
    {
        string basePath = WindowsAlternateStreams.BasePath(path);
        if (entries.TryGetValue(basePath, out ImageFileSystemEntry? owner) &&
            owner.Type is ImageFileType.File or ImageFileType.HardLink && owner.ContentLinkTarget is null)
        {
            return owner.ContentId;
        }
        return null;
    }

    private void EnsureParentDirectories(string path, ImageLayerReference layer)
    {
        string parent = ImagePath.GetDirectoryName(path);
        if (parent.Length == 0)
        {
            return;
        }

        EnsureParentDirectories(parent, layer);
        entries.TryGetValue(parent, out ImageFileSystemEntry? entry);
        semantics.ValidateParent(path, parent, entry?.Type);
        if (entry is null ||
            entry.Type != ImageFileType.Directory)
        {
            if (entry is not null) RemoveOwnerStreams(parent, layer);
            SetEntry(new ImageFileSystemEntry
            {
                Path = parent,
                Type = ImageFileType.Directory,
                Mode = 0x1ED,
                Windows = semantics.IsWindows ? new(null, false, false, "", null) : null,
                IntroducedLayer = layer,
                ContentLayerIndex = layer.Index
            });
            deletedEntries.Remove(parent);
        }
    }

    private void RemovePath(
        string path,
        bool includePath,
        ImageLayerReference layer)
    {
        string[]? removedStreamPaths = null;
        if (includePath && path.Contains(':') && alternateStreams is not null)
        {
            removedStreamPaths = alternateStreams.DeleteStream(path, GetStreamOwner(path));
        }
        else if (includePath)
        {
            RemoveOwnerStreams(path, layer);
        }
        string prefix = $"{path}/";
        string[] affected;
        if (paths is not null)
        {
            HashSet<string> selected = new(semantics.Descendants(paths, path), semantics.Comparer);
            if (includePath && entries.ContainsKey(path)) selected.Add(path);
            if (removedStreamPaths is not null)
            {
                foreach (string stream in removedStreamPaths)
                {
                    if (entries.ContainsKey(stream)) selected.Add(stream);
                }
            }
            affected = [.. selected];
        }
        else
        {
            affected = [.. entries.Keys.Where(candidate =>
                (includePath && semantics.Comparer.Equals(candidate, path)) ||
                semantics.StartsWith(candidate, prefix))];
        }
        foreach (string candidate in affected)
        {
            RemoveOwnerStreams(candidate, layer);
            RemoveEntry(candidate, layer);
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
                    Windows = semantics.IsWindows ? new(null, false, false, "", null) : null,
                    IntroducedLayer = layer,
                    DeletedLayer = layer,
                    ContentLayerIndex = layer.Index
                };
            }
        }
    }

    private void RemoveOwnerStreams(string path, ImageLayerReference layer)
    {
        if (alternateStreams is null) return;
        foreach (string stream in alternateStreams.PathsForOwner(path))
        {
            RemoveEntry(stream, layer);
        }
    }

    private void RemoveEntry(string path, ImageLayerReference layer)
    {
        if (entries.Remove(path, out ImageFileSystemEntry? entry))
        {
            paths?.Remove(path);
            ForgetContentPath(entry);
            deletedEntries[path] = entry with { DeletedLayer = layer };
        }
        alternateStreams?.Forget(path);
    }

    private void SetEntry(ImageFileSystemEntry entry)
    {
        if (entries.TryGetValue(entry.Path, out ImageFileSystemEntry? previous)) ForgetContentPath(previous);
        entries[entry.Path] = entry;
        paths?.Add(entry.Path);
        TrackContentPath(entry);
    }

    private void TrackContentPath(ImageFileSystemEntry entry)
    {
        if (contentPaths is null || entry.Type is not (ImageFileType.File or ImageFileType.HardLink)) return;
        contentPaths.TryGetValue(entry.ContentId, out ImageContentPaths aliases);
        aliases.Add(entry.Path, semantics.Comparer);
        contentPaths[entry.ContentId] = aliases;
    }

    private void ForgetContentPath(ImageFileSystemEntry entry)
    {
        if (contentPaths is null || entry.Type is not (ImageFileType.File or ImageFileType.HardLink)) return;
        ImageContentPaths aliases = contentPaths[entry.ContentId];
        aliases.Remove(entry.Path, semantics.Comparer);
        if (aliases.Count == 0)
        {
            contentPaths.Remove(entry.ContentId);
            unreferencedContents!.Add(entry.ContentId);
        }
        else
        {
            contentPaths[entry.ContentId] = aliases;
        }
    }

}

internal sealed record ImageIndexProgress(int LayerIndex, int LayerCount, long Bytes, bool Indexed);

internal sealed class InlineProgress<T>(Action<T> action) : IProgress<T>
{
    public void Report(T value) => action(value);
}
