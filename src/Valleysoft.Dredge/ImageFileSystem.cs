using Valleysoft.DockerRegistryClient;
using Valleysoft.DockerRegistryClient.Models.Images;
using Valleysoft.DockerRegistryClient.Models.Manifests;
using Valleysoft.Dredge.Commands;
using System.Runtime.CompilerServices;

namespace Valleysoft.Dredge;

internal sealed class ImageFileSystem : IAsyncDisposable
{
    private readonly IDockerRegistryClient client;
    private readonly ImageName imageName;
    private readonly IImageManifest manifest;
    private readonly LayerStore store;
    private readonly bool ownsStore;
    private readonly Dictionary<int, StoredLayerIndex> indexes = [];
    private readonly Dictionary<string, ImageFileSystemEntry> entries =
        new(StringComparer.Ordinal);
    private readonly Dictionary<string, ImageFileSystemEntry> deletedEntries =
        new(StringComparer.Ordinal);
    private readonly ImageFileSystemBuilder builder;
    private readonly ImagePathResolver pathResolver;
    private readonly ExtractionPlanner extractionPlanner;

    private ImageFileSystem(
        IDockerRegistryClient client,
        ImageName imageName,
        IImageManifest manifest,
        LayerStore store,
        bool ownsStore)
    {
        this.client = client;
        this.imageName = imageName;
        this.manifest = manifest;
        this.store = store;
        this.ownsStore = ownsStore;
        this.builder = new(client, imageName, manifest, store, indexes, entries, deletedEntries);
        this.pathResolver = new(entries);
        this.extractionPlanner = new(pathResolver, entries);
    }

    public static async Task<ImageFileSystem> CreateAsync(
        IDockerRegistryClient client,
        ImageName imageName,
        PlatformOptionsBase options,
        CancellationToken cancellationToken,
        LayerStore? store = null,
        string? contentPath = null,
        string? extractionPath = null,
        IProgress<ImageIndexProgress>? progress = null,
        ResolvedManifest? resolvedManifest = null,
        Image? imageConfig = null,
        bool requireLayerIndexes = false,
        IReadOnlyDictionary<int, StoredLayerIndex>? layerIndexes = null)
    {
        ResolvedManifest resolved =
            resolvedManifest ?? await ManifestHelper.GetResolvedManifestAsync(client, imageName, options, cancellationToken);
        IImageManifest manifest = resolved.Manifest;
        string configDigest = manifest.Config?.Digest ??
            throw new NotSupportedException(
                $"Could not resolve the image config digest of '{imageName}'.");
        Image config = imageConfig ?? await client.Blobs.GetImageAsync(
            imageName.Repo,
            configDigest,
            cancellationToken);
        if (string.Equals(config.Os, "windows", StringComparison.OrdinalIgnoreCase))
        {
            throw new NotSupportedException(
                "Image filesystem commands support Linux image layers only; Windows image layers are not supported.");
        }

        ImageFileSystem fileSystem = new(client, imageName, manifest, store ?? LayerStore.Create(), store is null);
        try
        {
            if (layerIndexes is not null)
            {
                // Indexes the explorer already read while loading; reuse them instead of reading layers again.
                foreach ((int layer, StoredLayerIndex index) in layerIndexes)
                {
                    if (layer < 0 || layer >= manifest.Layers.Length || manifest.Layers[layer].Digest != index.Digest)
                    {
                        throw new InvalidOperationException($"Layer index {layer} does not match the image manifest.");
                    }
                    fileSystem.indexes[layer] = index;
                }
            }
            string digest = resolved.ManifestInfo.DockerContentDigest;
            StoredFileSystem? cached = await fileSystem.store.ReadMetadataAsync<StoredFileSystem>(
                digest, "view", cancellationToken);
            if (!requireLayerIndexes && progress is null && cached is not null && fileSystem.TryRestore(cached))
            {
                return fileSystem;
            }
            bool complete = await fileSystem.BuildIndexAsync(
                contentPath ?? extractionPath, extractionPath is not null, cancellationToken, progress);
            if (complete)
            {
                await fileSystem.store.WriteMetadataAsync(digest, "view", fileSystem.Snapshot(), cancellationToken);
            }
            return fileSystem;
        }
        catch
        {
            await fileSystem.DisposeAsync();
            throw;
        }
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

    internal ImageAnalysisResult Analyze()
    {
        if (indexes.Count != manifest.Layers.Length)
        {
            throw new InvalidOperationException(
                "The complete image must be indexed before analyzing its layers.");
        }
        return ImageAnalysis.Analyze(Enumerable.Range(0, manifest.Layers.Length)
            .Select(index => indexes[index].Changes).ToArray());
    }

    internal ImageFileSystem CreateLayerSnapshot(int layer, CancellationToken cancellationToken)
    {
        if (layer < 0 || layer >= manifest.Layers.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(layer));
        }
        ImageFileSystem snapshot = new(client, imageName, manifest, store, ownsStore: false);
        for (int i = 0; i <= layer; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!indexes.TryGetValue(i, out StoredLayerIndex? index))
            {
                throw new InvalidOperationException($"Layer {i} must be indexed before reading its package inventory.");
            }
            snapshot.indexes.Add(i, index);
            snapshot.builder.ApplyLayer(index.Changes, new(i, index.Digest), cancellationToken);
        }
        return snapshot;
    }

    public async Task CopyFileToAsync(
        string requestedPath,
        Stream destination,
        CancellationToken cancellationToken)
    {
        ImageFileSystemEntry entry = ResolveContentEntry(requestedPath);
        await ImageFileSystemExtractor.CopyContentEntriesAsync(
            [(entry, destination)],
            store,
            client,
            imageName,
            GetIndexAsync,
            cancellationToken);
    }

    internal async IAsyncEnumerable<(string Path, byte[]? Content, Exception? Error)> ReadFilesAsync(
        IEnumerable<(string Path, long MaximumBytes)> requests,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        List<(string Path, ImageFileSystemEntry Entry)> resolved = [];
        foreach ((string path, long maximumBytes) in requests)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Exception? error = null;
            try
            {
                ImageFileSystemEntry entry = ResolveContentEntry(path);
                if (entry.Size < 0 || entry.Size > maximumBytes || entry.Size > int.MaxValue)
                {
                    throw new InvalidDataException($"File '/{path}' exceeds the supported maximum of {maximumBytes} bytes.");
                }
                resolved.Add((path, entry));
            }
            catch (Exception exception) when (exception is IOException or InvalidDataException or InvalidOperationException or NotSupportedException)
            {
                error = exception;
            }
            if (error is not null)
            {
                yield return (path, null, error);
            }
        }

        foreach (var layer in resolved.GroupBy(item => item.Entry.ContentLayerIndex))
        {
            Stream? blob = null;
            StoredLayerIndex? index = null;
            Exception? layerError = null;
            try
            {
                index = await GetIndexAsync(layer.Key, cancellationToken);
                blob = await store.OpenIndexedBlobAsync(client, imageName, index,
                    layer.Select(item => item.Entry.ContentEntryIndex), cancellationToken);
            }
            catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException or RegistryException or HttpRequestException)
            {
                layerError = exception;
            }
            using (blob)
            using (LayerContentReader? reader = blob is null ? null : new(blob))
            {
                Dictionary<int, ScannedEntry>? entriesByIndex = index?.Changes.Entries.ToDictionary(entry => entry.EntryIndex);
                foreach (var content in layer.GroupBy(item => item.Entry.ContentEntryIndex).OrderBy(group => group.Key))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    byte[]? bytes = null;
                    if (layerError is null)
                    {
                        try
                        {
                            ScannedEntry entry = entriesByIndex![content.Key];
                            bytes = new byte[checked((int)entry.Size)];
                            using MemoryStream output = new(bytes, writable: true);
                            await reader!.CopyToAsync(entry, output, cancellationToken);
                        }
                        catch (Exception exception) when (exception is IOException or InvalidDataException or HttpRequestException)
                        {
                            layerError = exception;
                            bytes = null;
                        }
                    }
                    foreach (var item in content)
                    {
                        yield return (item.Path, bytes, layerError);
                    }
                }
            }
        }
    }

    public async Task ExtractAsync(
        string requestedPath,
        string outputPath,
        CancellationToken cancellationToken)
    {
        ExtractionPlan plan = CreateExtractionPlan(requestedPath, outputPath);
        ImageFileSystemExtractor.ExtractionState state = new();
        try
        {
            ImageFileSystemExtractor.CreateExtractionRoot(plan, state);
            ImageFileSystemExtractor.CreateExtractionSubdirectories(plan, cancellationToken);
            List<(ImageFileSystemEntry Entry, string Destination)> content =
                ExtractionPlanner.GetContentExtractionRequests(plan);
            // A failed or canceled copy may leave a partial file that must be rolled back.
            state.OutputCreated |= content.Count > 0;
            await ImageFileSystemExtractor.ExtractContentEntriesAsync(
                content,
                store,
                client,
                imageName,
                GetIndexAsync,
                cancellationToken);
            ImageFileSystemExtractor.CreatePreservedHardLinks(plan, state, cancellationToken);
            ImageFileSystemExtractor.CreateSymbolicLinks(plan, state, cancellationToken);
            ImageFileSystemExtractor.CreateSymbolicHardLinks(plan, state, cancellationToken);
            ImageFileSystemExtractor.ApplyExtractionMetadata(plan);
        }
        catch (Exception exception)
        {
            ImageFileSystemExtractor.CleanupFailedExtraction(plan, state.OutputCreated, exception);
            throw;
        }
    }

    private ExtractionPlan CreateExtractionPlan(string requestedPath, string outputPath)
    {
        string sourcePath = ImagePath.NormalizeRequested(requestedPath);
        bool extractingRoot = sourcePath.Length == 0;
        ImageFileSystemEntry? source = extractingRoot
            ? null
            : pathResolver.GetExtractionSource(sourcePath, entries);
        return extractionPlanner.CreatePlan(
            requestedPath,
            outputPath,
            extractingRoot,
            source,
            entries);
    }

    private async Task<bool> BuildIndexAsync(string? contentPath, bool extracting,
        CancellationToken cancellationToken, IProgress<ImageIndexProgress>? progress) =>
        await builder.BuildAsync(contentPath, extracting, cancellationToken, progress);

    private async Task<StoredLayerIndex> GetIndexAsync(int layerIndex, CancellationToken cancellationToken) =>
        await builder.GetIndexAsync(layerIndex, cancellationToken);

    private ImageFileSystemEntry ResolveContentEntry(string requestedPath) =>
        pathResolver.ResolveContentEntry(requestedPath, entries);

    public string ResolveParentComponents(string path) =>
        pathResolver.ResolveParentComponents(path);

    public ValueTask DisposeAsync() => ownsStore ? store.DisposeAsync() : ValueTask.CompletedTask;

    private StoredFileSystem Snapshot() => new(
        manifest.Layers.Select(layer => layer.Digest!).ToArray(),
        entries.Values.Select(StoredEntry.FromEntry).ToArray(),
        deletedEntries.Values.Select(StoredEntry.FromEntry).ToArray());

    private bool TryRestore(StoredFileSystem cached)
    {
        try
        {
            if (cached.Layers is null || cached.Entries is null || cached.DeletedEntries is null ||
                !cached.Layers.SequenceEqual(manifest.Layers.Select(layer => layer.Digest)))
            {
                throw new InvalidDataException("The cached view has different layer identities.");
            }
            RestoreEntries(cached.Entries, entries);
            RestoreEntries(cached.DeletedEntries, deletedEntries);
            return true;
        }
        catch (InvalidDataException exception)
        {
            Console.Error.WriteLine($"Rebuilding invalid filesystem view: {exception.Message}");
            entries.Clear();
            deletedEntries.Clear();
            return false;
        }
    }

    private void RestoreEntries(StoredEntry[] cached, Dictionary<string, ImageFileSystemEntry> destination)
    {
        foreach (StoredEntry stored in cached)
        {
            ImageFileSystemEntry? value = stored?.Value;
            if (value is null || string.IsNullOrEmpty(value.Path) ||
                ImagePath.NormalizeArchive(value.Path) != value.Path ||
                !Enum.IsDefined(value.Type) || value.Size < 0 ||
                stored!.ContentLayerIndex < 0 || stored.ContentLayerIndex >= manifest.Layers.Length ||
                stored.ContentEntryIndex < 0 ||
                value.IntroducedLayer is null || !ValidReference(value.IntroducedLayer) ||
                (value.ModifiedLayer is not null && !ValidReference(value.ModifiedLayer)) ||
                (value.DeletedLayer is not null && !ValidReference(value.DeletedLayer)) ||
                (stored.ContentPath is not null && ImagePath.NormalizeArchive(stored.ContentPath) != stored.ContentPath))
            {
                throw new InvalidDataException("The cached view contains invalid entry coordinates.");
            }
            if (!destination.TryAdd(value.Path, value with
            {
                ContentLayerIndex = stored.ContentLayerIndex,
                ContentEntryIndex = stored.ContentEntryIndex,
                ContentPath = stored.ContentPath,
                ContentLinkTarget = stored.ContentLinkTarget
            }))
            {
                throw new InvalidDataException("The cached view contains duplicate paths.");
            }
        }
    }

    private bool ValidReference(ImageLayerReference layer) =>
        layer.Index >= 0 && layer.Index < manifest.Layers.Length &&
        layer.Digest == manifest.Layers[layer.Index].Digest;

    internal sealed record StoredFileSystem(string[] Layers, StoredEntry[] Entries, StoredEntry[] DeletedEntries);

    internal sealed record StoredEntry(
        ImageFileSystemEntry Value, int ContentLayerIndex, int ContentEntryIndex,
        string? ContentPath, string? ContentLinkTarget)
    {
        public static StoredEntry FromEntry(ImageFileSystemEntry value) =>
            new(value, value.ContentLayerIndex, value.ContentEntryIndex, value.ContentPath, value.ContentLinkTarget);
    }

}
