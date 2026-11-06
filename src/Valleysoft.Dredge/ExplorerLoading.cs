using System.Text.RegularExpressions;
using Valleysoft.DockerRegistryClient;
using Valleysoft.DockerRegistryClient.Models.Images;
using Valleysoft.DockerRegistryClient.Models.Manifests;
using Valleysoft.DockerRegistryClient.Models.Manifests.Oci;
using Valleysoft.Dredge.Commands;

namespace Valleysoft.Dredge;

internal sealed record ExplorerPlatform(string Os, string Architecture, string? Variant, string? OsVersion)
{
    internal string? ManifestDigest { get; init; }

    public bool Equals(ExplorerPlatform? other) =>
        other is not null && Os == other.Os && Architecture == other.Architecture &&
        Variant == other.Variant && OsVersion == other.OsVersion;

    public override int GetHashCode() => HashCode.Combine(Os, Architecture, Variant, OsVersion);

    internal static bool CompatibleVariants(
        string? before, string? after, string? beforeDigest, string? afterDigest)
    {
        before = string.IsNullOrEmpty(before) ? null : before;
        after = string.IsNullOrEmpty(after) ? null : after;
        return before == after ||
            (before is null || after is null) &&
            !string.IsNullOrEmpty(beforeDigest) && beforeDigest == afterDigest;
    }

    public bool IsEquivalentTo(ExplorerPlatform other) =>
        HasSamePlatform(other) &&
        string.Equals(NormalizedVariant, other.NormalizedVariant, StringComparison.OrdinalIgnoreCase);

    public bool IsCompatibleWith(ExplorerPlatform other, bool allowWindowsVersionDifference = false) =>
        HasSamePlatform(other, allowWindowsVersionDifference) &&
        (HasUnknownVariant || other.HasUnknownVariant ||
            string.Equals(NormalizedVariant, other.NormalizedVariant, StringComparison.OrdinalIgnoreCase));

    private bool HasSamePlatform(ExplorerPlatform other, bool allowWindowsVersionDifference = false) =>
        string.Equals(Os, other.Os, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(Architecture, other.Architecture, StringComparison.OrdinalIgnoreCase) &&
        (allowWindowsVersionDifference && string.Equals(Os, "windows", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(OsVersion ?? "", other.OsVersion ?? "", StringComparison.OrdinalIgnoreCase));

    private bool HasUnknownVariant => string.IsNullOrEmpty(Variant) &&
        !string.Equals(Architecture, "arm64", StringComparison.OrdinalIgnoreCase);

    private string NormalizedVariant =>
        string.Equals(Architecture, "arm64", StringComparison.OrdinalIgnoreCase) &&
            (string.IsNullOrEmpty(Variant) || string.Equals(Variant, "v8", StringComparison.OrdinalIgnoreCase))
            ? "v8" : Variant ?? "";

    internal static ExplorerPlatform FromConfig(Image config, ExplorerPlatform? selectedPlatform = null) =>
        new(config.Os, config.Architecture,
            string.IsNullOrEmpty(config.Variant) ? selectedPlatform?.Variant : config.Variant,
            string.IsNullOrEmpty(config.OsVersion) ? selectedPlatform?.OsVersion : config.OsVersion);

    internal static ExplorerPlatform FromManifest(ManifestPlatform platform) =>
        new(platform.Os ?? "", platform.Architecture ?? "",
            string.IsNullOrEmpty(platform.Variant) ? null : platform.Variant,
            string.IsNullOrEmpty(platform.OsVersion) ? null : platform.OsVersion);

    public override string ToString()
    {
        string value = $"{Os}/{Architecture}";
        if (!string.IsNullOrEmpty(Variant))
        {
            value += "/" + Variant;
        }
        if (!string.IsNullOrEmpty(OsVersion))
        {
            value += " " + OsVersion;
        }
        return value;
    }
}

internal sealed record ExplorerBaseImage(string Name, int LayerCount);

// Registry resolution stays lightweight for interactive platform selection;
// daemon snapshots already own their exported layer content.
internal sealed class ExplorerSource : IAsyncDisposable
{
    private bool disposeRegistryClient;
    private int disposed;
    public ResolvedImage? Source { get; init; }
    public IImageSourceFactory? SourceFactory { get; init; }
    public PlatformOptionsBase? SourceOptions { get; init; }
    public string? RequestedReference { get; init; }
    public IDockerRegistryClient? RegistryClient { get; private set; }
    public bool IsDaemon => Source?.IsDaemon == true;
    public string Reference => RequestedReference ?? Image.ToString();
    public required ImageName Image { get; init; }
    public required ResolvedManifest Resolved { get; init; }
    public required Image Config { get; init; }
    public required IReadOnlyList<ExplorerPlatform> Platforms { get; init; }
    public ExplorerPlatform? Platform { get; init; }
    public IReadOnlyList<ExplorerBaseImage> BaseImages { get; init; } = [];
    public string? BaseWarning { get; init; }

    public int LayerCount => Resolved.Manifest.Layers.Length;

    internal IImageContentSource GetContent(IDockerRegistryClient? client)
    {
        if (Source is { } source)
        {
            return source.Content;
        }
        if (Resolved.Manifest.Config?.Digest is null)
        {
            throw new NotSupportedException($"Could not resolve the image config digest of '{Image}'.");
        }
        return new RegistryImageContentSource(
            client ?? RegistryClient ?? throw new InvalidOperationException("Registry client is unavailable."), Image);
    }

    public static async Task<ExplorerSource> OpenAsync(
        IImageSourceFactory sourceFactory, IDockerRegistryClientFactory registryFactory,
        string reference, PlatformOptionsBase options, IReadOnlyList<string>? baseImages,
        CancellationToken cancellationToken,
        Func<IReadOnlyList<ExplorerPlatform>, ExplorerPlatform?>? choosePlatform = null,
        ExplorerPlatform? exactPlatform = null, LayerStore? store = null,
        ResolvedManifest? resolvedManifest = null, IDockerRegistryClient? registryClient = null,
        string? daemonSnapshotReference = null, bool comparing = false,
        Func<IReadOnlyList<ExplorerPlatform>, CancellationToken, Task<ExplorerPlatform?>>? chooseComparisonPlatform = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (exactPlatform is not null)
        {
            options = ForPlatform(exactPlatform, options);
        }
        ImageSourceReference parsed = ImageSourceReference.Parse(reference);
        if (!parsed.IsDaemon)
        {
            bool ownsClient = registryClient is null;
            IDockerRegistryClient client = registryClient ??
                await registryFactory.GetClientAsync(parsed.Registry, cancellationToken);
            try
            {
                bool mixedBases = baseImages?.Any(value => ImageSourceReference.Parse(value).IsDaemon) == true;
                ExplorerSource result = await OpenAsync(client, registryFactory, ImageName.Parse(parsed.Value),
                    options, mixedBases ? null : baseImages?.Select(value => ImageSourceReference.Parse(value).Value).ToArray(),
                    cancellationToken, choosePlatform, exactPlatform: exactPlatform,
                    skipBaseVerification: mixedBases, resolvedManifest: resolvedManifest, comparing: comparing,
                    chooseComparisonPlatform: chooseComparisonPlatform);
                (IReadOnlyList<ExplorerBaseImage> bases, string? warning) = mixedBases
                    ? await VerifySourceBasesAsync(sourceFactory, result.Config, result.Resolved, false,
                        options, baseImages, cancellationToken, new RegistryImageContentSource(client, result.Image),
                        store: store, registryFactory: registryFactory, exactPlatform: result.Platform)
                    : (result.BaseImages, result.BaseWarning);
                return new()
                {
                    Image = result.Image, Resolved = result.Resolved, Config = result.Config,
                    Platforms = result.Platforms, Platform = result.Platform,
                    BaseImages = bases, BaseWarning = warning,
                    SourceFactory = sourceFactory, SourceOptions = options, RequestedReference = reference,
                    RegistryClient = client, disposeRegistryClient = ownsClient
                };
            }
            catch
            {
                if (ownsClient)
                {
                    client.Dispose();
                }
                throw;
            }
        }

        ResolvedImage source = await sourceFactory.OpenAsync(
            daemonSnapshotReference ?? reference, options, cancellationToken, ImageSourceRequirements.LinuxFileSystem);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!string.Equals(source.Configuration.Os, "linux", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(source.Configuration.Os, "windows", StringComparison.OrdinalIgnoreCase))
            {
                throw new NotSupportedException(
                    $"The image explorer supports Linux and Windows images (found '{source.Configuration.Os}').");
            }
            ExplorerPlatform actualPlatform = ExplorerPlatform.FromConfig(source.Configuration, source.SelectedPlatform);
            if (exactPlatform is not null && !actualPlatform.IsCompatibleWith(exactPlatform))
            {
                throw new InvalidOperationException(
                    $"Docker image platform '{actualPlatform}' does not match the selected platform '{exactPlatform}'.");
            }
            (IReadOnlyList<ExplorerBaseImage> bases, string? warning) = await VerifySourceBasesAsync(
                sourceFactory, source.Configuration, source.Resolved, true, options, baseImages, cancellationToken,
                targetContent: source.Content, store: store, registryFactory: registryFactory, exactPlatform: actualPlatform);
            return new()
            {
                Source = source, SourceFactory = sourceFactory, SourceOptions = options, RequestedReference = reference,
                Image = source.Name, Resolved = source.Resolved, Config = source.Configuration,
                Platforms = [], Platform = actualPlatform,
                BaseImages = bases, BaseWarning = warning
            };
        }
        catch
        {
            await source.DisposeAsync();
            throw;
        }
    }

    internal static async Task<(IReadOnlyList<ExplorerBaseImage>, string?)> VerifySourceBasesAsync(
        IImageSourceFactory factory, Image config, ResolvedManifest target, bool isDaemon,
        PlatformOptionsBase options, IReadOnlyList<string>? references, CancellationToken cancellationToken,
        IImageContentSource? targetContent = null, LayerStore? store = null,
        IDockerRegistryClientFactory? registryFactory = null, ExplorerPlatform? exactPlatform = null)
    {
        if (references is not { Count: > 0 })
        {
            bool annotated = (target.Manifest as OciImageManifest)?.Annotations?.Keys.Any(
                key => key is "org.opencontainers.image.base.name" or "org.opencontainers.image.base.digest") == true;
            return ([], annotated ? "Daemon base annotations cannot be verified without an explicit base image; registry manifest identity is unavailable." : null);
        }
        ExplorerPlatform targetPlatform = ExplorerPlatform.FromConfig(config, exactPlatform);
        options = ForPlatform(targetPlatform, options);
        List<(ExplorerBaseImage Base, string? Digest)> verified = [];
        await using LayerStore? ownedStore = store is null ? LayerStore.Create() : null;
        LayerStore verificationStore = store ?? ownedStore!;
        foreach (string reference in references)
        {
            bool candidateIsDaemon = ImageSourceReference.Parse(reference).IsDaemon;
            await using ExplorerSource? registryCandidate = !candidateIsDaemon && registryFactory is not null
                ? await OpenRegistryBaseAsync(reference) : null;
            await using ResolvedImage? candidate = registryCandidate is null
                ? await factory.OpenAsync(reference, options, cancellationToken, ImageSourceRequirements.LinuxLayerContent)
                : null;
            Image candidateConfig = registryCandidate?.Config ?? candidate!.Configuration;
            ResolvedManifest candidateManifest = registryCandidate?.Resolved ?? candidate!.Resolved;
            IImageContentSource candidateContent = registryCandidate is null ? candidate!.Content :
                new RegistryImageContentSource(registryCandidate.RegistryClient!, registryCandidate.Image);
            ExplorerPlatform candidatePlatform = ExplorerPlatform.FromConfig(candidateConfig,
                registryCandidate?.Platform ?? candidate?.SelectedPlatform);
            cancellationToken.ThrowIfCancellationRequested();
            if (!targetPlatform.IsCompatibleWith(candidatePlatform))
            {
                throw new InvalidOperationException($"Base image '{reference}' uses a different platform.");
            }
            int count;
            if (isDaemon || candidateIsDaemon)
            {
                string[] targetIds = ExplorerSession.ContentDigests(config, target.Manifest.Layers.Length);
                string[] baseIds = ExplorerSession.ContentDigests(candidateConfig,
                    candidateManifest.Manifest.Layers.Length);
                if (baseIds.Length > targetIds.Length)
                {
                    throw new InvalidOperationException($"Base image '{reference}' does not match the explored image's content-layer prefix.");
                }
                if (!candidateIsDaemon || !baseIds.All(DockerImageArchive.IsSha256))
                {
                    baseIds = await ExplorerContentIdentity.VerifyAsync(candidateContent, candidateConfig,
                        candidateManifest, verificationStore, cancellationToken, indexLayers: true);
                }
                if (!isDaemon || !targetIds.Take(baseIds.Length).All(DockerImageArchive.IsSha256))
                {
                    targetIds = await ExplorerContentIdentity.VerifyAsync(
                        targetContent ?? throw new InvalidOperationException("Image content source is unavailable."),
                        config, target, verificationStore, cancellationToken, baseIds.Length, indexLayers: true);
                }
                if (!baseIds.SequenceEqual(targetIds.Take(baseIds.Length), StringComparer.Ordinal))
                {
                    throw new InvalidOperationException($"Base image '{reference}' does not match the explored image's content-layer prefix.");
                }
                count = baseIds.Length;
            }
            else
            {
                count = ExplorerSession.VerifyPrefix(target.Manifest, candidateManifest.Manifest);
            }
            verified.Add((new(reference, count),
                candidateIsDaemon ? null : candidateManifest.ManifestInfo.DockerContentDigest));
        }
        verified.Sort((left, right) => left.Base.LayerCount.CompareTo(right.Base.LayerCount));
        if (verified.Count > 1 && (verified[0].Base.LayerCount == 0 ||
            verified.Zip(verified.Skip(1)).Any(pair => pair.First.Base.LayerCount == pair.Second.Base.LayerCount)))
        {
            throw new InvalidOperationException("Each base image must add layers and have a distinct layer boundary.");
        }
        IDictionary<string, string>? annotations = (target.Manifest as OciImageManifest)?.Annotations;
        (ExplorerBaseImage deepest, string? deepestDigest) = verified[^1];
        if (annotations?.TryGetValue("org.opencontainers.image.base.digest", out string? annotatedDigest) == true &&
            deepestDigest is not null && annotatedDigest != deepestDigest)
        {
            throw new InvalidOperationException(
                $"The base annotation digest '{annotatedDigest}' does not match '{deepest.Name}'.");
        }
        bool uncertainName = false;
        if (annotations?.TryGetValue("org.opencontainers.image.base.name", out string? annotatedName) == true &&
            !string.IsNullOrEmpty(annotatedName))
        {
            if (deepestDigest is null || ImageSourceReference.Parse(annotatedName).IsDaemon)
            {
                uncertainName = true;
            }
            else if (ImageSourceReference.Parse(deepest.Name).GetImageName().ToString() !=
                ImageSourceReference.Parse(annotatedName).GetImageName().ToString())
            {
                if (registryFactory is not null)
                {
                    await using ExplorerSource annotated = await OpenRegistryBaseAsync(annotatedName);
                    if (annotated.Resolved.ManifestInfo.DockerContentDigest != deepestDigest)
                    {
                        throw new InvalidOperationException(
                            $"The explicit base image '{deepest.Name}' disagrees with the annotation '{annotatedName}'.");
                    }
                }
                else
                {
                    await using ResolvedImage annotated = await factory.OpenAsync(
                        annotatedName, options, cancellationToken, ImageSourceRequirements.Metadata);
                    if (annotated.IsDaemon)
                    {
                        uncertainName = true;
                    }
                    else if (annotated.Resolved.ManifestInfo.DockerContentDigest != deepestDigest)
                    {
                        throw new InvalidOperationException(
                            $"The explicit base image '{deepest.Name}' disagrees with the annotation '{annotatedName}'.");
                    }
                }
            }
        }
        bool uncertainDigest = deepestDigest is null &&
            annotations?.ContainsKey("org.opencontainers.image.base.digest") == true;
        string? warning = uncertainName || uncertainDigest
            ? "Explicit base content verified; registry base annotation digests cannot be verified against daemon image IDs."
            : null;
        return ([.. verified.Select(entry => entry.Base)], warning);

        async Task<ExplorerSource> OpenRegistryBaseAsync(string reference)
        {
            ImageSourceReference parsed = ImageSourceReference.Parse(reference);
            IDockerRegistryClient client = await registryFactory!.GetClientAsync(parsed.Registry, cancellationToken);
            try
            {
                ExplorerSource result = await OpenAsync(client, registryFactory!, ImageName.Parse(parsed.Value),
                    options, null, cancellationToken, exactPlatform: targetPlatform, skipBaseVerification: true);
                result.RegistryClient = client;
                result.disposeRegistryClient = true;
                return result;
            }
            catch
            {
                client.Dispose();
                throw;
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            return;
        }
        try
        {
            if (Source is not null)
            {
                await Source.DisposeAsync();
            }
        }
        finally
        {
            if (disposeRegistryClient)
            {
                RegistryClient?.Dispose();
            }
        }
    }

    public static async Task<ExplorerSource> OpenAsync(
        IDockerRegistryClient client, IDockerRegistryClientFactory factory,
        ImageName image, PlatformOptionsBase options, IReadOnlyList<string>? baseImages,
        CancellationToken cancellationToken,
        Func<IReadOnlyList<ExplorerPlatform>, ExplorerPlatform?>? choosePlatform = null,
        IAppSettingsStore? settingsStore = null, ExplorerPlatform? exactPlatform = null,
        ResolvedManifest? resolvedManifest = null, bool skipBaseVerification = false, bool comparing = false,
        Func<IReadOnlyList<ExplorerPlatform>, CancellationToken, Task<ExplorerPlatform?>>? chooseComparisonPlatform = null)
    {
        (ResolvedManifest resolved, IReadOnlyList<ExplorerPlatform> platforms, ExplorerPlatform? platform) =
            resolvedManifest is null
                ? await ResolveAsync(client, image, options, exactPlatform, cancellationToken, choosePlatform,
                    settingsStore, comparing, chooseComparisonPlatform)
                : (resolvedManifest, [], resolvedManifest.SelectedPlatform ?? exactPlatform);
        IImageManifest manifest = resolved.Manifest;
        string configDigest = manifest.Config?.Digest ??
            throw new InvalidDataException($"Image '{image}' has no config digest.");
        Image config = await client.Blobs.GetImageAsync(image.Repo, configDigest, cancellationToken);
        if (!string.Equals(config.Os, "linux", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(config.Os, "windows", StringComparison.OrdinalIgnoreCase))
        {
            throw new NotSupportedException(
                $"The image explorer supports Linux and Windows images (found '{config.Os}').");
        }
        ExplorerPlatform? expectedPlatform = comparing ? platform ?? exactPlatform : exactPlatform;
        bool allowWindowsVersionDifference = comparing && resolved.SelectedPlatform is null;
        if (resolved.SelectedPlatform is null)
        {
            platform = ExplorerPlatform.FromConfig(config, platform);
        }
        if (platform is not null && string.Equals(config.Os, "windows", StringComparison.OrdinalIgnoreCase))
        {
            platform = platform with { ManifestDigest = resolved.ManifestInfo.DockerContentDigest };
        }
        ExplorerPlatform actualPlatform = ExplorerPlatform.FromConfig(config, platform);
        if (expectedPlatform is not null &&
            !actualPlatform.IsCompatibleWith(expectedPlatform, allowWindowsVersionDifference))
        {
            throw new InvalidOperationException(
                $"Image platform '{actualPlatform}' does not match the selected platform '{expectedPlatform}'.");
        }
        platform ??= actualPlatform;
        PlatformOptionsBase resolvedOptions = ForPlatform(actualPlatform, options);

        (IReadOnlyList<ExplorerBaseImage> verifiedBases, string? warning) = skipBaseVerification
            ? ([], null)
            : await ExplorerSession.VerifyBasesAsync(
                client, factory, image, resolved, resolvedOptions, baseImages, cancellationToken, platform);
        return new ExplorerSource
        {
            Image = image,
            Resolved = resolved,
            Config = config,
            Platforms = platforms,
            Platform = platform,
            BaseImages = verifiedBases,
            BaseWarning = warning,
            SourceOptions = options
        };
    }

    // Resolves a tag or digest to one image manifest. An exact platform, which
    // includes the variant that platform options can't express, wins over options.
    internal static async Task<(ResolvedManifest Resolved, IReadOnlyList<ExplorerPlatform> Platforms, ExplorerPlatform? Platform)> ResolveAsync(
        IDockerRegistryClient client, ImageName image, PlatformOptionsBase options,
        ExplorerPlatform? exactPlatform, CancellationToken cancellationToken,
        Func<IReadOnlyList<ExplorerPlatform>, ExplorerPlatform?>? choosePlatform = null,
        IAppSettingsStore? settingsStore = null,
        bool comparing = false,
        Func<IReadOnlyList<ExplorerPlatform>, CancellationToken, Task<ExplorerPlatform?>>? chooseComparisonPlatform = null)
    {
        ManifestInfo info = await client.Manifests.GetAsync(
            image.Repo, (image.Tag ?? image.Digest)!, cancellationToken);
        (IManifestReference Reference, ExplorerPlatform Platform)[] candidates = GetPlatformCandidates(info.Manifest);
        IReadOnlyList<ExplorerPlatform> platforms = [.. candidates.Select(item => item.Platform).Distinct()];
        ExplorerPlatform? platform = null;
        if (info.Manifest is IManifestList && platforms.Count == 0)
        {
            throw new NotSupportedException($"The image explorer supports Linux and Windows images; '{image}' has no supported platform.");
        }
        if (info.Manifest is IManifestList && exactPlatform is not null)
        {
            var exact = candidates.FirstOrDefault(candidate => candidate.Platform == exactPlatform);
            IManifestReference reference = exact.Reference;
            platform = exact.Platform;
            if (platform is null && comparing && exactPlatform.Os.Equals("windows", StringComparison.OrdinalIgnoreCase))
            {
                (IManifestReference Reference, ExplorerPlatform Platform)[] compatible = [.. candidates.Where(candidate =>
                    string.Equals(candidate.Platform.Os, exactPlatform.Os, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(candidate.Platform.Architecture, exactPlatform.Architecture, StringComparison.OrdinalIgnoreCase) &&
                    ExplorerPlatform.CompatibleVariants(exactPlatform.Variant, candidate.Platform.Variant,
                        exactPlatform.ManifestDigest, candidate.Reference.Digest))
                    .DistinctBy(candidate => candidate.Platform)];
                (IManifestReference Reference, ExplorerPlatform Platform)[] sameVersion = [.. compatible.Where(candidate =>
                    candidate.Platform.OsVersion == exactPlatform.OsVersion)];
                if (sameVersion.Length > 0)
                {
                    compatible = sameVersion;
                }
                platform = compatible.Length == 1 ? compatible[0].Platform :
                    compatible.Length > 1 && chooseComparisonPlatform is not null
                        ? await chooseComparisonPlatform([.. compatible.Select(candidate => candidate.Platform)], cancellationToken) ??
                            throw new OperationCanceledException("No comparison platform was chosen.", cancellationToken)
                        : null;
                if (platform is not null)
                {
                    var selected = compatible.FirstOrDefault(candidate => candidate.Platform == platform);
                    if (selected.Platform is null)
                    {
                        throw new InvalidOperationException("The selected comparison platform is not compatible.");
                    }
                    reference = selected.Reference;
                    platform = selected.Platform;
                }
            }
            else if (platform is null)
            {
                (IManifestReference Reference, ExplorerPlatform Platform)[] compatible =
                [
                    .. candidates.Where(candidate => candidate.Platform.IsCompatibleWith(exactPlatform))
                        .DistinctBy(candidate => candidate.Platform)
                ];
                if (compatible.Length > 1 && chooseComparisonPlatform is null)
                {
                    throw new InvalidOperationException(
                        $"'{image}' has multiple platforms compatible with {exactPlatform}. Available platforms: " +
                        string.Join(", ", compatible.Select(item => item.Platform)));
                }
                platform = compatible.Length == 1 ? compatible[0].Platform :
                    compatible.Length > 1 && chooseComparisonPlatform is not null
                        ? await chooseComparisonPlatform(
                            [.. compatible.Select(candidate => candidate.Platform)], cancellationToken) ??
                            throw new OperationCanceledException("No comparison platform was chosen.", cancellationToken)
                        : null;
                if (platform is not null)
                {
                    var selected = compatible.FirstOrDefault(candidate => candidate.Platform == platform);
                    if (selected.Platform is null)
                    {
                        throw new InvalidOperationException("The selected comparison platform is not compatible.");
                    }
                    reference = selected.Reference;
                    platform = selected.Platform;
                }
            }
            if (platform is null)
            {
                throw new InvalidOperationException(
                    $"'{image}' has no {exactPlatform} platform. Available platforms: " +
                    string.Join(", ", platforms.Select(item => item.ToString())));
            }
            info = await GetPlatformManifestAsync(client, image, reference, cancellationToken);
        }
        else if (info.Manifest is IManifestList)
        {
            (string? os, string? architecture, string? osVersion) =
                options.ResolvePlatform(settingsStore ?? new AppSettingsStore());
            ExplorerPlatform[] matches = [.. platforms.Where(candidate =>
                (os is null || candidate.Os == os) &&
                (architecture is null || candidate.Architecture == architecture) &&
                (osVersion is null || candidate.OsVersion == osVersion))];
            if (matches.Length == 1)
            {
                platform = matches[0];
            }
            else if (matches.Length > 1 && choosePlatform is not null)
            {
                platform = choosePlatform(matches) ??
                    throw new OperationCanceledException("No platform was chosen.");
            }
            else
            {
                throw new InvalidOperationException(matches.Length == 0
                    ? $"No platform in '{image}' matches the requested platform. Available platforms: " +
                        string.Join(", ", platforms.Select(item => item.ToString()))
                    : $"'{image}' has {matches.Length} matching platforms. Use " +
                        $"{PlatformOptionsBase.OsOptionName} and {PlatformOptionsBase.ArchOptionName} to choose one: " +
                        string.Join(", ", matches.Select(item => item.ToString())));
            }
            info = await GetPlatformManifestAsync(client, image,
                candidates.First(candidate => candidate.Platform == platform).Reference, cancellationToken);
        }
        if (info.Manifest is not IImageManifest manifest)
        {
            throw new NotSupportedException(
                $"The image name '{image}' has a media type of '{info.MediaType}' which is not supported.");
        }
        return (new ResolvedManifest(info, manifest, platform), platforms, platform);
    }

    private static Task<ManifestInfo> GetPlatformManifestAsync(IDockerRegistryClient client, ImageName image,
        IManifestReference reference, CancellationToken cancellationToken)
    {
        return client.Manifests.GetAsync(image.Repo,
            reference.Digest ?? throw new InvalidDataException("Manifest digest is missing."),
            cancellationToken);
    }

    public static PlatformOptionsBase ForPlatform(ExplorerPlatform platform, PlatformOptionsBase? connectionOptions = null) => new()
    {
        Os = platform.Os,
        Architecture = platform.Architecture,
        OsVersion = platform.OsVersion,
        DockerHost = connectionOptions?.DockerHost,
        DockerContext = connectionOptions?.DockerContext,
        UsePlatformDefaults = false
    };

    internal static IReadOnlyList<ExplorerPlatform> GetPlatforms(IManifest manifest) =>
        [.. GetPlatformCandidates(manifest).Select(item => item.Platform).Distinct()];

    private static (IManifestReference Reference, ExplorerPlatform Platform)[] GetPlatformCandidates(IManifest manifest) =>
        manifest is IManifestList list
            ? list.Manifests
                .Where(static item => item.Platform is { } platform &&
                    (string.Equals(platform.Os, "linux", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(platform.Os, "windows", StringComparison.OrdinalIgnoreCase)))
                .Select(static item => (item, ExplorerPlatform.FromManifest(item.Platform!)))
                .ToArray()
            : [];

}

internal enum ExplorerLayerState { Waiting, Indexing, Ready, Failed }

// Indexes layers in parallel. Waiting layers are taken in order, except that a
// prioritized layer jumps the queue. Failures stay failed until retried.
internal sealed class ExplorerLayerIndexer
{
    private readonly Lock sync = new();
    private readonly Func<int, IProgress<long>, CancellationToken, Task<StoredLayerIndex>> indexAsync;
    private readonly int layerCount;
    private readonly int concurrency;
    private readonly List<int> pending;
    private readonly ExplorerLayerState[] states;
    private readonly Dictionary<int, StoredLayerIndex> indexes = [];
    private readonly List<Task> workers = [];
    private readonly CancellationTokenSource stopSource = new();
    private CancellationTokenSource? linkedSource;
    private Task? stoppingTask;
    private bool stopping;
    private int running;
    private CancellationToken cancellationToken;
    private bool started;

    public ExplorerLayerIndexer(
        int layerCount,
        Func<int, IProgress<long>, CancellationToken, Task<StoredLayerIndex>> indexAsync,
        int concurrency = 3)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(layerCount);
        ArgumentOutOfRangeException.ThrowIfLessThan(concurrency, 1);
        this.layerCount = layerCount;
        this.indexAsync = indexAsync;
        this.concurrency = concurrency;
        pending = [.. Enumerable.Range(0, layerCount)];
        states = new ExplorerLayerState[layerCount];
    }

    public static ExplorerLayerIndexer Create(
        IDockerRegistryClient? client, ExplorerSource source, LayerStore store, int concurrency = 3)
    {
        IImageManifest manifest = source.Resolved.Manifest;
        IImageContentSource content = source.GetContent(client);
        return new(manifest.Layers.Length, (layer, progress, token) =>
        {
            IDescriptor descriptor = manifest.Layers[layer];
            ImageFileSystemSemantics semantics = ImageFileSystemSemantics.ForOs(source.Config.Os);
            semantics.ValidateLayerMediaType(descriptor.MediaType);
            string digest = descriptor.Digest ??
                throw new InvalidDataException($"Layer {layer} has no digest.");
            string[]? diffIds = source.Config.RootFilesystem?.DiffIds;
            string? expectedDiffId = diffIds is not null && diffIds.Length == manifest.Layers.Length
                ? diffIds[layer] : null;
            return store.GetIndexAsync(content, new(layer, digest), descriptor.Size, token, progress,
                expectedDiffId: expectedDiffId, semantics: semantics);
        }, concurrency);
    }

    public event Action<int, long>? Progress;
    public event Action<int>? Started;
    public event Action<int, StoredLayerIndex>? Indexed;
    public event Action<int, Exception>? Failed;
    public event Action<IReadOnlyDictionary<int, StoredLayerIndex>>? Completed;

    public IReadOnlyDictionary<int, StoredLayerIndex> Snapshot()
    {
        lock (sync)
        {
            return new Dictionary<int, StoredLayerIndex>(indexes);
        }
    }

    public void Start(CancellationToken token)
    {
        lock (sync)
        {
            ObjectDisposedException.ThrowIf(stopping, this);
            if (started)
            {
                throw new InvalidOperationException("The indexer has already started.");
            }
            started = true;
            linkedSource = CancellationTokenSource.CreateLinkedTokenSource(token, stopSource.Token);
            cancellationToken = linkedSource.Token;
            if (layerCount == 0)
            {
                workers.Add(Task.Run(() =>
                {
                    if (!Volatile.Read(ref stopping) && !cancellationToken.IsCancellationRequested)
                    {
                        Completed?.Invoke(new Dictionary<int, StoredLayerIndex>());
                    }
                }));
                return;
            }
            FillWorkers();
        }
    }

    public void Prioritize(int layer)
    {
        lock (sync)
        {
            if (stopping)
            {
                return;
            }
            if (pending.Remove(layer))
            {
                pending.Insert(0, layer);
            }
        }
    }

    public bool Retry(int layer)
    {
        lock (sync)
        {
            if (stopping || states[layer] != ExplorerLayerState.Failed)
            {
                return false;
            }
            states[layer] = ExplorerLayerState.Waiting;
            pending.Insert(0, layer);
            if (started)
            {
                FillWorkers();
            }
            return true;
        }
    }

    private void FillWorkers()
    {
        while (!stopping && !cancellationToken.IsCancellationRequested && running < concurrency && pending.Count > 0)
        {
            running++;
            workers.Add(Task.Run(WorkAsync, CancellationToken.None));
        }
    }

    private async Task WorkAsync()
    {
        while (true)
        {
            int layer;
            lock (sync)
            {
                if (pending.Count == 0 || cancellationToken.IsCancellationRequested)
                {
                    running--;
                    return;
                }
                layer = pending[0];
                pending.RemoveAt(0);
                states[layer] = ExplorerLayerState.Indexing;
            }

            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                Started?.Invoke(layer);
                StoredLayerIndex index = await indexAsync(layer,
                    new InlineProgress<long>(bytes =>
                    {
                        if (!Volatile.Read(ref stopping) && !cancellationToken.IsCancellationRequested)
                        {
                            Progress?.Invoke(layer, bytes);
                        }
                    }), cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                IReadOnlyDictionary<int, StoredLayerIndex>? complete = null;
                lock (sync)
                {
                    if (stopping)
                    {
                        states[layer] = ExplorerLayerState.Waiting;
                        running--;
                        return;
                    }
                    cancellationToken.ThrowIfCancellationRequested();
                    indexes[layer] = index;
                    states[layer] = ExplorerLayerState.Ready;
                    if (indexes.Count == layerCount)
                    {
                        complete = new Dictionary<int, StoredLayerIndex>(indexes);
                    }
                }
                Indexed?.Invoke(layer, index);
                if (complete is not null)
                {
                    Completed?.Invoke(complete);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                lock (sync)
                {
                    states[layer] = ExplorerLayerState.Waiting;
                    running--;
                }
                return;
            }
            catch (Exception exception)
            {
                lock (sync)
                {
                    if (stopping)
                    {
                        states[layer] = ExplorerLayerState.Waiting;
                        running--;
                        return;
                    }
                    states[layer] = ExplorerLayerState.Failed;
                }
                if (!cancellationToken.IsCancellationRequested)
                {
                    Failed?.Invoke(layer, exception);
                }
            }
        }
    }

    public Task StopAsync()
    {
        lock (sync)
        {
            stopping = true;
            pending.Clear();
            return stoppingTask ??= StopCoreAsync([.. workers]);
        }
    }

    private async Task StopCoreAsync(Task[] activeWorkers)
    {
        try
        {
            await stopSource.CancelAsync();
        }
        finally
        {
            try
            {
                await Task.WhenAll(activeWorkers);
            }
            finally
            {
                linkedSource?.Dispose();
                stopSource.Dispose();
            }
        }
    }
}

internal static partial class InstructionText
{
    public static string Normalize(string? createdBy)
    {
        if (string.IsNullOrWhiteSpace(createdBy))
        {
            return "(no history)";
        }
        string line = createdBy.Trim();
        if (line.EndsWith("# buildkit", StringComparison.Ordinal))
        {
            line = line[..^"# buildkit".Length].TrimEnd();
        }
        int nop = line.IndexOf("#(nop)", StringComparison.Ordinal);
        if (nop >= 0)
        {
            line = line[(nop + "#(nop)".Length)..].Trim();
            if (line.StartsWith("ADD ", StringComparison.OrdinalIgnoreCase) ||
                line.StartsWith("COPY ", StringComparison.OrdinalIgnoreCase))
            {
                int at = line.IndexOf(" in ", StringComparison.Ordinal);
                if (at >= 0)
                {
                    line = line[..at] + line[(at + 3)..];
                }
            }
        }
        else
        {
            Match shell = ShellPrefix().Match(line);
            if (shell.Success)
            {
                line = "RUN " + line[shell.Length..];
            }
        }
        return Whitespace().Replace(line, " ").Trim();
    }

    [GeneratedRegex(@"^(RUN\s+)?(\|\d+\s.*?\s)?/bin/(ba)?sh -c\s+")]
    private static partial Regex ShellPrefix();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();
}
