using System.IO.Compression;
using System.Text;
using Valleysoft.Dredge.Commands.Image;
using Valleysoft.Dredge.Explorer;
using Valleysoft.Dredge.Explorer.Tui;

namespace Valleysoft.Dredge;

internal sealed record HtmlReportFile(
    string Path, string Type, long Size, string? Change = null, string? Mode = null, string? Owner = null,
    string? LinkTarget = null, bool HardLink = false, string? ContentId = null, string? Note = null);
internal sealed record HtmlReportDifference(string Path, string Change, long? Before, long? After);
internal sealed record HtmlReportPathSize(
    string Path, long? Before, long? After, bool Directory, int FileCount, string? Sharing, string? LinkTarget, string Change,
    bool Link, bool HardLink = false, string? SwappedLinkTarget = null);
internal sealed record HtmlReportFileTreeMetadata(
    int Layer, bool WholeFilesystem, string Path, long Size, long ShippedSize,
    int FileCount, string? Sharing, bool HardLink, string? LinkTarget, string Type, string? Change, string? Note);
internal sealed record HtmlReportChangeCount(string Change, int Count);
internal sealed record HtmlReportFileTreeSummary(
    int Layer, bool WholeFilesystem, long Size, int PathCount, HtmlReportChangeCount[] Changes);
internal sealed record HtmlReportFinding(
    string Kind, string Title, string Category, long Bytes, int FileCount,
    int[] Layers, string[] Roots, string Where, string Why, string FixLabel, string Fix,
    bool FixIsDockerfile, string[] Explain, bool Certain, bool FromBase);
// Size is the download (or stored) size; FileSize is the uncompressed file payload, of
// which Reclaimable bytes are hidden by later layers.
internal sealed record HtmlReportLayer(
    int Index, string Digest, string ComparisonIdentity, long Size, long FileSize, long Reclaimable,
    string? BaseImage, string? BaseLabel, string Instruction, HtmlReportFile[] Changes);
internal sealed record HtmlReportHistory(int? Layer, string Instruction, string Created);
internal sealed record HtmlReportPackage(string Name, string[] Versions);
internal sealed record HtmlReportPackageGroup(
    string Ecosystem, bool Available, HtmlReportPackage[] Packages, int ScopedCount, bool Incomplete);
internal sealed record HtmlReportPackageInventory(
    int[] Layers, HtmlReportPackageGroup[] Ecosystems, string[] Warnings, string? UnavailableReason);
internal sealed record HtmlReportPackageDifference(
    string Ecosystem, string Name, string? Before, string? After, string? Scope, bool IsDefinitive);
internal sealed record HtmlReportPathIdentity(string Path, int Id);
internal sealed record HtmlReportImage(
    string Reference, string Label, string Identity, string Platform, string RepoName, string TransferLabel,
    HtmlReportHistory[] History, HtmlReportLayer[] Layers, HtmlReportFile[] Files, string[] Warnings,
    HtmlReportPackageInventory[] PackageInventories, HtmlReportPathIdentity[]? PathIdentities = null,
    HtmlReportFileTreeMetadata[]? FileTreeMetadata = null, HtmlReportFinding[]? Findings = null,
    long HiddenBytes = 0, long BaseChurnBytes = 0, long PotentialBytes = 0,
    string? SnapshotDetails = null, HtmlReportFileTreeSummary[]? FileTreeSummaries = null,
    string? Digest = null, int? BaseLayerCount = null, bool IsDaemon = false);
internal sealed record HtmlReportData(
    HtmlReportImage[] Images, HtmlReportDifference[]? Differences, HtmlReportPackageDifference[]? PackageDifferences,
    string[] Limitations, int? InitialLayer, bool UsesContentIdentity, bool DownloadSizeAvailable,
    HtmlReportPathSize[]? PathSizes = null, HtmlReportPathIdentity[]? ComparisonPathIdentities = null);

internal static class HtmlImageReport
{
    private const string DataPlaceholder = "/* DREDGE_REPORT_DATA */";

    public static async Task<string> CreateAsync(
        IImageSourceFactory factory, IDockerRegistryClientFactory registryFactory,
        ExploreOptions options, TextWriter error, CancellationToken cancellationToken,
        LayerStore? layerStore = null)
    {
        await using ImageSourceFactory.ComparisonScope? scope =
            options.Compare is not null ? (factory as ImageSourceFactory)?.CreateComparisonScope() : null;
        factory = scope ?? factory;
        await using LayerStore? ownedStore = layerStore is null ? LayerStore.Create() : null;
        LayerStore store = layerStore ?? ownedStore!;
        await using ExplorerSource baselineSource = await ExplorerSource.OpenAsync(
            factory, registryFactory, options.Image, options, options.BaseImages, cancellationToken, store: store);
        baselineSource.Source?.Report(error);
        LayerIndexOption.ValidateLayer(options.Layer, baselineSource.LayerCount);
        ExplorerSession baseline = await ExplorerSession.CreateAsync(
            baselineSource.RegistryClient, baselineSource, store, null, cancellationToken);
        await using ImageFileSystem baselineFiles = baseline.Files;

        if (options.Compare is null)
        {
            HtmlReportImage singleReport = await CreateImageAsync(baselineSource, baseline, false, cancellationToken);
            return Render(new([singleReport], null, null, [], options.Layer, false, false));
        }

        string targetReference = ExploreCommand.ResolveCompareReference(options.Image, options.Compare);
        string? targetRegistry = ImageSourceReference.Parse(targetReference).Registry;
        await using LayerStore targetStore = store.CreateScope();
        await using ExplorerSource targetSource = await CommandHelper.WithRegistryAsync(targetRegistry, () =>
            ExplorerSource.OpenAsync(
                factory, registryFactory, targetReference, options, null, cancellationToken,
                exactPlatform: baselineSource.Platform, store: targetStore, comparing: true));
        targetSource.Source?.Report(error);
        ExplorerSession target = await CommandHelper.WithRegistryAsync(targetRegistry, () =>
            ExplorerSession.CreateAsync(
                targetSource.RegistryClient, targetSource, targetStore, null, cancellationToken));
        await using ImageFileSystem targetFiles = target.Files;
        bool usesContentIdentity = baseline.IsDaemon || target.IsDaemon;
        if (usesContentIdentity)
        {
            await baseline.EnsureContentIdentitiesAsync(cancellationToken);
            await CommandHelper.WithRegistryAsync(targetRegistry, () =>
                target.EnsureContentIdentitiesAsync(cancellationToken));
        }
        cancellationToken.ThrowIfCancellationRequested();
        HtmlReportImage baselineReport = await CreateImageAsync(
            baselineSource, baseline, usesContentIdentity, cancellationToken);
        HtmlReportImage targetReport = await CommandHelper.WithRegistryAsync(targetRegistry, () =>
            CreateImageAsync(targetSource, target, usesContentIdentity, cancellationToken));
        ExplorerComparison comparison = ExplorerSession.Compare(baseline, target);
        HtmlReportDifference[] differences = [.. comparison.Files.Select(static file =>
            new HtmlReportDifference(file.Path, file.Kind.ToString(), file.Baseline?.Size, file.Target?.Size))];
        HtmlReportPackageDifference[] packages = [.. comparison.Packages.Select(static package =>
            new HtmlReportPackageDifference(CompareView.EcosystemName(package.Ecosystem), package.Name,
                package.BaselineVersion, package.TargetVersion, package.Scope, package.IsDefinitive))];
        HtmlReportPathSize[] pathSizes = [.. CompareView.FilePathSizes(comparison).Select(static size =>
            new HtmlReportPathSize(size.Path, size.Before, size.After, size.Directory, size.FileCount,
                size.Sharing, size.LinkTarget, size.Change, size.Link, size.HardLink, size.SwappedLinkTarget))];
        HtmlReportPathIdentity[]? comparisonPathIdentities = baseline.Semantics.IsWindows
            ? PathIdentities(differences.Select(static difference => difference.Path)
                .Concat(pathSizes.Select(static size => size.Path)), baseline.Semantics.Comparer)
            : null;
        return Render(new(
            [baselineReport, targetReport], differences, packages, [.. comparison.Limitations], options.Layer,
            comparison.UsesContentIdentity, comparison.DownloadSizeAvailable, pathSizes, comparisonPathIdentities));
    }

    private static async Task<HtmlReportImage> CreateImageAsync(
        ExplorerSource source, ExplorerSession session, bool usesContentIdentity, CancellationToken cancellationToken)
    {
        ExplorerImage image = ExplorerImage.FromSource(source);
        ExplorerInsightsResult insights = ExplorerInsights.Build(
            session.Analysis, image.Instructions, image.BaseLayerCount, semantics: session.Semantics);
        image.SetSession(session, insights);
        HtmlReportFileTreeMetadata[] treeMetadata = FileTreeMetadata(image, cancellationToken);
        HtmlReportPathIdentity[]? pathIdentities = session.Semantics.IsWindows
            ? PathIdentities(session.Analysis.Layers.SelectMany(static layer => layer.Changes)
                .Select(static change => change.Path), session.Semantics.Comparer)
            : null;
        HtmlReportLayer[] layers = [.. session.Analysis.Layers.Select(layer =>
        {
            HtmlReportFile[] changes = [.. layer.Changes.Select(change => new HtmlReportFile(
                change.Path, change.Type.ToString(), change.Size, change.Kind.ToString(),
                change.Entry is { } entry ? Mode(entry.Type, entry.Mode, entry.Windows) : null,
                change.Entry is { } owner ? Owner(owner.UserId, owner.GroupId, owner.Windows) : null,
                change.Entry?.LinkTarget, change.Type == ImageFileType.HardLink, ContentId(change.ContentId),
                change.Entry?.Windows?.UnsupportedReason))];
            return new HtmlReportLayer(layer.Index, image.LayerDigests[layer.Index],
                session.LayerIdentity(layer.Index, usesContentIdentity) ??
                    throw new InvalidDataException("Layer identity is missing."),
                image.LayerDownloads[layer.Index], layer.FileBytes, layer.HiddenBytes,
                image.BaseImageAt(layer.Index),
                image.BaseImageAt(layer.Index) is string baseImage ? image.BaseLabel(baseImage) : null,
                image.Row(layer.Index).Instruction, changes);
        })];
        string[] warnings = [.. new[] { source.BaseWarning,
            session.Semantics.IsWindows
                ? "Windows content-only analysis excludes unsupported filesystem and runtime metadata."
                : null }.OfType<string>(),
            .. session.Files.Diagnostics.Select(static diagnostic => $"{diagnostic.Message} {diagnostic.Path}")];
        List<(InstalledPackageMetadata Metadata, int Hash, List<int> Layers)> inventories = [];
        Dictionary<int, List<int>> inventoriesByHash = [];
        for (int layer = image.LayerCount == 0 ? -1 : 0; layer < image.LayerCount; layer++)
        {
            InstalledPackageMetadata metadata = await session.GetPackagesAsync(layer, cancellationToken);
            int hash = PackageInventoryHash(metadata);
            bool found = false;
            if (inventoriesByHash.TryGetValue(hash, out List<int>? candidates))
            {
                foreach (int candidate in candidates)
                {
                    if (!PackageInventoriesEqual(inventories[candidate].Metadata, metadata)) continue;
                    inventories[candidate].Layers.Add(layer);
                    found = true;
                    break;
                }
            }
            if (!found)
            {
                int index = inventories.Count;
                inventories.Add((metadata, hash, [layer]));
                if (candidates is null) inventoriesByHash.Add(hash, [index]);
                else candidates.Add(index);
            }
        }
        HtmlReportPackageInventory[] packageInventories = [.. inventories.Select(static inventory =>
            new HtmlReportPackageInventory(
                [.. inventory.Layers],
                [.. inventory.Metadata.Ecosystems.OrderBy(static pair => pair.Key).Select(pair =>
                    new HtmlReportPackageGroup(
                        CompareView.EcosystemName(pair.Key),
                        pair.Value.Availability == InstalledPackageMetadataAvailability.Available,
                        [.. pair.Value.Packages.OrderBy(static package => package.Key, StringComparer.Ordinal)
                            .Select(static package => new HtmlReportPackage(package.Key, [.. package.Value]))],
                        ScopedPackageCount(inventory.Metadata, pair.Key),
                        ImageComparison.HasIncompletePackageCoverage(inventory.Metadata, pair.Key)))],
                [.. inventory.Metadata.Diagnostics.Select(static diagnostic => diagnostic.Path.Length == 0
                    ? diagnostic.Message : $"/{diagnostic.Path}: {diagnostic.Message}")],
                inventory.Metadata.UnavailableReason))];
        HtmlReportFile[] files = [.. session.Entries.Select(static entry => new HtmlReportFile(
            entry.Path, entry.Type.ToString(), entry.Size, null,
            Mode(entry.Type, entry.Mode, entry.Windows), Owner(entry.UserId, entry.GroupId, entry.Windows),
            entry.LinkTarget, entry.Type == ImageFileType.HardLink, ContentId(entry.ContentId)))];
        return new HtmlReportImage(
            source.Reference, ExplorerTags.Label(source.Reference), session.SnapshotReference, image.Platform, image.RepoName, image.TransferLabel,
            [.. image.History.Select(static row => new HtmlReportHistory(row.Layer, row.Instruction, row.Created))],
            layers, files,
            warnings, packageInventories,
            pathIdentities,
            treeMetadata,
            [.. insights.Findings.Select(static finding => new HtmlReportFinding(
                finding.Kind.ToString(), finding.Title, finding.Category, finding.Bytes, finding.FileCount,
                [.. finding.Layers], [.. finding.Roots], finding.Where, finding.Why, finding.FixLabel,
                finding.Fix, finding.FixIsDockerfile, [.. finding.Explain], finding.Certain, finding.FromBase))],
            insights.HiddenBytes, insights.BaseChurnBytes, insights.PotentialBytes,
            session.SnapshotDetails, FileTreeSummaries(image, cancellationToken), image.Digest, image.BaseLayerCount, image.IsDaemon);
    }

    private static HtmlReportPathIdentity[] PathIdentities(IEnumerable<string> paths, StringComparer comparer)
    {
        Dictionary<string, int> identities = new(StringComparer.Ordinal);
        Dictionary<string, int> canonicalPaths = new(comparer);
        foreach (string path in paths)
        {
            for (int end = path.IndexOf('/'); ; end = path.IndexOf('/', end + 1))
            {
                string prefix = end < 0 ? path : path[..end];
                if (!canonicalPaths.TryGetValue(prefix, out int id))
                {
                    id = canonicalPaths.Count;
                    canonicalPaths.Add(prefix, id);
                }
                identities.TryAdd(prefix, id);
                if (end < 0) break;
            }
        }
        return [.. identities.Select(static pair => new HtmlReportPathIdentity(pair.Key, pair.Value))];
    }

    private static int PackageInventoryHash(InstalledPackageMetadata metadata)
    {
        HashCode hash = new();
        foreach (var ecosystem in metadata.Ecosystems.OrderBy(static pair => pair.Key))
        {
            hash.Add(ecosystem.Key);
            hash.Add(ecosystem.Value.Availability);
            hash.Add(ScopedPackageCount(metadata, ecosystem.Key));
            hash.Add(ImageComparison.HasIncompletePackageCoverage(metadata, ecosystem.Key));
            int packagesHash = 0;
            foreach (var package in ecosystem.Value.Packages)
            {
                HashCode versionsHash = new();
                foreach (string version in package.Value)
                {
                    versionsHash.Add(version, StringComparer.Ordinal);
                }
                packagesHash ^= HashCode.Combine(
                    StringComparer.Ordinal.GetHashCode(package.Key), versionsHash.ToHashCode());
            }
            hash.Add(ecosystem.Value.Packages.Count);
            hash.Add(packagesHash);
        }
        foreach (InstalledPackageDiagnostic diagnostic in metadata.Diagnostics)
        {
            hash.Add(diagnostic.Path, StringComparer.Ordinal);
            hash.Add(diagnostic.Message, StringComparer.Ordinal);
        }
        hash.Add(metadata.Diagnostics.Count);
        hash.Add(metadata.UnavailableReason, StringComparer.Ordinal);
        return hash.ToHashCode();
    }

    private static bool PackageInventoriesEqual(InstalledPackageMetadata left, InstalledPackageMetadata right)
    {
        if (left.UnavailableReason != right.UnavailableReason ||
            left.Diagnostics.Count != right.Diagnostics.Count ||
            left.Ecosystems.Count != right.Ecosystems.Count)
        {
            return false;
        }
        for (int index = 0; index < left.Diagnostics.Count; index++)
        {
            if (left.Diagnostics[index] != right.Diagnostics[index]) return false;
        }
        foreach (var ecosystem in left.Ecosystems)
        {
            if (!right.Ecosystems.TryGetValue(ecosystem.Key, out InstalledPackageEcosystemMetadata? other) ||
                ecosystem.Value.Availability != other.Availability ||
                ScopedPackageCount(left, ecosystem.Key) != ScopedPackageCount(right, ecosystem.Key) ||
                ImageComparison.HasIncompletePackageCoverage(left, ecosystem.Key) !=
                    ImageComparison.HasIncompletePackageCoverage(right, ecosystem.Key) ||
                ecosystem.Value.Packages.Count != other.Packages.Count)
            {
                return false;
            }
            foreach (var package in ecosystem.Value.Packages)
            {
                if (!other.Packages.TryGetValue(package.Key, out IReadOnlyList<string>? versions) ||
                    !package.Value.SequenceEqual(versions, StringComparer.Ordinal))
                {
                    return false;
                }
            }
        }
        return true;
    }

    private static int ScopedPackageCount(InstalledPackageMetadata metadata, InstalledPackageEcosystem ecosystem) =>
        metadata.Coverage.Count == 0 ? metadata.Ecosystems[ecosystem].Packages.Count :
            metadata.ScopedPackages.Where(package => package.Ecosystem == ecosystem)
                .DistinctBy(static package => (package.Scope, package.Name)).Count();

    private static HtmlReportFileTreeMetadata[] FileTreeMetadata(ExplorerImage image, CancellationToken cancellationToken)
    {
        List<HtmlReportFileTreeMetadata> result = [];
        for (int layer = 0; layer < image.LayerCount; layer++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Add(image.LayerTree(layer), layer);
        }
        return [.. result];

        void Add(IEnumerable<Explorer.Node> nodes, int layer)
        {
            foreach (Explorer.Node node in nodes)
            {
                if (node.FileCount > 0 || node.Sharing is not null || node.HardLink ||
                    node.Target is not null || node.Note is not null)
                {
                    result.Add(new(layer, false, node.Path, node.Size, node.ShippedSize,
                        node.FileCount, node.Sharing, node.HardLink, node.Target,
                        node.Kind switch { Explorer.Kind.Dir => "Directory", Explorer.Kind.Link => "SymbolicLink", _ => "File" },
                        node.Change switch
                        {
                            Explorer.Change.Added => "Added",
                            Explorer.Change.Modified => "Modified",
                            Explorer.Change.Identical => "Identical",
                            Explorer.Change.Removed => "Deleted",
                            _ => null
                        }, node.Note));
                }
                Add(node.Children, layer);
            }
        }
    }

    private static HtmlReportFileTreeSummary[] FileTreeSummaries(ExplorerImage image, CancellationToken cancellationToken)
    {
        List<HtmlReportFileTreeSummary> result = [];
        for (int layer = 0; layer < image.LayerCount; layer++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            List<Explorer.Node> changed = image.LayerTree(layer);
            Add(changed, layer);
        }
        return [.. result];

        void Add(IEnumerable<Explorer.Node> tree, int layer)
        {
            Dictionary<Explorer.Change, int> counts = ExplorerPresenter.Count(tree);
            result.Add(new(layer, false, Explorer.Node.TotalSize(tree), counts.Values.Sum(),
                [.. counts.OrderBy(static pair => pair.Key).Select(static pair =>
                    new HtmlReportChangeCount(pair.Key switch
                    {
                        Explorer.Change.Added => "Added",
                        Explorer.Change.Modified => "Modified",
                        Explorer.Change.Identical => "Identical",
                        Explorer.Change.Removed => "Deleted",
                        _ => "None"
                    }, pair.Value))]));
        }
    }

    private static string? ContentId(ImageContentId? contentId) =>
        contentId is { } id ? $"{id.Layer}:{id.Path}:{id.EntryIndex}" : null;

    private static string Mode(ImageFileType type, int mode, WindowsFileMetadata? windows) =>
        windows is null ? ExplorerImage.FormatMode(type, mode) :
        windows.Attributes is uint attributes ? $"Windows attributes 0x{attributes:X}" : "Windows attributes unavailable";

    private static string Owner(int userId, int groupId, WindowsFileMetadata? windows) =>
        windows is null ? $"{userId}:{groupId}" : "ACL/owner not interpreted";

    internal static string Render(HtmlReportData data)
    {
        using Stream stream = typeof(HtmlImageReport).Assembly.GetManifestResourceStream(
            "Valleysoft.Dredge.HtmlImageReport.html") ??
            throw new InvalidOperationException("The HTML report template is missing.");
        using StreamReader reader = new(stream);
        string template = reader.ReadToEnd();
        if (!template.Contains(DataPlaceholder, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The HTML report template has no data placeholder.");
        }
        string json = JsonHelper.Serialize(data, JsonHelper.CompactSettings);
        using MemoryStream compressed = new();
        using (GZipStream gzip = new(compressed, CompressionLevel.SmallestSize, leaveOpen: true))
        {
            gzip.Write(Encoding.UTF8.GetBytes(json));
        }
        return template.Replace(DataPlaceholder, Convert.ToBase64String(compressed.GetBuffer(), 0, (int)compressed.Length),
            StringComparison.Ordinal);
    }
}
