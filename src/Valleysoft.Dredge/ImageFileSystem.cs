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
        ImageFileSystemExtractor.ExtractionState state = new();
        try
        {
            if (plan.ExtractingRoot || plan.Source!.Type == ImageFileType.Directory)
            {
                Directory.CreateDirectory(plan.OutputPath);
                state.OutputCreated = true;
            }
            ExtractionPlanner.CreateExtractionSubdirectories(plan, cancellationToken);
            List<(ImageFileSystemEntry Entry, string Destination)> content =
                ExtractionPlanner.GetContentExtractionRequests(plan);
            // A failed or canceled copy may leave a partial file that must be rolled back.
            state.OutputCreated |= content.Count > 0;
            await ExtractContentEntriesAsync(content, cancellationToken);
            CreatePreservedHardLinks(plan, state, cancellationToken);
            CreateSymbolicLinks(plan, state, cancellationToken);
            CreateSymbolicHardLinks(plan, state, cancellationToken);
            ApplyExtractionMetadata(plan);
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

    private ImageFileSystemEntry GetExtractionSource(string sourcePath) =>
        pathResolver.GetExtractionSource(sourcePath, entries);

    private List<ImageFileSystemEntry> SelectExtractionEntries(
        string sourcePath,
        ImageFileSystemEntry? source,
        bool extractingRoot) =>
        ExtractionPlanner.SelectExtractionEntries(sourcePath, source, extractingRoot, entries);

    private static List<ImageFileSystemEntry> OrderExtractionEntries(
        IEnumerable<ImageFileSystemEntry> selected) =>
        ExtractionPlanner.OrderExtractionEntries(selected);

    private static void ValidateExtractionEntries(IEnumerable<ImageFileSystemEntry> selected) =>
        ExtractionPlanner.ValidateExtractionEntries(selected);

    private static Dictionary<string, string> CreateExtractionDestinations(
        IEnumerable<ImageFileSystemEntry> selected,
        string sourcePath,
        string outputPath,
        bool extractingRoot) =>
        ExtractionPlanner.CreateExtractionDestinations(selected, sourcePath, outputPath, extractingRoot);

    private Dictionary<string, string> GetExtractionHardLinkTargets(
        IEnumerable<ImageFileSystemEntry> selected) =>
        extractionPlanner.GetExtractionHardLinkTargets(selected);

    private HashSet<string> GetPreservableHardLinks(
        IEnumerable<ImageFileSystemEntry> selected,
        IReadOnlyDictionary<string, string> destinations,
        IReadOnlyDictionary<string, string> hardLinkTargets) =>
        ExtractionPlanner.GetPreservableHardLinks(selected, destinations, hardLinkTargets, entries);

    private static void CreateExtractionSubdirectories(
        ExtractionPlan plan,
        CancellationToken cancellationToken) =>
        ExtractionPlanner.CreateExtractionSubdirectories(plan, cancellationToken);

    private static List<(ImageFileSystemEntry Entry, string Destination)>
        GetContentExtractionRequests(ExtractionPlan plan) =>
        ExtractionPlanner.GetContentExtractionRequests(plan);

    private static void CreatePreservedHardLinks(
        ExtractionPlan plan,
        ImageFileSystemExtractor.ExtractionState state,
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
        ImageFileSystemExtractor.ExtractionState state,
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
        ImageFileSystemExtractor.ExtractionState state,
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

    private ImageFileSystemEntry ResolvePath(string requestedPath) =>
        pathResolver.ResolvePath(requestedPath, entries);

    private string ResolveParentComponents(string path) =>
        pathResolver.ResolveParentComponents(path);

    private ImageFileSystemEntry? TryResolvePath(string requestedPath) =>
        pathResolver.TryResolvePath(requestedPath, entries);

    private string GetHardLinkTargetPath(ImageFileSystemEntry entry) =>
        pathResolver.GetHardLinkTargetPath(entry, entries);

    private string? TryGetHardLinkTargetPath(ImageFileSystemEntry entry) =>
        pathResolver.TryGetHardLinkTargetPath(entry, entries);

    private async Task CopyContentEntriesAsync(
        IEnumerable<(ImageFileSystemEntry Entry, Stream Destination)> requests,
        CancellationToken cancellationToken)
    {
        List<(ImageFileSystemEntry Entry, Stream Destination)> requestList = requests.ToList();
        foreach (IGrouping<int, (ImageFileSystemEntry Entry, Stream Destination)> group in
            requestList.GroupBy(request => request.Entry.ContentLayerIndex))
        {
            cancellationToken.ThrowIfCancellationRequested();
            Dictionary<(string Path, int EntryIndex), Queue<Stream>> destinations = group
                .GroupBy(request => (
                    Path: request.Entry.ContentPath ?? request.Entry.Path,
                    EntryIndex: request.Entry.ContentEntryIndex))
                .ToDictionary(
                    item => item.Key,
                    item => new Queue<Stream>(item.Select(request => request.Destination)));

            StoredLayerIndex index = await GetIndexAsync(group.Key, cancellationToken);
            using Stream blob = await store.OpenIndexedBlobAsync(
                client, imageName, index, destinations.Keys.Select(key => key.EntryIndex), cancellationToken);
            using LayerContentReader reader = new(blob);
            foreach (ScannedEntry entry in index.Changes.Entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!destinations.Remove(
                    (entry.Path, entry.EntryIndex),
                    out Queue<Stream>? outputs))
                {
                    continue;
                }

                using FileStream? buffer = outputs.Count > 1 ? store.CreateScratchFile() : null;
                Stream first = buffer ?? outputs.Dequeue();
                await reader.CopyToAsync(entry, first, cancellationToken);
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
            Dictionary<(string Path, int EntryIndex), Queue<string>> destinations = group
                .GroupBy(request => (
                    Path: request.Entry.ContentPath ?? request.Entry.Path,
                    EntryIndex: request.Entry.ContentEntryIndex))
                .ToDictionary(
                    item => item.Key,
                    item => new Queue<string>(item.Select(request => request.Destination)));

            StoredLayerIndex index = await GetIndexAsync(group.Key, cancellationToken);
            using Stream blob = await store.OpenIndexedBlobAsync(
                client, imageName, index, destinations.Keys.Select(key => key.EntryIndex), cancellationToken);
            using LayerContentReader reader = new(blob);
            foreach (ScannedEntry entry in index.Changes.Entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!destinations.Remove(
                    (entry.Path, entry.EntryIndex),
                    out Queue<string>? outputs))
                {
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
                    await reader.CopyToAsync(entry, destination, cancellationToken);
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

}
