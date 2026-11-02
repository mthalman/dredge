using System.Text.Json;
using Valleysoft.DockerRegistryClient.Models.Images;
using Valleysoft.DockerRegistryClient.Models.Manifests;
using Valleysoft.DockerRegistryClient.Models.Manifests.Docker;
using Valleysoft.DockerRegistryClient.Models.Manifests.Oci;
using ManifestReference = Valleysoft.DockerRegistryClient.Models.Manifests.Docker.ManifestReference;

namespace Valleysoft.Dredge.Tests;

public sealed class WindowsDescriptorSelectionTests
{
    private const string FirstDigest = "sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string EvidenceDigest = "sha256:bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private const string Version = "10.0.26100.1";
    private static readonly ImageName Name = ImageName.Parse("image");
    private CancellationToken Token => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(null, "v8")]
    [InlineData("", "v8")]
    [InlineData("v8", null)]
    public async Task EqualPlatformTuplesFetchTheDescriptorThatProvedVariantCompatibility(
        string? configVariant, string? indexedVariant)
    {
        Mock<IDockerRegistryClient> client = Client(
            Reference(FirstDigest, indexedVariant), Reference(EvidenceDigest, indexedVariant));
        SetConfig(client, configVariant);
        ExplorerSource direct = await ExplorerSource.OpenAsync(client.Object,
            Mock.Of<IDockerRegistryClientFactory>(), ImageName.Parse("image@" + EvidenceDigest),
            new(), null, Token);

        (ResolvedManifest resolved, IReadOnlyList<ExplorerPlatform> platforms, ExplorerPlatform? platform) =
            await ExplorerSource.ResolveAsync(client.Object, Name, new(), direct.Platform, Token,
                comparing: true, chooseComparisonPlatform: (_, _) =>
                    throw new InvalidOperationException("Equal tuples must remain one choice."));
        ExplorerSource alias = await ExplorerSource.OpenAsync(client.Object,
            Mock.Of<IDockerRegistryClientFactory>(), Name, new(), null, Token,
            exactPlatform: platform, resolvedManifest: resolved);

        Assert.Single(platforms);
        Assert.Equal(EvidenceDigest, resolved.ManifestInfo.DockerContentDigest);
        Assert.Empty(ExplorerSession.Compare(Session(direct), Session(alias)).Files);
        Assert.Empty(ExplorerSession.Compare(Session(alias), Session(direct)).Files);
        client.Verify(o => o.Manifests.GetAsync(Name.Repo, FirstDigest, It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task DuplicateEvidenceDescriptorsDoNotMakeAUniqueChoiceAmbiguous()
    {
        Mock<IDockerRegistryClient> client = Client(Reference(FirstDigest), Reference(EvidenceDigest),
            Reference(EvidenceDigest));

        (ResolvedManifest resolved, _, _) = await Resolve(client, Baseline(),
            (_, _) => throw new InvalidOperationException("Duplicate descriptors are one platform."));

        Assert.Equal(EvidenceDigest, resolved.ManifestInfo.DockerContentDigest);
    }

    [Fact]
    public async Task ExactVersionEvidenceWinsOverOtherCompatibleVersions()
    {
        Mock<IDockerRegistryClient> client = Client(
            Reference(FirstDigest), Reference(EvidenceDigest, version: "10.0.20348.1"),
            Reference(EvidenceDigest));

        (ResolvedManifest resolved, _, ExplorerPlatform? platform) = await Resolve(client, Baseline(),
            (_, _) => throw new InvalidOperationException("The exact version must win."));

        Assert.Equal(Version, platform!.OsVersion);
        Assert.Equal(EvidenceDigest, resolved.ManifestInfo.DockerContentDigest);
    }

    [Fact]
    public async Task AmbiguousChoicesRetainEvidenceEvenWhenChooserReturnsAnEqualPlatform()
    {
        Mock<IDockerRegistryClient> client = Client(
            Reference(FirstDigest, version: "10.0.20348.1"),
            Reference(EvidenceDigest, version: "10.0.20348.1"),
            Reference(FirstDigest, version: "10.0.20348.2"),
            Reference(EvidenceDigest, version: "10.0.20348.2"),
            Reference(EvidenceDigest, version: "10.0.20348.2"));

        (ResolvedManifest resolved, _, ExplorerPlatform? platform) = await Resolve(client, Baseline(),
            (choices, token) =>
            {
                Assert.Equal(Token, token);
                Assert.Equal(2, choices.Count);
                return Task.FromResult<ExplorerPlatform?>(choices[1] with { ManifestDigest = FirstDigest });
            });

        Assert.Equal("10.0.20348.2", platform!.OsVersion);
        Assert.Equal(EvidenceDigest, resolved.ManifestInfo.DockerContentDigest);
    }

    [Fact]
    public async Task AmbiguousChoicesStillRequireAValidSelection()
    {
        Mock<IDockerRegistryClient> client = Client(
            Reference(EvidenceDigest, version: "10.0.20348.1"),
            Reference(EvidenceDigest, version: "10.0.20348.2"));

        await Assert.ThrowsAsync<InvalidOperationException>(() => Resolve(client, Baseline()));
        await Assert.ThrowsAsync<OperationCanceledException>(() => Resolve(client, Baseline(),
            (_, _) => Task.FromResult<ExplorerPlatform?>(null)));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Resolve(client, Baseline(),
            (_, _) => Task.FromResult<ExplorerPlatform?>(new("windows", "amd64", null, Version))));
    }

    [Theory]
    [InlineData("v9", "arm64", "windows", EvidenceDigest)]
    [InlineData("v8", "amd64", "windows", EvidenceDigest)]
    [InlineData("v8", "arm64", "linux", EvidenceDigest)]
    [InlineData("v8", "arm64", "windows", FirstDigest)]
    public async Task EvidenceCannotBypassVariantArchitectureOsOrDigestGuards(
        string variant, string architecture, string os, string digest)
    {
        Mock<IDockerRegistryClient> client = Client(Reference(digest, variant, architecture: architecture, os: os));
        ExplorerPlatform baseline = Baseline(variant: variant == "v9" ? "v8" : null);

        await Assert.ThrowsAsync<InvalidOperationException>(() => Resolve(client, baseline));
    }

    [Theory]
    [InlineData("windows")]
    [InlineData("linux")]
    public async Task ExactTupleAndNormalOptionSelectionKeepFirstDescriptorPreference(string os)
    {
        Mock<IDockerRegistryClient> client = Client(
            Reference(FirstDigest, os: os), Reference(EvidenceDigest, os: os));
        ExplorerPlatform exact = new(os, "arm64", "v8", Version) { ManifestDigest = EvidenceDigest };

        (ResolvedManifest resolved, _, _) = await Resolve(client, exact);
        Assert.Equal(FirstDigest, resolved.ManifestInfo.DockerContentDigest);
        (resolved, IReadOnlyList<ExplorerPlatform> platforms, _) = await ExplorerSource.ResolveAsync(
            client.Object, Name, new(), null, Token,
            settingsStore: new TestAppSettingsStore(new AppSettings()));
        Assert.Single(platforms);
        Assert.Equal(FirstDigest, resolved.ManifestInfo.DockerContentDigest);
    }

    private Task<(ResolvedManifest, IReadOnlyList<ExplorerPlatform>, ExplorerPlatform?)> Resolve(
        Mock<IDockerRegistryClient> client, ExplorerPlatform baseline,
        Func<IReadOnlyList<ExplorerPlatform>, CancellationToken, Task<ExplorerPlatform?>>? chooser = null) =>
        ExplorerSource.ResolveAsync(client.Object, Name, new(), baseline, Token,
            comparing: true, chooseComparisonPlatform: chooser);

    private static ExplorerPlatform Baseline(string? variant = null) =>
        new("windows", "arm64", variant, Version) { ManifestDigest = EvidenceDigest };

    private static ManifestReference Reference(string digest, string? variant = "v8",
        string version = Version, string architecture = "arm64", string os = "windows") =>
        new()
        {
            Digest = digest,
            Platform = new ManifestPlatform
            {
                Os = os, Architecture = architecture, Variant = variant, OsVersion = version
            }
        };

    private static Mock<IDockerRegistryClient> Client(params ManifestReference[] references)
    {
        Mock<IDockerRegistryClient> client = new() { DefaultValue = DefaultValue.Mock };
        ManifestList index = new() { Manifests = references };
        client.Setup(o => o.Manifests.GetAsync(Name.Repo, Name.Tag!, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ManifestInfo("application/index", "sha256:index", index));
        OciImageManifest manifest = new()
        {
            Config = new OciDescriptor { Digest = "sha256:config" },
            Layers = []
        };
        foreach (string digest in new[] { FirstDigest, EvidenceDigest })
        {
            client.Setup(o => o.Manifests.GetAsync(Name.Repo, digest, It.IsAny<CancellationToken>()))
                .ReturnsAsync(new ManifestInfo("application/manifest", digest, manifest));
        }
        return client;
    }

    private static void SetConfig(Mock<IDockerRegistryClient> client, string? variant)
    {
        byte[] config = JsonSerializer.SerializeToUtf8Bytes(new Image
        {
            Os = "windows", Architecture = "arm64", Variant = variant, OsVersion = Version
        }, DredgeJsonContext.Default.Image);
        client.Setup(o => o.Blobs.GetAsync(Name.Repo, "sha256:config", It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new MemoryStream(config));
    }

    private static ExplorerSession Session(ExplorerSource source) => new()
    {
        Image = source.Image,
        Resolved = source.Resolved,
        Config = source.Config,
        Platform = source.Platform,
        Files = null!,
        Analysis = new(0, 0, []),
        Entries = [],
        Packages = new(Enum.GetValues<InstalledPackageEcosystem>().ToDictionary(
            static ecosystem => ecosystem,
            static _ => new InstalledPackageEcosystemMetadata(
                InstalledPackageMetadataAvailability.Unavailable,
                new Dictionary<string, IReadOnlyList<string>>())))
    };
}
