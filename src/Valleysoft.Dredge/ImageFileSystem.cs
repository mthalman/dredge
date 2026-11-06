using Valleysoft.DockerRegistryClient;
using Valleysoft.DockerRegistryClient.Models.Images;
using Valleysoft.DockerRegistryClient.Models.Manifests;
using Valleysoft.Dredge.Commands;
using System.Runtime.CompilerServices;

namespace Valleysoft.Dredge;

internal sealed class ImageFileSystem : IAsyncDisposable
{
    private readonly IImageContentSource content;
    private readonly ImageName imageName;
    private readonly IImageManifest manifest;
    private readonly LayerStore store;
    private readonly bool ownsStore;
    private readonly Dictionary<int, StoredLayerIndex> indexes = [];
    private readonly Dictionary<string, ImageFileSystemEntry> entries;
    private readonly Dictionary<string, ImageFileSystemEntry> deletedEntries;
    private readonly ImageFileSystemBuilder builder;
    private readonly ImagePathResolver pathResolver;
    private readonly ExtractionPlanner extractionPlanner;
    private IReadOnlyList<LayerDiagnostic> restoredDiagnostics = [];

    private ImageFileSystem(
        IImageContentSource content,
        ImageName imageName,
        IImageManifest manifest,
        LayerStore store,
        bool ownsStore,
        ImageFileSystemSemantics? semantics = null)
    {
        this.content = content;
        this.imageName = imageName;
        this.manifest = manifest;
        this.store = store;
        this.ownsStore = ownsStore;
        Semantics = semantics ?? ImageFileSystemSemantics.Linux;
        entries = new(Semantics.Comparer);
        deletedEntries = new(Semantics.Comparer);
        this.builder = new(content, imageName, manifest, store, indexes, entries, deletedEntries, Semantics);
        this.pathResolver = new(entries, Semantics);
        this.extractionPlanner = new(pathResolver, entries, Semantics);
    }

    internal ImageFileSystemSemantics Semantics { get; }
    internal ImageFileSystemEntry? FindEntry(string path) => entries.GetValueOrDefault(path);

    internal int DiagnosticCount
    {
        get
        {
            int count = restoredDiagnostics.Count;
            foreach (StoredLayerIndex index in indexes.Values)
            {
                count += index.Changes.Diagnostics.Count;
            }
            return count;
        }
    }
    internal IReadOnlyList<LayerDiagnostic> Diagnostics =>
        [.. restoredDiagnostics, .. indexes.OrderBy(static pair => pair.Key).SelectMany(pair => pair.Value.Changes.Diagnostics
            .Select(diagnostic => diagnostic with { Message = $"Layer {pair.Key}: {diagnostic.Message}" }))];

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
        IReadOnlyDictionary<int, StoredLayerIndex>? layerIndexes = null,
        int? maxLayerIndex = null)
    {
        ResolvedManifest resolved =
            resolvedManifest ?? await ManifestHelper.GetResolvedManifestAsync(client, imageName, options, cancellationToken);
        IImageManifest manifest = resolved.Manifest;
        if (maxLayerIndex is int selectedLayer &&
            (selectedLayer < 0 || selectedLayer >= manifest.Layers.Length))
        {
            throw new ArgumentOutOfRangeException(nameof(maxLayerIndex));
        }
        string configDigest = manifest.Config?.Digest ??
            throw new NotSupportedException(
                $"Could not resolve the image config digest of '{imageName}'.");
        Image config = imageConfig ?? await client.Blobs.GetImageAsync(
            imageName.Repo,
            configDigest,
            cancellationToken);
        return await CreateAsync(new RegistryImageContentSource(client, imageName), imageName, resolved, config,
            cancellationToken, store, contentPath, extractionPath, progress, requireLayerIndexes, layerIndexes, maxLayerIndex);
    }

    public static Task<ImageFileSystem> CreateAsync(
        ResolvedImage source,
        CancellationToken cancellationToken,
        LayerStore? store = null,
        string? contentPath = null,
        string? extractionPath = null,
        IProgress<ImageIndexProgress>? progress = null,
        bool requireLayerIndexes = false,
        IReadOnlyDictionary<int, StoredLayerIndex>? layerIndexes = null,
        int? maxLayerIndex = null) =>
        CreateAsync(source.Content, source.Name, source.Resolved, source.Configuration,
            cancellationToken, store, contentPath, extractionPath, progress, requireLayerIndexes, layerIndexes, maxLayerIndex);

    internal static async Task<ImageFileSystem> CreateAsync(
        IImageContentSource content,
        ImageName imageName,
        ResolvedManifest resolved,
        Image config,
        CancellationToken cancellationToken,
        LayerStore? store = null,
        string? contentPath = null,
        string? extractionPath = null,
        IProgress<ImageIndexProgress>? progress = null,
        bool requireLayerIndexes = false,
        IReadOnlyDictionary<int, StoredLayerIndex>? layerIndexes = null,
        int? maxLayerIndex = null,
        bool allowMissingContent = false)
    {
        IImageManifest manifest = resolved.Manifest;
        if (maxLayerIndex is int selectedLayer &&
            (selectedLayer < 0 || selectedLayer >= manifest.Layers.Length))
        {
            throw new ArgumentOutOfRangeException(nameof(maxLayerIndex));
        }
        ImageFileSystemSemantics semantics = ImageFileSystemSemantics.ForOs(config.Os);
        if (contentPath is not null) _ = semantics.NormalizeRequested(contentPath);
        if (extractionPath is not null) _ = semantics.NormalizeRequested(extractionPath);
        foreach (IDescriptor layer in manifest.Layers.Take(
            maxLayerIndex is int lastLayer ? lastLayer + 1 : manifest.Layers.Length))
        {
            semantics.ValidateLayerMediaType(layer.MediaType);
        }
        ImageFileSystem fileSystem = new(content, imageName, manifest, store ?? LayerStore.Create(), store is null, semantics);
        fileSystem.builder.ExpectedDiffIds = config.RootFilesystem?.DiffIds;
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
            string digest = content is DockerImageArchive
                ? manifest.Config!.Digest!
                : resolved.ManifestInfo.DockerContentDigest;
            ValidatedFileSystem? validated = maxLayerIndex is null
                ? (content as DockerImageArchive)?.CachedFileSystem : null;
            StoredFileSystem? cached = maxLayerIndex is null && validated is null
                ? await fileSystem.store.ReadMetadataAsync<StoredFileSystem>(
                    digest, semantics.ViewKind, cancellationToken)
                : null;
            cancellationToken.ThrowIfCancellationRequested();
            if (maxLayerIndex is null && !requireLayerIndexes && progress is null &&
                ((validated is not null && fileSystem.TryRestore(validated)) ||
                (cached is not null && fileSystem.TryRestore(cached))))
            {
                return fileSystem;
            }
            bool complete = await fileSystem.BuildIndexAsync(
                contentPath ?? extractionPath, extractionPath is not null, cancellationToken, progress,
                maxLayerIndex, allowMissingContent);
            if (complete && maxLayerIndex is null)
            {
                await fileSystem.store.WriteMetadataAsync(digest, semantics.ViewKind, fileSystem.Snapshot(), cancellationToken);
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
        string path = Semantics.NormalizeRequested(requestedPath);
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
            return [Semantics.Comparer.Equals(selected.Path, path) ? selected : selected with { Path = path }];
        }

        IEnumerable<ImageFileSystemEntry> results = entries.Values;
        if (showDeleted)
        {
            results = results.Concat(deletedEntries.Values);
        }

        string resolvedPath = selected?.Path ?? path;
        string prefix = resolvedPath.Length == 0 ? string.Empty : $"{resolvedPath}/";
        return [.. results
            .Where(entry =>
            {
                if (!Semantics.StartsWith(entry.Path, prefix) ||
                    entry.Path.Length == prefix.Length)
                {
                    return false;
                }

                string relative = entry.Path[prefix.Length..];
                return recursive || !relative.Contains('/');
            })
            .Select(entry => Semantics.Comparer.Equals(resolvedPath, path)
                ? entry
                : entry with { Path = $"{path}/{entry.Path[prefix.Length..]}" })
            .OrderBy(entry => entry.Path, Semantics.Comparer)];
    }

    internal ImageAnalysisResult Analyze()
    {
        if (indexes.Count != manifest.Layers.Length)
        {
            throw new InvalidOperationException(
                "The complete image must be indexed before analyzing its layers.");
        }
        return ImageAnalysis.Analyze([.. Enumerable.Range(0, manifest.Layers.Length).Select(index => indexes[index].Changes)], Semantics);
    }

    internal void ValidateLayerSnapshot(int layer, CancellationToken cancellationToken)
    {
        if (layer < 0 || layer >= manifest.Layers.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(layer));
        }
        for (int i = 0; i <= layer; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!indexes.ContainsKey(i))
            {
                throw new InvalidOperationException($"Layer {i} must be indexed before reading its package inventory.");
            }
        }
    }

    internal ImageFileSystem CreateLayerSnapshot(int layer, CancellationToken cancellationToken)
    {
        ValidateLayerSnapshot(layer, cancellationToken);
        ImageFileSystem snapshot = new(content, imageName, manifest, store, ownsStore: false, Semantics);
        for (int i = 0; i <= layer; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            StoredLayerIndex index = indexes[i];
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
            content,
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

        foreach (var layer in resolved.GroupBy(static item => item.Entry.ContentLayerIndex))
        {
            Stream? blob = null;
            StoredLayerIndex? index = null;
            Exception? layerError = null;
            try
            {
                index = await GetIndexAsync(layer.Key, cancellationToken);
                blob = await store.OpenIndexedBlobAsync(content, index,
                    layer.Select(static item => item.Entry.ContentEntryIndex), cancellationToken);
            }
            catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException or RegistryException or HttpRequestException)
            {
                layerError = exception;
            }
            using (blob)
            using (LayerContentReader? reader = blob is null ? null : new(blob, index!.Encoding))
            {
                Dictionary<int, ScannedEntry>? entriesByIndex = index?.Changes.Entries.ToDictionary(static entry => entry.EntryIndex);
                foreach (var content in layer.GroupBy(static item => item.Entry.ContentEntryIndex).OrderBy(static group => group.Key))
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
                this.content,
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
        string sourcePath = Semantics.NormalizeRequested(requestedPath);
        bool extractingRoot = sourcePath.Length == 0;
        ImageFileSystemEntry? source = extractingRoot
            ? null
            : pathResolver.GetExtractionSource(sourcePath, entries);
        return extractionPlanner.CreatePlan(
            requestedPath,
            outputPath,
            extractingRoot,
            source,
            entries,
            useCapturedStreamState: source is { Type: ImageFileType.File or ImageFileType.HardLink });
    }

    private async Task<bool> BuildIndexAsync(string? contentPath, bool extracting,
        CancellationToken cancellationToken, IProgress<ImageIndexProgress>? progress,
        int? maxLayerIndex, bool allowMissingContent) =>
        await builder.BuildAsync(contentPath, extracting, cancellationToken, progress, maxLayerIndex, allowMissingContent);

    private async Task<StoredLayerIndex> GetIndexAsync(int layerIndex, CancellationToken cancellationToken) =>
        await builder.GetIndexAsync(layerIndex, cancellationToken);

    internal ImageFileSystemEntry ResolveContentEntry(string requestedPath) =>
        pathResolver.ResolveContentEntry(requestedPath, entries);

    public string ResolveParentComponents(string path) =>
        pathResolver.ResolveParentComponents(path);

    public ValueTask DisposeAsync() => ownsStore ? store.DisposeAsync() : ValueTask.CompletedTask;

    private StoredFileSystem Snapshot() => new(
        [.. manifest.Layers.Select(static layer => layer.Digest!)],
        [.. entries.Values.Select(StoredEntry.FromEntry)],
        [.. deletedEntries.Values.Select(StoredEntry.FromEntry)]) { Diagnostics = Diagnostics };

    internal static async Task<ValidatedFileSystem?> ReadCachedViewAsync(
        LayerStore store, string configurationDigest, int layerCount, CancellationToken cancellationToken,
        ImageFileSystemSemantics? semantics = null)
    {
        semantics ??= ImageFileSystemSemantics.Linux;
        StoredFileSystem? cached = await store.ReadMetadataAsync<StoredFileSystem>(
            configurationDigest, semantics.ViewKind, cancellationToken);
        if (cached is null)
        {
            return null;
        }
        if (cached.Layers is null || cached.Layers.Length != layerCount ||
            cached.Layers.Any(digest => digest is null || !DockerImageArchive.IsSha256(digest)))
        {
            Console.Error.WriteLine("Rebuilding invalid filesystem view: The cached view has different layer identities.");
            return null;
        }
        try
        {
            if (cached.Entries is null || cached.DeletedEntries is null)
            {
                throw new InvalidDataException("The cached view has different layer identities.");
            }
            return new(cached, semantics);
        }
        catch (InvalidDataException exception)
        {
            Console.Error.WriteLine($"Rebuilding invalid filesystem view: {exception.Message}");
            return null;
        }
    }

    private bool TryRestore(StoredFileSystem cached) =>
        TryRestore(cached.Layers, cached.Entries, cached.DeletedEntries, false, cached.Diagnostics);

    private bool TryRestore(ValidatedFileSystem cached) =>
        TryRestore(cached.Layers, cached.Entries, cached.DeletedEntries, true, cached.Diagnostics);

    private bool TryRestore(IReadOnlyList<string>? cachedLayers, IReadOnlyList<StoredEntry>? cachedEntries,
        IReadOnlyList<StoredEntry>? cachedDeletedEntries, bool validated, IReadOnlyList<LayerDiagnostic> diagnostics)
    {
        try
        {
            if (cachedLayers is null || cachedEntries is null || cachedDeletedEntries is null ||
                !cachedLayers.SequenceEqual(manifest.Layers.Select(static layer => layer.Digest)))
            {
                throw new InvalidDataException("The cached view has different layer identities.");
            }
            RestoreEntries(cachedEntries, entries, cachedLayers, validated, Semantics);
            RestoreEntries(cachedDeletedEntries, deletedEntries, cachedLayers, validated, Semantics);
            restoredDiagnostics = diagnostics;
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

    private static void RestoreEntries(IReadOnlyList<StoredEntry> cached,
        Dictionary<string, ImageFileSystemEntry> destination, IReadOnlyList<string?> layers, bool validated,
        ImageFileSystemSemantics semantics)
    {
        foreach (StoredEntry stored in cached)
        {
            if (!validated)
            {
                ValidateEntry(stored, layers, semantics);
            }
            ImageFileSystemEntry value = stored.Value;
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

    private static void ValidateEntries(
        StoredEntry[] cached, IReadOnlyList<string?> layers, ImageFileSystemSemantics semantics)
    {
        HashSet<string> paths = new(semantics.Comparer);
        foreach (StoredEntry stored in cached)
        {
            ValidateEntry(stored, layers, semantics);
            if (!paths.Add(stored.Value.Path))
            {
                throw new InvalidDataException("The cached view contains duplicate paths.");
            }
        }
    }

    private static void ValidateEntry(
        StoredEntry? stored, IReadOnlyList<string?> layers, ImageFileSystemSemantics semantics)
    {
        ImageFileSystemEntry? value = stored?.Value;
        if (value is null || string.IsNullOrEmpty(value.Path) ||
            semantics.NormalizeArchivePath(value.Path) != value.Path ||
            !Enum.IsDefined(value.Type) || value.Size < 0 ||
            stored!.ContentLayerIndex < 0 || stored.ContentLayerIndex >= layers.Count ||
            stored.ContentEntryIndex < 0 ||
            value.IntroducedLayer is null || !ValidReference(value.IntroducedLayer, layers) ||
            (value.ModifiedLayer is not null && !ValidReference(value.ModifiedLayer, layers)) ||
            (value.DeletedLayer is not null && !ValidReference(value.DeletedLayer, layers)) ||
            (stored.ContentPath is not null &&
                semantics.NormalizeArchivePath(stored.ContentPath) != stored.ContentPath))
        {
            throw new InvalidDataException("The cached view contains invalid entry coordinates.");
        }
    }

    private static bool ValidReference(ImageLayerReference layer, IReadOnlyList<string?> layers) =>
        layer.Index >= 0 && layer.Index < layers.Count &&
        layer.Digest == layers[layer.Index];

    internal sealed record StoredFileSystem(string[] Layers, StoredEntry[] Entries, StoredEntry[] DeletedEntries)
    {
        public IReadOnlyList<LayerDiagnostic> Diagnostics { get; init; } = [];
    }

    internal sealed class ValidatedFileSystem
    {
        public IReadOnlyList<string> Layers { get; }
        public IReadOnlyList<StoredEntry> Entries { get; }
        public IReadOnlyList<StoredEntry> DeletedEntries { get; }
        public IReadOnlyList<LayerDiagnostic> Diagnostics { get; }

        public ValidatedFileSystem(
            StoredFileSystem stored, ImageFileSystemSemantics? semantics = null)
        {
            semantics ??= ImageFileSystemSemantics.Linux;
            string[] layers = [.. stored.Layers];
            StoredEntry[] entries = [.. stored.Entries];
            StoredEntry[] deletedEntries = [.. stored.DeletedEntries];
            ValidateEntries(entries, layers, semantics);
            ValidateEntries(deletedEntries, layers, semantics);
            Layers = Array.AsReadOnly(layers);
            Entries = Array.AsReadOnly(entries);
            DeletedEntries = Array.AsReadOnly(deletedEntries);
            Diagnostics = Array.AsReadOnly([.. stored.Diagnostics]);
        }
    }

    internal sealed record StoredEntry(
        ImageFileSystemEntry Value, int ContentLayerIndex, int ContentEntryIndex,
        string? ContentPath, string? ContentLinkTarget)
    {
        public static StoredEntry FromEntry(ImageFileSystemEntry value) =>
            new(value, value.ContentLayerIndex, value.ContentEntryIndex, value.ContentPath, value.ContentLinkTarget);
    }

}
