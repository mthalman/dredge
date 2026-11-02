using System.Text.Json;
using Valleysoft.DockerRegistryClient;
using Valleysoft.DockerRegistryClient.Models.Images;
using Valleysoft.DockerRegistryClient.Models.Manifests;
using Valleysoft.DockerRegistryClient.Models.Manifests.Docker;
using Valleysoft.DockerRegistryClient.Models.Manifests.Oci;
using Valleysoft.Dredge.Commands;
using ManifestReference = Valleysoft.DockerRegistryClient.Models.Manifests.Docker.ManifestReference;

namespace Valleysoft.Dredge.Tests;

public sealed class WindowsPlatformReviewTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("v8")]
    public async Task DirectChildDigestResolvesItsIndexedAliasInBothDirections(string? configVariant)
    {
        (ExplorerSource indexed, ExplorerSource direct) = await OpenSources(configVariant, sameDigest: true);
        Mock<IDockerRegistryClient> client = IndexedClient(indexed);
        CancellationToken token = TestContext.Current.CancellationToken;
        (ResolvedManifest resolved, _, ExplorerPlatform? platform) = await ExplorerSource.ResolveAsync(
            client.Object, indexed.Image, new(), direct.Platform, token, comparing: true);
        ExplorerSource alias = await ExplorerSource.OpenAsync(client.Object,
            Mock.Of<IDockerRegistryClientFactory>(), indexed.Image, new(), null, token,
            exactPlatform: platform, resolvedManifest: resolved);

        Assert.Equal("v8", alias.Platform!.Variant);
        Assert.Empty(ExplorerSession.Compare(Session(direct), Session(alias)).Files);
        Assert.Empty(ExplorerSession.Compare(Session(alias), Session(direct)).Files);
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("v9", false)]
    [InlineData("v9", true)]
    public async Task IndexedAliasResolutionRejectsConflictingOrUnprovenVariants(
        string? configVariant, bool sameDigest)
    {
        (ExplorerSource indexed, ExplorerSource direct) = await OpenSources(configVariant, sameDigest);
        await Assert.ThrowsAsync<InvalidOperationException>(() => ExplorerSource.ResolveAsync(
            IndexedClient(indexed).Object, indexed.Image, new(), direct.Platform,
            TestContext.Current.CancellationToken, comparing: true));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task IndexedAliasResolutionAcceptsKnownMatchingVariants(bool sameDigest)
    {
        (ExplorerSource indexed, ExplorerSource direct) = await OpenSources("v8", sameDigest);
        (_, _, ExplorerPlatform? platform) = await ExplorerSource.ResolveAsync(
            IndexedClient(indexed).Object, indexed.Image, new(), direct.Platform,
            TestContext.Current.CancellationToken, comparing: true);
        Assert.Equal("v8", platform!.Variant);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task MissingIndexedVariantRequiresMatchingDigestEvidence(bool sameDigest)
    {
        (ExplorerSource indexed, ExplorerSource direct) = await OpenSources("v8", sameDigest);
        Task<(ResolvedManifest, IReadOnlyList<ExplorerPlatform>, ExplorerPlatform?)> resolve =
            ExplorerSource.ResolveAsync(IndexedClient(indexed, variant: null).Object,
                indexed.Image, new(), direct.Platform, TestContext.Current.CancellationToken, comparing: true);
        if (sameDigest)
        {
            (_, _, ExplorerPlatform? platform) = await resolve;
            Assert.Null(platform!.Variant);
        }
        else
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => resolve);
        }
    }

    [Fact]
    public async Task DigestEvidencePreservesExactVersionPreference()
    {
        (ExplorerSource indexed, ExplorerSource direct) = await OpenSources(null, sameDigest: true);
        Mock<IDockerRegistryClient> client = IndexedClient(indexed, "v8", "10.0.20348.1", "10.0.26100.1");
        (_, _, ExplorerPlatform? platform) = await ExplorerSource.ResolveAsync(client.Object,
            indexed.Image, new(), direct.Platform, TestContext.Current.CancellationToken, comparing: true,
            chooseComparisonPlatform: (_, _) => throw new InvalidOperationException("Exact version must win."));
        Assert.Equal("10.0.26100.1", platform!.OsVersion);
    }

    [Fact]
    public async Task DigestEvidencePreservesUniqueAndAmbiguousVersionSelection()
    {
        (ExplorerSource indexed, ExplorerSource direct) = await OpenSources(null, sameDigest: true);
        CancellationToken token = TestContext.Current.CancellationToken;
        Mock<IDockerRegistryClient> unique = IndexedClient(indexed, "v8", "10.0.20348.1");
        (_, _, ExplorerPlatform? platform) = await ExplorerSource.ResolveAsync(unique.Object,
            indexed.Image, new(), direct.Platform, token, comparing: true);
        Assert.Equal("10.0.20348.1", platform!.OsVersion);
        Mock<IDockerRegistryClient> ambiguous = IndexedClient(indexed, "v8", "10.0.20348.1", "10.0.20348.2");
        await Assert.ThrowsAsync<InvalidOperationException>(() => ExplorerSource.ResolveAsync(ambiguous.Object,
            indexed.Image, new(), direct.Platform, token, comparing: true));
        (_, _, platform) = await ExplorerSource.ResolveAsync(ambiguous.Object, indexed.Image, new(),
            direct.Platform, token, comparing: true, chooseComparisonPlatform: (choices, _) =>
            {
                Assert.Equal(2, choices.Count);
                return Task.FromResult<ExplorerPlatform?>(choices[1]);
            });
        Assert.Equal("10.0.20348.2", platform!.OsVersion);
        await Assert.ThrowsAsync<OperationCanceledException>(() => ExplorerSource.ResolveAsync(ambiguous.Object,
            indexed.Image, new(), direct.Platform, token, comparing: true,
            chooseComparisonPlatform: (_, _) => Task.FromResult<ExplorerPlatform?>(null)));
    }

    [Fact]
    public async Task DigestEvidenceDoesNotChangePlatformIdentity()
    {
        (ExplorerSource indexed, _) = await OpenSources(null, sameDigest: true);
        ExplorerPlatform expected = new("windows", "arm64", "v8", "10.0.26100.1");
        Assert.Equal(expected, indexed.Platform);
        Assert.Equal(expected.GetHashCode(), indexed.Platform!.GetHashCode());
        Assert.Equal("windows/arm64/v8 10.0.26100.1", indexed.Platform.ToString());
    }

    private static Mock<IDockerRegistryClient> IndexedClient(
        ExplorerSource source, string? variant = "v8", params string[] versions)
    {
        Mock<IDockerRegistryClient> client = new() { DefaultValue = DefaultValue.Mock };
        if (versions.Length == 0)
        {
            versions = ["10.0.26100.1"];
        }
        ManifestList index = new()
        {
            Manifests = [.. versions.Select(version => new ManifestReference
                {
                    Digest = source.Resolved.ManifestInfo.DockerContentDigest,
                    Platform = new ManifestPlatform
                    {
                        Os = "windows", Architecture = "arm64", Variant = variant, OsVersion = version
                    }
                })]
        };
        client.Setup(o => o.Manifests.GetAsync(source.Image.Repo, source.Image.Tag!, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ManifestInfo("application/index", "sha256:index", index));
        client.Setup(o => o.Manifests.GetAsync(source.Image.Repo,
            source.Resolved.ManifestInfo.DockerContentDigest, It.IsAny<CancellationToken>()))
            .ReturnsAsync(source.Resolved.ManifestInfo);
        byte[] config = JsonSerializer.SerializeToUtf8Bytes(source.Config, DredgeJsonContext.Default.Image);
        client.Setup(o => o.Blobs.GetAsync(source.Image.Repo, "sha256:config", It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new MemoryStream(config));
        return client;
    }

    [Theory]
    [InlineData("v8")]
    [InlineData(null)]
    public async Task IndexedImageComparesWithItsChildManifestDigest(string? configVariant)
    {
        (ExplorerSource indexed, ExplorerSource direct) = await OpenSources(configVariant, sameDigest: true);

        Assert.Equal("v8", indexed.Platform!.Variant);
        Assert.Equal(configVariant, direct.Platform!.Variant);
        Assert.Equal(indexed.Resolved.ManifestInfo.DockerContentDigest,
            direct.Resolved.ManifestInfo.DockerContentDigest);
        ExplorerComparison comparison = ExplorerSession.Compare(Session(indexed), Session(direct));
        Assert.Empty(comparison.Files);
        Assert.Equal(0, comparison.AdditionalDownloadBytes);
        Assert.Empty(ExplorerSession.Compare(Session(direct), Session(indexed)).Files);
    }

    [Fact]
    public async Task DirectManifestRetainsConfigVariant()
    {
        (_, ExplorerSource direct) = await OpenSources("v9", sameDigest: false);

        Assert.Equal(new ExplorerPlatform("windows", "arm64", "v9", "10.0.26100.1"), direct.Platform);
    }

    [Fact]
    public async Task DifferentManifestsWithExplicitMatchingVariantsCanCompare()
    {
        (ExplorerSource indexed, ExplorerSource direct) = await OpenSources("v8", sameDigest: false);

        Assert.Empty(ExplorerSession.Compare(Session(indexed), Session(direct)).Files);
    }

    [Theory]
    [InlineData("v9", false)]
    [InlineData("v9", true)]
    [InlineData(null, false)]
    [InlineData("", false)]
    public async Task IncompatibleOrUnprovenVariantsRemainRejected(string? configVariant, bool sameDigest)
    {
        (ExplorerSource indexed, ExplorerSource direct) = await OpenSources(configVariant, sameDigest);

        Assert.Throws<InvalidOperationException>(() => ExplorerSession.Compare(Session(indexed), Session(direct)));
        Assert.Throws<InvalidOperationException>(() => ExplorerSession.Compare(Session(direct), Session(indexed)));
    }

    [Fact]
    public void ConfigVariantDeserializesWithSourceGeneratedMetadata()
    {
        Image config = JsonSerializer.Deserialize(
            """{"os":"windows","architecture":"arm64","variant":"v8","os.version":"10.0.26100.1"}""",
            DredgeJsonContext.Default.Image)!;

        Assert.Equal("v8", config.Variant);
        Assert.Equal("10.0.26100.1", config.OsVersion);
    }

    private static async Task<(ExplorerSource Indexed, ExplorerSource Direct)> OpenSources(
        string? configVariant, bool sameDigest)
    {
        const string childDigest = "sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        string directDigest = sameDigest ? childDigest :
            "sha256:bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
        OciImageManifest manifest = new()
        {
            Config = new OciDescriptor { Digest = "sha256:config" },
            Layers = []
        };
        ManifestList index = new()
        {
            Manifests =
            [
                new ManifestReference
                {
                    Digest = childDigest,
                    Platform = new ManifestPlatform
                    {
                        Os = "windows", Architecture = "arm64", Variant = "v8", OsVersion = "10.0.26100.1"
                    }
                }
            ]
        };
        Mock<IDockerRegistryClient> client = new() { DefaultValue = DefaultValue.Mock };
        client.Setup(o => o.Manifests.GetAsync("library/image", "latest", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ManifestInfo("application/index", "sha256:index", index));
        client.Setup(o => o.Manifests.GetAsync("library/image", childDigest, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ManifestInfo("application/manifest", childDigest, manifest));
        client.Setup(o => o.Manifests.GetAsync("library/image", directDigest, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ManifestInfo("application/manifest", directDigest, manifest));
        byte[] config = JsonSerializer.SerializeToUtf8Bytes(new Image
        {
            Os = "windows", Architecture = "arm64", Variant = configVariant, OsVersion = "10.0.26100.1"
        }, DredgeJsonContext.Default.Image);
        client.Setup(o => o.Blobs.GetAsync("library/image", "sha256:config", It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new MemoryStream(config));
        IDockerRegistryClientFactory factory = Mock.Of<IDockerRegistryClientFactory>();
        CancellationToken token = TestContext.Current.CancellationToken;
        ExplorerSource indexed = await ExplorerSource.OpenAsync(client.Object, factory, ImageName.Parse("image"),
            new() { Os = "windows", Architecture = "arm64", OsVersion = "10.0.26100.1" }, null, token,
            settingsStore: new TestAppSettingsStore(new AppSettings()));
        ImageName digestName = ImageName.Parse("image@" + directDigest);
        (ResolvedManifest resolved, _, ExplorerPlatform? platform) = await ExplorerSource.ResolveAsync(
            client.Object, digestName, ExplorerSource.ForPlatform(indexed.Platform!), indexed.Platform, token,
            comparing: true);
        ExplorerSource direct = await ExplorerSource.OpenAsync(client.Object, factory, digestName, new(), null,
            token, exactPlatform: platform, resolvedManifest: resolved);
        return (indexed, direct);
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
