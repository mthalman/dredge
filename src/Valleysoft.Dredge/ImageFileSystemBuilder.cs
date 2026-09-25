namespace Valleysoft.Dredge;

using Valleysoft.DockerRegistryClient;
using Valleysoft.DockerRegistryClient.Models.Images;
using Valleysoft.DockerRegistryClient.Models.Manifests;

internal sealed class ImageFileSystemBuilder
{
    private readonly IDockerRegistryClient client;
    private readonly ImageName imageName;
    private readonly IImageManifest manifest;
    private readonly LayerStore store;
    private readonly Dictionary<int, StoredLayerIndex> indexes;
    private readonly Dictionary<string, ImageFileSystemEntry> entries;
    private readonly Dictionary<string, ImageFileSystemEntry> deletedEntries;
    private readonly ImagePathResolver pathResolver;

    public ImageFileSystemBuilder(
        IDockerRegistryClient client,
        ImageName imageName,
        IImageManifest manifest,
        LayerStore store,
        Dictionary<int, StoredLayerIndex> indexes,
        Dictionary<string, ImageFileSystemEntry> entries,
        Dictionary<string, ImageFileSystemEntry> deletedEntries)
    {
        this.client = client;
        this.imageName = imageName;
        this.manifest = manifest;
        this.store = store;
        this.indexes = indexes;
        this.entries = entries;
        this.deletedEntries = deletedEntries;
        this.pathResolver = new(entries);
    }

    public async Task<bool> BuildAsync(
        string? contentPath,
        bool extracting,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrEmpty(ImagePath.NormalizeRequested(contentPath)))
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
                    if (!extracting || pathResolver.GetExtractionSource(contentPath!, entries).Type == ImageFileType.File)
                    {
                        _ = pathResolver.ResolveContentEntry(contentPath!, entries);
                        return firstLayer == 0;
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

        for (int i = 0; i < manifest.Layers.Length; i++)
        {
            StoredLayerIndex index = await GetIndexAsync(i, cancellationToken);
            ApplyLayer(index.Changes, new(i, index.Digest), cancellationToken);
        }
        return true;
    }

    public async Task<StoredLayerIndex> GetIndexAsync(int layerIndex, CancellationToken cancellationToken)
    {
        if (!indexes.TryGetValue(layerIndex, out StoredLayerIndex? index))
        {
            IDescriptor descriptor = manifest.Layers[layerIndex];
            string digest = descriptor.Digest ??
                throw new InvalidDataException($"Layer digest not set for image '{imageName}'.");
            index = await store.GetIndexAsync(client, imageName, new(layerIndex, digest),
                descriptor.Size, cancellationToken);
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
            ImageFileSystemEntry current = scanned.ToEntry(introduced, modified, layer);
            if (current.Type == ImageFileType.HardLink)
            {
                string targetPath = pathResolver.GetHardLinkTargetPath(current, entries);
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
}
