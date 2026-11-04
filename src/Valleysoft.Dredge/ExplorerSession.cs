using Valleysoft.DockerRegistryClient;
using Valleysoft.DockerRegistryClient.Models.Images;
using Valleysoft.DockerRegistryClient.Models.Manifests;
using Valleysoft.DockerRegistryClient.Models.Manifests.Oci;
using Valleysoft.Dredge.Commands;

namespace Valleysoft.Dredge;

internal sealed record ExplorerFileDifference(
    string Path, LayerChangeKind Kind, ImageFileSystemEntry? Baseline,
    ImageFileSystemEntry? Target);

internal sealed record ExplorerPackageDifference(
    InstalledPackageEcosystem Ecosystem, string Name, string? BaselineVersion,
    string? TargetVersion);

internal sealed record ExplorerComparison(
    ExplorerSession Baseline, ExplorerSession Target, long AdditionalDownloadBytes,
    IReadOnlyList<ExplorerFileDifference> Files,
    IReadOnlyList<ExplorerPackageDifference> Packages)
{
    public IReadOnlyList<string> Limitations { get; init; } = [];
    public bool DownloadSizeAvailable => !Baseline.IsDaemon && !Target.IsDaemon;
    public bool UsesContentIdentity => Baseline.IsDaemon || Target.IsDaemon;
}

internal sealed class ExplorerSession
{
    public ResolvedImage? Source { get; init; }
    public bool IsDaemon => Source?.IsDaemon == true;
    public string SnapshotReference => IsDaemon ? $"docker://{Source!.Identity}" :
        Explorer.ExplorerImage.DigestReference(Image, Resolved.ManifestInfo.DockerContentDigest);
    public string SnapshotDetails => IsDaemon
        ? $"Source: Docker daemon\nPlatform: {Source!.Platform}\nImage ID: {Source.Identity}\nRegistry manifest digest: unavailable"
        : SnapshotReference;
    private readonly SemaphoreSlim packageGate = new(1, 1);
    private InstalledPackageMetadata? packages;
    private readonly Dictionary<int, InstalledPackageMetadata> layerPackages = [];
    private IImageContentSource? content;
    private LayerStore? store;
    private string[]? verifiedContentDigests;
    private Image config = null!;
    private Lazy<string[]> contentDigests = null!;

    public required ImageName Image { get; init; }
    public required ResolvedManifest Resolved { get; init; }
    public ExplorerPlatform? SelectedPlatform { get; init; }
    public required Image Config
    {
        get => config;
        init
        {
            config = value;
            string[] ids = value.RootFilesystem?.DiffIds?.ToArray() ?? [];
            contentDigests = new(() => ValidateContentDigests(ids, Resolved.Manifest.Layers.Length));
        }
    }
    public required ImageFileSystem Files { get; init; }
    public required ImageAnalysisResult Analysis { get; init; }
    public required IReadOnlyList<ImageFileSystemEntry> Entries { get; init; }
    public ExplorerPlatform? Platform { get; init; }
    internal ImageFileSystemSemantics Semantics => ImageFileSystemSemantics.ForOs(Config.Os);
    public InstalledPackageMetadata Packages
    {
        get => packages ?? throw new InvalidOperationException("Package metadata has not been loaded.");
        init => packages = value;
    }
    public static async Task<ExplorerSession> LoadAsync(
        IDockerRegistryClient client, IDockerRegistryClientFactory factory,
        ImageName image, PlatformOptionsBase options,
        LayerStore store, IReadOnlyList<string>? baseImages, CancellationToken cancellationToken,
        IProgress<ImageIndexProgress>? progress = null, ExplorerPlatform? exactPlatform = null,
        ResolvedManifest? resolvedManifest = null)
    {
        ExplorerSource source = await ExplorerSource.OpenAsync(
            client, factory, image, options, baseImages, cancellationToken, exactPlatform: exactPlatform,
            resolvedManifest: resolvedManifest, comparing: exactPlatform is not null);
        return await CreateAsync(client, source, store, null, cancellationToken, progress);
    }

    public static async Task<ExplorerSession> CreateAsync(
        IDockerRegistryClient? client, ExplorerSource source, LayerStore store,
        IReadOnlyDictionary<int, StoredLayerIndex>? indexes, CancellationToken cancellationToken,
        IProgress<ImageIndexProgress>? progress = null)
    {
        IImageContentSource content = source.GetContent(client);
        ImageFileSystem files = await ImageFileSystem.CreateAsync(
            content, source.Image, source.Resolved, source.Config, cancellationToken, store,
            progress: progress, requireLayerIndexes: true, layerIndexes: indexes);
        try
        {
            ImageAnalysisResult analysis = files.Analyze();
            return new ExplorerSession
            {
                Source = source.Source,
                content = content,
                store = store,
                Image = source.Image,
                Resolved = source.Resolved,
                SelectedPlatform = source.Platform,
                Config = source.Config,
                Files = files,
                Analysis = analysis,
                Entries = files.List(null, true, false),
                Platform = source.Platform,
            };
        }
        catch
        {
            await files.DisposeAsync();
            throw;
        }
    }

    public async Task EnsurePackagesAsync(CancellationToken cancellationToken)
    {
        await GetPackagesAsync(Resolved.Manifest.Layers.Length - 1, cancellationToken);
    }

    internal async Task EnsureContentIdentitiesAsync(CancellationToken cancellationToken)
    {
        if (verifiedContentDigests is not null || IsDaemon && contentDigests.Value.All(DockerImageArchive.IsSha256))
        {
            return;
        }
        verifiedContentDigests = await ExplorerContentIdentity.VerifyAsync(
            content ?? throw new InvalidOperationException("Image content source is unavailable."),
            Config, Resolved, store ?? throw new InvalidOperationException("Layer store is unavailable."),
            cancellationToken, expectedDiffIds: contentDigests.Value);
    }

    public async Task<InstalledPackageMetadata> GetPackagesAsync(int layer, CancellationToken cancellationToken)
    {
        int finalLayer = Resolved.Manifest.Layers.Length - 1;
        if (layer < 0 && finalLayer >= 0 || layer < -1 || layer > finalLayer)
        {
            throw new ArgumentOutOfRangeException(nameof(layer));
        }
        await packageGate.WaitAsync(cancellationToken);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Files.Semantics.IsWindows && layer != finalLayer)
            {
                Files.ValidateLayerSnapshot(layer, cancellationToken);
            }
            if (Files.Semantics.IsWindows || layer == finalLayer)
            {
                if (packages is null)
                {
                    InstalledPackageMetadata metadata = await InstalledPackageReader.ReadAsync(Files, cancellationToken);
                    cancellationToken.ThrowIfCancellationRequested();
                    packages = metadata;
                }
                return packages;
            }
            if (layerPackages.TryGetValue(layer, out InstalledPackageMetadata? cached))
            {
                return cached;
            }
            await using ImageFileSystem snapshot = Files.CreateLayerSnapshot(layer, cancellationToken);
            InstalledPackageMetadata historical = await InstalledPackageReader.ReadAsync(snapshot, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            layerPackages.Add(layer, historical);
            return historical;
        }
        finally
        {
            packageGate.Release();
        }
    }

    internal static ExplorerComparison Compare(ExplorerSession baseline, ExplorerSession target)
    {
        ImageFileSystemSemantics semantics = baseline.Semantics;
        bool samePlatform = baseline.IsDaemon || target.IsDaemon
            ? baseline.ComparisonPlatform.IsCompatibleWith(target.ComparisonPlatform)
            : string.Equals(baseline.Config.Os, target.Config.Os, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(baseline.Config.Architecture, target.Config.Architecture,
                    StringComparison.OrdinalIgnoreCase) &&
                (baseline.Semantics.IsWindows
                    ? CompatibleWindowsVariants(baseline, target)
                    : string.Equals(baseline.Config.OsVersion, target.Config.OsVersion, StringComparison.OrdinalIgnoreCase));
        if (!samePlatform)
        {
            throw new InvalidOperationException(
                $"Images must use the same platform: {baseline.Config.Os}/{baseline.Config.Architecture} {baseline.Config.OsVersion} " +
                $"and {target.Config.Os}/{target.Config.Architecture} {target.Config.OsVersion}.");
        }

        HashSet<string>? baselineDigests = !baseline.IsDaemon && !target.IsDaemon
            ? new(StringComparer.Ordinal) : null;
        foreach (IDescriptor layer in baseline.Resolved.Manifest.Layers)
        {
            string digest = layer.Digest ?? throw new InvalidDataException("Layer digest is missing.");
            baselineDigests?.Add(digest);
        }
        long additional = 0;
        foreach (IDescriptor layer in target.Resolved.Manifest.Layers)
        {
            string digest = layer.Digest ?? throw new InvalidDataException("Layer digest is missing.");
            if (baselineDigests is not null && baselineDigests.Add(digest))
            {
                additional = checked(additional + layer.Size);
            }
        }

        Dictionary<string, ImageFileSystemEntry> before = baseline.Entries
            .ToDictionary(static entry => entry.Path, semantics.Comparer);
        Dictionary<string, ImageFileSystemEntry> after = target.Entries
            .ToDictionary(static entry => entry.Path, semantics.Comparer);
        List<ExplorerFileDifference> differences = [];
        List<string> limitations = [];
        foreach (string path in before.Keys.Concat(after.Keys).Distinct(semantics.Comparer)
            .Order(semantics.Comparer))
        {
            before.TryGetValue(path, out ImageFileSystemEntry? old);
            after.TryGetValue(path, out ImageFileSystemEntry? current);
            if (semantics.IsWindows && (old?.Type == ImageFileType.Other || current?.Type == ImageFileType.Other))
            {
                if (limitations.Count < 64)
                {
                    limitations.Add($"File comparison unavailable for '/{path}': {current?.Windows?.UnsupportedReason ?? old?.Windows?.UnsupportedReason ?? "unsupported entry type"}");
                }
                continue;
            }
            if (old is null || current is null ||
                !SameFile(old, current, baseline.Analysis.LiveContents, target.Analysis.LiveContents, semantics))
            {
                differences.Add(new(path, old is null ? LayerChangeKind.Added :
                    current is null ? LayerChangeKind.Deleted : LayerChangeKind.Modified, old, current));
            }
        }

        List<ExplorerPackageDifference> packages = [];
        foreach (InstalledPackageEcosystem ecosystem in Enum.GetValues<InstalledPackageEcosystem>())
        {
            InstalledPackageEcosystemMetadata old = baseline.Packages.Ecosystems[ecosystem];
            InstalledPackageEcosystemMetadata current = target.Packages.Ecosystems[ecosystem];
            if (old.Availability != InstalledPackageMetadataAvailability.Available ||
                current.Availability != InstalledPackageMetadataAvailability.Available)
            {
                continue;
            }
            foreach (string name in old.Packages.Keys.Concat(current.Packages.Keys)
                .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
            {
                old.Packages.TryGetValue(name, out IReadOnlyList<string>? previous);
                current.Packages.TryGetValue(name, out IReadOnlyList<string>? next);
                if (previous is null || next is null || !previous.SequenceEqual(next))
                {
                    packages.Add(new(ecosystem, name, previous is null ? null : string.Join(", ", previous),
                        next is null ? null : string.Join(", ", next)));
                }
            }
        }
        return new(baseline, target, additional, differences, packages) { Limitations = limitations };
    }

    private static bool CompatibleWindowsVariants(ExplorerSession baseline, ExplorerSession target)
    {
        string? before = baseline.Platform?.Variant ?? baseline.Config.Variant;
        string? after = target.Platform?.Variant ?? target.Config.Variant;
        return ExplorerPlatform.CompatibleVariants(before, after,
            baseline.Resolved.ManifestInfo.DockerContentDigest,
            target.Resolved.ManifestInfo.DockerContentDigest);
    }

    internal ExplorerPlatform ComparisonPlatform => ExplorerPlatform.FromConfig(Config, SelectedPlatform);

    internal static string[] ContentDigests(Image config, int layerCount) =>
        ValidateContentDigests(config.RootFilesystem?.DiffIds?.ToArray() ?? [], layerCount);

    private static string[] ValidateContentDigests(string[] ids, int layerCount)
    {
        if (ids.Length != layerCount || ids.Any(string.IsNullOrEmpty))
        {
            throw new InvalidDataException("Verified root filesystem diff IDs are required for cross-source layer comparison.");
        }
        return ids;
    }

    internal string? LayerIdentity(int layer, bool content) =>
        layer >= Resolved.Manifest.Layers.Length ? null :
        content ? (verifiedContentDigests ?? contentDigests.Value)[layer] :
        Resolved.Manifest.Layers[layer].Digest;

    private static bool SameFile(
        ImageFileSystemEntry left, ImageFileSystemEntry right,
        IReadOnlyDictionary<string, ScannedEntry> oldEntries,
        IReadOnlyDictionary<string, ScannedEntry> newEntries,
        ImageFileSystemSemantics semantics)
    {
        if (left.Type != right.Type ||
            !semantics.IsWindows && (left.Mode != right.Mode ||
                left.UserId != right.UserId || left.GroupId != right.GroupId) ||
            left.Size != right.Size || left.LinkTarget != right.LinkTarget || left.Windows != right.Windows)
        {
            return false;
        }
        if (left.Type is not (ImageFileType.File or ImageFileType.HardLink))
        {
            return true;
        }
        if (left.Type == ImageFileType.HardLink &&
            (left.ContentLinkTarget is not null || right.ContentLinkTarget is not null))
        {
            return left.ContentLinkTarget == right.ContentLinkTarget;
        }
        return oldEntries.TryGetValue(left.Path, out ScannedEntry? old) &&
            newEntries.TryGetValue(right.Path, out ScannedEntry? current) &&
            old.ContentHash == current.ContentHash;
    }

    internal static async Task<(IReadOnlyList<ExplorerBaseImage> Bases, string? Warning)> VerifyBasesAsync(
        IDockerRegistryClient client, IDockerRegistryClientFactory factory,
        ImageName image, ResolvedManifest target,
        PlatformOptionsBase options, IReadOnlyList<string>? explicitBases, CancellationToken cancellationToken,
        ExplorerPlatform? platform = null)
    {
        IDictionary<string, string>? annotations =
            (target.Manifest as OciImageManifest)?.Annotations;
        string? annotatedName = annotations is not null &&
            annotations.TryGetValue("org.opencontainers.image.base.name", out string? name)
            ? name : null;
        string? annotatedDigest = annotations is not null &&
            annotations.TryGetValue("org.opencontainers.image.base.digest", out string? digest)
            ? digest : null;
        if (explicitBases is not { Count: > 0 } && string.IsNullOrEmpty(annotatedName))
        {
            return ([], string.IsNullOrEmpty(annotatedDigest) ? null :
                "A base digest is annotated without a base name; the base boundary cannot be verified.");
        }

        ImageName[] baseNames = explicitBases?.Select(ImageName.Parse).ToArray() ?? [];
        ImageName? annotationName = string.IsNullOrEmpty(annotatedName) ? null :
            ImageName.Parse(annotatedName);
        ImageName[] candidates = baseNames.Length == 0 && annotationName is not null ? [annotationName] : baseNames;
        List<(ExplorerBaseImage Base, string Digest)> verified = [];
        foreach (ImageName baseName in candidates)
        {
            ResolvedManifest resolved;
            if (baseNames.Length == 0)
            {
                try
                {
                    resolved = await ResolveAsync(baseName);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    return ([], $"Annotated base '{baseName}' could not be verified: {exception.Message}");
                }
            }
            else
            {
                resolved = await ResolveAsync(baseName);
            }
            int count = VerifyPrefix(target.Manifest, resolved.Manifest);
            verified.Add((new(baseName.ToString(), count), resolved.ManifestInfo.DockerContentDigest));
        }
        verified.Sort((left, right) => left.Base.LayerCount.CompareTo(right.Base.LayerCount));
        if (baseNames.Length > 1)
        {
            for (int index = 1; index < verified.Count; index++)
            {
                if (verified[index - 1].Base.LayerCount == verified[index].Base.LayerCount)
                {
                    throw new InvalidOperationException(
                        $"Base images '{verified[index - 1].Base.Name}' and '{verified[index].Base.Name}' have the same layer boundary; each base must add layers.");
                }
            }
            if (verified[0].Base.LayerCount == 0)
            {
                throw new InvalidOperationException(
                    $"Base image '{verified[0].Base.Name}' has no layers; each base must add layers.");
            }
        }
        (ExplorerBaseImage deepest, string deepestDigest) = verified[^1];
        if (baseNames.Length > 0 && annotationName is not null &&
            deepest.Name != annotationName.ToString())
        {
            ResolvedManifest annotated = await ResolveAsync(annotationName);
            if (deepestDigest != annotated.ManifestInfo.DockerContentDigest)
            {
                throw new InvalidOperationException(
                    $"The explicit base image '{deepest.Name}' disagrees with the annotation '{annotationName}'.");
            }
        }
        if (!string.IsNullOrEmpty(annotatedDigest) &&
            annotatedDigest != deepestDigest)
        {
            throw new InvalidOperationException(
                $"The base annotation digest '{annotatedDigest}' does not match '{deepest.Name}'.");
        }
        return ([.. verified.Select(entry => entry.Base)], null);

        async Task<ResolvedManifest> ResolveAsync(ImageName name)
        {
            if (name.Registry == image.Registry)
            {
                return await ResolveWithAsync(client, name);
            }
            using IDockerRegistryClient other = await factory.GetClientAsync(
                name.Registry, cancellationToken);
            return await ResolveWithAsync(other, name);
        }

        // Platform options can't name a variant such as arm/v7, so a chosen platform is matched exactly.
        async Task<ResolvedManifest> ResolveWithAsync(IDockerRegistryClient registry, ImageName name) =>
            platform is null
                ? await ManifestHelper.GetResolvedManifestAsync(registry, name, options, cancellationToken)
                : (await ExplorerSource.ResolveAsync(registry, name, options, platform, cancellationToken)).Resolved;
    }

    internal static int VerifyPrefix(IImageManifest target, IImageManifest candidate)
    {
        if (candidate.Layers.Length > target.Layers.Length)
        {
            throw new InvalidOperationException("The annotated base has more layers than the explored image.");
        }
        for (int index = 0; index < candidate.Layers.Length; index++)
        {
            if (candidate.Layers[index].Digest != target.Layers[index].Digest)
            {
                throw new InvalidOperationException(
                    $"Base layer {index} does not match the explored image's layer prefix.");
            }
        }
        return candidate.Layers.Length;
    }
}
