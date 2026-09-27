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
    IReadOnlyList<ExplorerPackageDifference> Packages);

internal sealed class ExplorerSession
{
    public required ImageName Image { get; init; }
    public required ResolvedManifest Resolved { get; init; }
    public required Image Config { get; init; }
    public required ImageFileSystem Files { get; init; }
    public required ImageAnalysisResult Analysis { get; init; }
    public required IReadOnlyList<ImageFileSystemEntry> Entries { get; init; }
    public required InstalledPackageMetadata Packages { get; init; }
    public int? BaseLayerCount { get; init; }
    public string? BaseWarning { get; init; }
    public Func<string, CancellationToken, Task<ExplorerComparison>>? CompareAsync { get; set; }
    public Func<CancellationToken, Task<IReadOnlyList<string>>>? TagsAsync { get; set; }

    public string? BaseName { get; init; }
    public IReadOnlyList<ExplorerBaseImage> BaseImages { get; init; } = [];
    public ExplorerPlatform? Platform { get; init; }
    public IReadOnlyList<ExplorerPlatform> Platforms { get; init; } = [];

    public static async Task<ExplorerSession> LoadAsync(
        IDockerRegistryClient client, IDockerRegistryClientFactory factory,
        ImageName image, PlatformOptionsBase options,
        LayerStore store, IReadOnlyList<string>? baseImages, CancellationToken cancellationToken,
        IProgress<ImageIndexProgress>? progress = null, ExplorerPlatform? exactPlatform = null)
    {
        ExplorerSource source = await ExplorerSource.OpenAsync(
            client, factory, image, options, baseImages, cancellationToken, exactPlatform: exactPlatform);
        return await CreateAsync(client, source, store, null, cancellationToken, progress);
    }

    public static async Task<ExplorerSession> CreateAsync(
        IDockerRegistryClient client, ExplorerSource source, LayerStore store,
        IReadOnlyDictionary<int, StoredLayerIndex>? indexes, CancellationToken cancellationToken,
        IProgress<ImageIndexProgress>? progress = null)
    {
        PlatformOptionsBase options = source.Platform is null ? new() : ExplorerSource.ForPlatform(source.Platform);
        ImageFileSystem files = await ImageFileSystem.CreateAsync(client, source.Image, options,
            cancellationToken, store, progress: progress, resolvedManifest: source.Resolved,
            imageConfig: source.Config, requireLayerIndexes: true, layerIndexes: indexes);
        try
        {
            ImageAnalysisResult analysis = files.Analyze();
            InstalledPackageMetadata packages = await InstalledPackageReader.ReadAsync(files, cancellationToken);
            return new ExplorerSession
            {
                Image = source.Image,
                Resolved = source.Resolved,
                Config = source.Config,
                Files = files,
                Analysis = analysis,
                Entries = files.List(null, true, false),
                Packages = packages,
                BaseLayerCount = source.BaseLayerCount,
                BaseWarning = source.BaseWarning,
                BaseName = source.BaseName,
                BaseImages = source.BaseImages,
                Platform = source.Platform,
                Platforms = source.Platforms
            };
        }
        catch
        {
            await files.DisposeAsync();
            throw;
        }
    }
    internal static ExplorerComparison Compare(ExplorerSession baseline, ExplorerSession target)
    {
        if (!string.Equals(baseline.Config.Os, target.Config.Os, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(baseline.Config.Architecture, target.Config.Architecture,
                StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(baseline.Config.OsVersion, target.Config.OsVersion,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Images must use the same platform: {baseline.Config.Os}/{baseline.Config.Architecture} " +
                $"and {target.Config.Os}/{target.Config.Architecture}.");
        }

        HashSet<string> baselineDigests = baseline.Resolved.Manifest.Layers
            .Select(layer => layer.Digest ?? throw new InvalidDataException("Layer digest is missing."))
            .ToHashSet(StringComparer.Ordinal);
        long additional = 0;
        foreach (IDescriptor layer in target.Resolved.Manifest.Layers)
        {
            string digest = layer.Digest ?? throw new InvalidDataException("Layer digest is missing.");
            if (!baselineDigests.Contains(digest))
            {
                additional = checked(additional + layer.Size);
                baselineDigests.Add(digest);
            }
        }

        Dictionary<string, ImageFileSystemEntry> before = baseline.Entries
            .ToDictionary(entry => entry.Path, StringComparer.Ordinal);
        Dictionary<string, ImageFileSystemEntry> after = target.Entries
            .ToDictionary(entry => entry.Path, StringComparer.Ordinal);
        List<ExplorerFileDifference> differences = [];
        foreach (string path in before.Keys.Concat(after.Keys).Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal))
        {
            before.TryGetValue(path, out ImageFileSystemEntry? old);
            after.TryGetValue(path, out ImageFileSystemEntry? current);
            if (old is null || current is null ||
                !SameFile(old, current, baseline.Analysis.LiveEntries, target.Analysis.LiveEntries))
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
        return new(baseline, target, additional, differences, packages);
    }

    private static bool SameFile(
        ImageFileSystemEntry left, ImageFileSystemEntry right,
        IReadOnlyDictionary<string, ScannedEntry> oldEntries,
        IReadOnlyDictionary<string, ScannedEntry> newEntries)
    {
        if (left.Type != right.Type || left.Mode != right.Mode ||
            left.UserId != right.UserId || left.GroupId != right.GroupId ||
            left.Size != right.Size || left.LinkTarget != right.LinkTarget)
        {
            return false;
        }
        if (left.Type is not (ImageFileType.File or ImageFileType.HardLink))
        {
            return true;
        }
        return oldEntries.TryGetValue(left.ContentPath ?? left.Path, out ScannedEntry? old) &&
            newEntries.TryGetValue(right.ContentPath ?? right.Path, out ScannedEntry? current) &&
            old.ContentHash == current.ContentHash;
    }

    internal static async Task<(int? Count, string? Name, string? Warning)> VerifyBaseAsync(
        IDockerRegistryClient client, IDockerRegistryClientFactory factory,
        ImageName image, ResolvedManifest target, PlatformOptionsBase options,
        string? explicitBase, CancellationToken cancellationToken, ExplorerPlatform? platform = null)
    {
        (IReadOnlyList<ExplorerBaseImage> bases, string? warning) = await VerifyBasesAsync(
            client, factory, image, target, options,
            explicitBase is null ? null : [explicitBase], cancellationToken, platform);
        return (bases.LastOrDefault()?.LayerCount, bases.LastOrDefault()?.Name, warning);
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
        ImageName annotatedReference = annotationName!;
        List<(ExplorerBaseImage Base, string Digest)> verified = [];
        foreach (ImageName baseName in baseNames.Length == 0 ? [annotatedReference] : baseNames)
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
                    return ([], $"Annotated base '{annotatedReference}' could not be verified: {exception.Message}");
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
        return (verified.Select(entry => entry.Base).ToArray(), null);

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
