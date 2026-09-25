using Valleysoft.DockerRegistryClient;
using Valleysoft.DockerRegistryClient.Models.Images;
using Valleysoft.DockerRegistryClient.Models.Manifests;
using Valleysoft.Dredge.Commands;

namespace Valleysoft.Dredge;

internal sealed class ImageFileSystem : IAsyncDisposable
{
    private const int MaximumLinkHops = 40;

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
        string? extractionPath = null)
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

        ImageFileSystem fileSystem = new(client, imageName, manifest, store ?? LayerStore.Create(), store is null);
        try
        {
            string digest = resolved.ManifestInfo.DockerContentDigest;
            StoredFileSystem? cached = await fileSystem.store.ReadMetadataAsync<StoredFileSystem>(
                digest, "view", cancellationToken);
            if (cached is not null && fileSystem.TryRestore(cached))
            {
                return fileSystem;
            }
            bool complete = await fileSystem.BuildIndexAsync(
                contentPath ?? extractionPath, extractionPath is not null, cancellationToken);
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

    private async Task<bool> BuildIndexAsync(string? contentPath, bool extracting, CancellationToken cancellationToken) =>
        await builder.BuildAsync(contentPath, extracting, cancellationToken);

    private async Task<StoredLayerIndex> GetIndexAsync(int layerIndex, CancellationToken cancellationToken) =>
        await builder.GetIndexAsync(layerIndex, cancellationToken);

    private void ApplyLayer(
        LayerChanges changes,
        ImageLayerReference layer,
        CancellationToken cancellationToken) =>
        builder.ApplyLayer(changes, layer, cancellationToken);

    private ImageFileSystemEntry ResolveContentEntry(string requestedPath) =>
        pathResolver.ResolveContentEntry(requestedPath, entries);

    private string ResolveParentComponents(string path) =>
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

    private sealed record StoredFileSystem(string[] Layers, StoredEntry[] Entries, StoredEntry[] DeletedEntries);

    private sealed record StoredEntry(
        ImageFileSystemEntry Value, int ContentLayerIndex, int ContentEntryIndex,
        string? ContentPath, string? ContentLinkTarget)
    {
        public static StoredEntry FromEntry(ImageFileSystemEntry value) =>
            new(value, value.ContentLayerIndex, value.ContentEntryIndex, value.ContentPath, value.ContentLinkTarget);
    }

}
