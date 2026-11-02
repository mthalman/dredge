using System.Formats.Tar;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Valleysoft.DockerRegistryClient;
using Valleysoft.DockerRegistryClient.Models.Images;
using Valleysoft.DockerRegistryClient.Models.Manifests;
using Valleysoft.DockerRegistryClient.Models.Manifests.Docker;
using Valleysoft.DockerRegistryClient.Models.Manifests.Oci;
using Valleysoft.Dredge.Commands;
using Valleysoft.Dredge.Explorer;
using static Valleysoft.Dredge.Tests.LayerCacheTestContext;

namespace Valleysoft.Dredge.Tests;

public sealed class ExplorerMixedSourceRegressionTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task IndexingAndSessionsUseTheSourceOwnedContentWithoutACallerClient(bool daemon)
    {
        await using LayerCacheTestContext cache = new();
        await using ResolvedImage image = Source(daemon, Layers(1));
        Mock<IDockerRegistryClient> client = Registry(image, "app");
        Mock<IDockerRegistryClientFactory> registries = Registries(client);
        Factory sources = new((_, _) => image);
        await using ExplorerSource source = await ExplorerSource.OpenAsync(
            sources, registries.Object,
            daemon ? "docker://app:current" : "registry://registry.test/app:current",
            new() { Os = "linux", Architecture = "amd64" }, null, Token);
        ExplorerLayerIndexer indexer = ExplorerLayerIndexer.Create(null, source, cache.Store);
        TaskCompletionSource<IReadOnlyDictionary<int, StoredLayerIndex>> completed =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        indexer.Completed += indexes => completed.TrySetResult(indexes);
        indexer.Failed += (_, error) => completed.TrySetException(error);
        IReadOnlyDictionary<int, StoredLayerIndex> indexes;
        try
        {
            indexer.Start(Token);
            indexes = await completed.Task.WaitAsync(TimeSpan.FromSeconds(30), Token);
        }
        finally
        {
            await indexer.StopAsync();
        }
        Assert.Single(indexes);

        ExplorerSession session = await ExplorerSession.CreateAsync(null, source, cache.Store, indexes, Token);
        await using ImageFileSystem files = session.Files;
        using MemoryStream content = new();
        await files.CopyFileToAsync("file0", content, Token);
        Assert.Equal("content0", Encoding.UTF8.GetString(content.ToArray()));
        Assert.Equal(1, Assert.Single(Assert.IsType<Content>(image.Content).Downloads).Value);
    }

    [Fact]
    public async Task NormalizedRegistryContentStillRequiresTheManifestConfigDigest()
    {
        await using LayerCacheTestContext cache = new();
        await using ResolvedImage image = Source(false, []);
        Assert.IsType<OciDescriptor>(image.Resolved.Manifest.Config).Digest = null!;
        await using ExplorerSource source = new()
        {
            Image = image.Name, Resolved = image.Resolved, Config = image.Configuration, Platforms = []
        };
        Mock<IDockerRegistryClient> client = new(MockBehavior.Strict);
        NotSupportedException error = await Assert.ThrowsAsync<NotSupportedException>(() =>
            ExplorerSession.CreateAsync(client.Object, source, cache.Store, null, Token));
        Assert.Contains("config digest", error.Message);
        Assert.Throws<NotSupportedException>(() => ExplorerLayerIndexer.Create(client.Object, source, cache.Store));
        client.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData(null, "1", false, true)]
    [InlineData(null, "1", true, true)]
    [InlineData("1", "1", true, true)]
    [InlineData("2", "1", true, false)]
    [InlineData(null, null, true, true)]
    public async Task RegistryComparisonRetainsDescriptorOnlyOsVersion(
        string? configVersion, string? descriptorVersion, bool cachedManifest, bool compatible)
    {
        await using LayerCacheTestContext cache = new();
        await using ResolvedImage registry = Source(false, Layers(1), osVersion: configVersion);
        Mock<IDockerRegistryClient> client = Registry(registry, "app", "amd64", osVersion: descriptorVersion);
        Mock<IDockerRegistryClientFactory> registries = Registries(client);
        ImageName image = ImageName.Parse("registry.test/app:current");
        PlatformOptionsBase options = new() { Os = "linux", Architecture = "amd64" };
        await using ExplorerSource baseline = await ExplorerSource.OpenAsync(
            client.Object, registries.Object, image, options, null, Token);
        Assert.Equal(descriptorVersion, baseline.Platform!.OsVersion);

        Task<ExplorerSession> OpenTarget() => ExplorerSession.LoadAsync(
            client.Object, registries.Object, image, options, cache.Store, null, Token,
            exactPlatform: baseline.Platform, resolvedManifest: cachedManifest ? registry.Resolved : null);
        if (!compatible)
        {
            await Assert.ThrowsAsync<InvalidOperationException>(OpenTarget);
            return;
        }

        ExplorerSession before = await ExplorerSession.CreateAsync(client.Object, baseline, cache.Store, null, Token);
        await using ImageFileSystem beforeFiles = before.Files;
        ExplorerSession after = await OpenTarget();
        await using ImageFileSystem afterFiles = after.Files;
        Assert.Equal(descriptorVersion, before.ComparisonPlatform.OsVersion);
        Assert.True(before.ComparisonPlatform.IsEquivalentTo(after.ComparisonPlatform));
        Assert.Contains(descriptorVersion is null ? "--os-version=\"\"" : $"--os-version {descriptorVersion}",
            ExplorerImage.FromSource(baseline).PlatformArguments);
        await before.EnsurePackagesAsync(Token);
        await after.EnsurePackagesAsync(Token);
        Assert.Empty(ExplorerSession.Compare(before, after).Files);
    }

    [Fact]
    public async Task DaemonArmV7BaseResolutionSelectsExactRegistryVariant()
    {
        byte[][] layers = Layers(1);
        await using ResolvedImage daemon = Source(true, layers, architecture: "arm", variant: "v7");
        await using ResolvedImage registry = Source(false, layers, architecture: "arm", variant: "v7");
        Mock<IDockerRegistryClient> client = Registry(registry, "base", "arm", "v7");
        ManifestList index = new()
        {
            Manifests =
            [
                Reference("sha256:v6", "arm", "v6"),
                Reference(registry.Identity, "arm", "v7")
            ]
        };
        client.Setup(value => value.Manifests.GetAsync("base", "current", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ManifestInfo("application/index", "sha256:index", index));
        Mock<IDockerRegistryClientFactory> registries = Registries(client);
        Factory sources = new((reference, _) =>
        {
            Assert.Equal("docker://app:current", reference);
            return daemon;
        });

        await using ExplorerSource source = await ExplorerSource.OpenAsync(
            sources, registries.Object, "docker://app:current", new(),
            ["registry://registry.test/base:current"], Token);

        Assert.Equal("v7", source.Platform!.Variant);
        Assert.Single(source.BaseImages);
        Assert.Single(sources.Calls);
        client.Verify(value => value.Manifests.GetAsync(
            "base", "sha256:v6", It.IsAny<CancellationToken>()), Times.Never);
        Assert.All(sources.Requirements, value => Assert.Equal(ImageSourceRequirements.LinuxFileSystem, value));
    }

    [Theory]
    [InlineData(true, null, true)]
    [InlineData(true, "v7", true)]
    [InlineData(true, "v6", false)]
    [InlineData(false, null, true)]
    [InlineData(false, "v7", true)]
    [InlineData(false, "v6", false)]
    public async Task MixedBaseVerificationPreservesExplicitConfigVariant(
        bool daemonTarget, string? configVariant, bool compatible)
    {
        await using LayerCacheTestContext cache = new();
        byte[][] layers = Layers(1);
        await using ResolvedImage daemon = Source(true, layers, architecture: "arm", variant: "v7");
        await using ResolvedImage registry = Source(false, layers, architecture: "arm", variant: configVariant);
        Mock<IDockerRegistryClient> client = Registry(registry, "base", "arm", "v7");
        Mock<IDockerRegistryClientFactory> registries = Registries(client);
        Factory sources = new((_, _) => daemon);
        Task<ExplorerSource> Open() => ExplorerSource.OpenAsync(
            sources, registries.Object,
            daemonTarget ? "docker://app:current" : "registry://registry.test/base:current",
            new() { Os = "linux", Architecture = "arm" },
            [daemonTarget ? "registry://registry.test/base:current" : "docker://app:current"],
            Token, store: cache.Store);

        if (compatible)
        {
            await using ExplorerSource source = await Open();
            Assert.Single(source.BaseImages);
        }
        else
        {
            InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(Open);
            Assert.Contains("platform", error.Message);
            Assert.Empty(Assert.IsType<Content>(registry.Content).Downloads);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Arm64DefaultVariantComparesAcrossSourcesInBothDirections(bool daemonFirst)
    {
        using Scratch scratch = new();
        await using LayerStore store = new(scratch.Path);
        byte[][] layers = Layers(1);
        await using ResolvedImage daemon = Source(true, layers, architecture: "arm64");
        await using ResolvedImage registry = Source(false, layers, architecture: "arm64", variant: "v8");
        Mock<IDockerRegistryClient> client = Registry(registry, "app", "arm64", "v8");
        Mock<IDockerRegistryClientFactory> registries = Registries(client);
        Factory sources = new((_, _) => daemon);
        string first = daemonFirst ? "docker://app:current" : "registry://registry.test/app:current";
        string second = daemonFirst ? "registry://registry.test/app:current" : "docker://app:current";
        PlatformOptionsBase options = new() { Os = "linux", Architecture = "arm64" };
        await using ExplorerSource baseline = await ExplorerSource.OpenAsync(
            sources, registries.Object, first, options, null, Token);
        await using ExplorerSource target = await ExplorerSource.OpenAsync(
            sources, registries.Object, second, options, null, Token, exactPlatform: baseline.Platform);

        Assert.True(baseline.Platform!.IsEquivalentTo(target.Platform!));
        Assert.Equal(daemonFirst ? "v8" : null, target.Platform!.Variant);
        Assert.Equal(daemonFirst ? null : "v8", baseline.Config.Variant);
        Assert.Equal(daemonFirst ? "v8" : null, target.Config.Variant);

        ExplorerSession before = await ExplorerSession.CreateAsync(
            baseline.RegistryClient, baseline, store, null, Token);
        await using ImageFileSystem beforeFiles = before.Files;
        ExplorerSession after = await ExplorerSession.CreateAsync(
            target.RegistryClient, target, store, null, Token);
        await using ImageFileSystem afterFiles = after.Files;
        await before.EnsureContentIdentitiesAsync(Token);
        await after.EnsureContentIdentitiesAsync(Token);
        await before.EnsurePackagesAsync(Token);
        await after.EnsurePackagesAsync(Token);

        ExplorerComparison comparison = ExplorerSession.Compare(before, after);

        Assert.Empty(comparison.Files);
        Assert.Empty(comparison.Packages);
        Assert.True(new Explorer.CompareState(comparison, first, second).Shared(0));
        Assert.NotEmpty(Assert.IsType<Content>(registry.Content).Downloads);
    }

    [Theory]
    [InlineData(true, null, "v7", true)]
    [InlineData(false, null, "v7", true)]
    [InlineData(true, null, "v6", false)]
    [InlineData(false, null, "v6", false)]
    [InlineData(true, "v6", "v7", false)]
    [InlineData(false, "v6", "v7", false)]
    public async Task FinalComparisonRetainsDescriptorOnlyVariantsWithoutOverridingConfig(
        bool daemonFirst, string? configVariant, string descriptorVariant, bool equivalent)
    {
        using Scratch scratch = new();
        await using LayerStore store = new(scratch.Path);
        byte[][] layers = Layers(1);
        await using ResolvedImage daemon = Source(true, layers, architecture: "arm", variant: "v7");
        await using ResolvedImage registry = Source(false, layers, architecture: "arm", variant: configVariant);
        using JsonDocument document = JsonDocument.Parse(registry.ConfigurationJson);
        Assert.Equal(configVariant is not null, document.RootElement.TryGetProperty("variant", out _));
        Mock<IDockerRegistryClient> client = Registry(registry, "app", "arm", descriptorVariant);
        Mock<IDockerRegistryClientFactory> registries = Registries(client);
        Factory sources = new((_, _) => daemon);
        PlatformOptionsBase options = new() { Os = "linux", Architecture = "arm" };
        await using ExplorerSource local = await ExplorerSource.OpenAsync(
            sources, registries.Object, "docker://app:current", options, null, Token, store: store);
        await using ExplorerSource remote = await ExplorerSource.OpenAsync(
            sources, registries.Object, "registry://registry.test/app:current", options, null, Token, store: store);
        Assert.Equal(descriptorVariant, remote.Platform!.Variant);
        ExplorerSession before = await ExplorerSession.CreateAsync(
            daemonFirst ? local.RegistryClient : remote.RegistryClient,
            daemonFirst ? local : remote, store, null, Token);
        await using ImageFileSystem beforeFiles = before.Files;
        ExplorerSession after = await ExplorerSession.CreateAsync(
            daemonFirst ? remote.RegistryClient : local.RegistryClient,
            daemonFirst ? remote : local, store, null, Token);
        await using ImageFileSystem afterFiles = after.Files;
        await before.EnsureContentIdentitiesAsync(Token);
        await after.EnsureContentIdentitiesAsync(Token);
        await before.EnsurePackagesAsync(Token);
        await after.EnsurePackagesAsync(Token);

        if (equivalent)
        {
            Assert.Empty(ExplorerSession.Compare(before, after).Files);
        }
        else
        {
            Assert.Throws<InvalidOperationException>(() => ExplorerSession.Compare(before, after));
        }
    }

    [Theory]
    [InlineData("arm", "v6", "v7", null, null)]
    [InlineData("arm64", "v8", "v9", null, null)]
    [InlineData("arm64", null, null, "1", "2")]
    public async Task DistinctVariantsAndOsVersionsRemainDifferent(
        string architecture, string? baselineVariant, string? targetVariant,
        string? baselineVersion, string? targetVersion)
    {
        byte[][] layers = Layers(1);
        await using ResolvedImage daemon = Source(true, layers, architecture: architecture,
            variant: targetVariant, osVersion: targetVersion);
        Factory sources = new((_, _) => daemon);
        await Assert.ThrowsAsync<InvalidOperationException>(() => ExplorerSource.OpenAsync(
            sources, Mock.Of<IDockerRegistryClientFactory>(), "docker://app:current", new(), null,
            Token, exactPlatform: new("linux", architecture, baselineVariant, baselineVersion)));
    }

    [Theory]
    [InlineData(true, "arm", "v6", "v7", null, null)]
    [InlineData(false, "arm", "v6", "v7", null, null)]
    [InlineData(true, "arm64", "v8", "v9", null, null)]
    [InlineData(false, "arm64", "v8", "v9", null, null)]
    [InlineData(true, "arm64", null, null, "1", "2")]
    [InlineData(false, "arm64", null, null, "1", "2")]
    public async Task FinalComparisonRejectsDistinctVariantsAndOsVersions(
        bool daemonFirst, string architecture, string? baselineVariant, string? targetVariant,
        string? baselineVersion, string? targetVersion)
    {
        using Scratch scratch = new();
        await using LayerStore store = new(scratch.Path);
        byte[][] layers = Layers(1);
        await using ResolvedImage baseline = Source(daemonFirst, layers, architecture: architecture,
            variant: baselineVariant, osVersion: baselineVersion);
        await using ResolvedImage target = Source(!daemonFirst, layers, architecture: architecture,
            variant: targetVariant, osVersion: targetVersion);
        ExplorerSession before = await ExplorerSession.CreateAsync(null, Explorer(baseline), store, null, Token);
        await using ImageFileSystem beforeFiles = before.Files;
        ExplorerSession after = await ExplorerSession.CreateAsync(null, Explorer(target), store, null, Token);
        await using ImageFileSystem afterFiles = after.Files;
        await before.EnsureContentIdentitiesAsync(Token);
        await after.EnsureContentIdentitiesAsync(Token);
        await before.EnsurePackagesAsync(Token);
        await after.EnsurePackagesAsync(Token);

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() =>
            ExplorerSession.Compare(before, after));

        Assert.Contains("same platform", error.Message);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task SessionLayerIdentitiesAndVerificationUseTheSamePrivateSnapshot(bool initiallyCorrect)
    {
        using Scratch scratch = new();
        await using LayerStore store = new(scratch.Path);
        byte[][] layers = Layers(3);
        await using ResolvedImage image = Source(false, layers);
        string[] ids = image.Configuration.RootFilesystem!.DiffIds!;
        string correct = ids[0];
        if (!initiallyCorrect)
        {
            ids[0] = "sha256:incorrect";
        }
        string expected = ids[0];
        ExplorerSession session = await ExplorerSession.CreateAsync(null, Explorer(image), store, null, Token);
        await using ImageFileSystem files = session.Files;
        ids[0] = initiallyCorrect ? "sha256:incorrect" : correct;
        ids[1] = null!;

        Assert.Equal(expected, session.LayerIdentity(0, true));
        for (int repeat = 0; repeat < 100; repeat++)
        {
            Assert.Equal(expected, session.LayerIdentity(0, true));
            Assert.Equal(Digest(layers[1]), session.LayerIdentity(1, true));
            Assert.Equal(Digest(layers[2]), session.LayerIdentity(2, true));
        }
        Assert.Null(session.LayerIdentity(layers.Length, true));
        Assert.Equal(image.Resolved.Manifest.Layers[0].Digest, session.LayerIdentity(0, false));
        if (initiallyCorrect)
        {
            await session.EnsureContentIdentitiesAsync(Token);
            await session.EnsureContentIdentitiesAsync(Token);
        }
        else
        {
            InvalidDataException error = await Assert.ThrowsAsync<InvalidDataException>(() =>
                session.EnsureContentIdentitiesAsync(Token));
            Assert.Contains(expected, error.Message);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ExplorerSourceDisposesOnlyFactoryOwnedRegistryClientsOnce(bool owned)
    {
        await using ResolvedImage image = Source(false, Layers(1));
        Mock<IDockerRegistryClient> client = Registry(image, "app");
        Mock<IDockerRegistryClientFactory> registries = Registries(client);
        Factory sources = new((_, _) => throw new InvalidOperationException("Registry snapshots are resolved directly."));
        ExplorerSource source = owned
            ? await ExplorerSource.OpenAsync(sources, registries.Object,
                "registry://registry.test/app:current", new(), null, Token)
            : await ExplorerSource.OpenAsync(client.Object, registries.Object,
                ImageName.Parse("registry.test/app:current"), new(), null, Token,
                resolvedManifest: image.Resolved);

        await source.DisposeAsync();
        await source.DisposeAsync();

        client.Verify(value => value.Dispose(), owned ? Times.Once() : Times.Never());
        Assert.Empty(sources.Calls);
    }

    [Fact]
    public async Task ExplorerSourceReleasesFactoryOwnedClientWhenConfigLoadingFails()
    {
        await using ResolvedImage image = Source(false, Layers(1));
        Mock<IDockerRegistryClient> client = Registry(image, "app");
        client.Setup(value => value.Blobs.GetAsync("app", image.Resolved.Manifest.Config!.Digest!,
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new IOException("Config unavailable."));
        Mock<IDockerRegistryClientFactory> registries = Registries(client);
        Factory sources = new((_, _) => throw new InvalidOperationException("Registry snapshots are resolved directly."));

        await Assert.ThrowsAsync<IOException>(() => ExplorerSource.OpenAsync(
            sources, registries.Object, "registry://registry.test/app:current", new(), null, Token));

        client.Verify(value => value.Dispose(), Times.Once);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task BasePlatformComparisonNormalizesOnlyArm64DefaultVariant(bool daemonTarget)
    {
        using Scratch scratch = new();
        await using LayerStore store = new(scratch.Path);
        byte[][] layers = Layers(1);
        await using ResolvedImage target = Source(daemonTarget, layers, architecture: "arm64",
            variant: daemonTarget ? null : "v8");
        await using ResolvedImage candidate = Source(!daemonTarget, layers, architecture: "arm64",
            variant: daemonTarget ? "v8" : null);
        Factory sources = new((_, _) => candidate);

        var (bases, warning) = await ExplorerSource.VerifySourceBasesAsync(
            sources, target.Configuration, target.Resolved, daemonTarget, new(),
            [daemonTarget ? "registry://registry.test/base:current" : "docker://base:current"],
            Token, target.Content, store);

        Assert.Single(bases);
        Assert.Null(warning);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task MixedBasesKeepRegistryAnnotationChecks(bool conflictingName, bool conflictingDigest)
    {
        using Scratch scratch = new();
        await using LayerStore store = new(scratch.Path);
        byte[][] layers = Layers(3);
        await using ResolvedImage target = Source(false, layers);
        await using ResolvedImage daemonBase = Source(true, layers[..1]);
        await using ResolvedImage registryBase = Source(false, layers[..2]);
        await using ResolvedImage annotatedBase = Source(false, layers[..2], identity: "sha256:other-manifest");
        OciImageManifest manifest = Assert.IsType<OciImageManifest>(target.Resolved.Manifest);
        manifest.Annotations = new Dictionary<string, string>
        {
            ["org.opencontainers.image.base.name"] = conflictingName
                ? "registry.test/annotated:current" : "registry.test/base:current",
            ["org.opencontainers.image.base.digest"] = conflictingDigest ? "sha256:wrong" : registryBase.Identity
        };
        Mock<IDockerRegistryClient> client = Registry(registryBase, "base");
        AddRegistryImage(client, annotatedBase, "annotated");
        Mock<IDockerRegistryClientFactory> registries = Registries(client);
        Factory sources = new((reference, _) =>
        {
            Assert.Equal("docker://ancestor:current", reference);
            return daemonBase;
        });

        Task<(IReadOnlyList<ExplorerBaseImage>, string?)> work = ExplorerSource.VerifySourceBasesAsync(
            sources, target.Configuration, target.Resolved, false, new(),
            ["docker://ancestor:current", "registry://registry.test/base:current"],
            Token, target.Content, store, registries.Object);
        if (conflictingName || conflictingDigest)
        {
            InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(() => work);
            Assert.Contains(conflictingDigest ? "annotation digest" : "disagrees with the annotation", error.Message);
        }
        else
        {
            var (bases, warning) = await work;
            Assert.Equal(2, bases.Count);
            Assert.Null(warning);
        }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task MovingBaseTag_ValidatesAnnotationAgainstTheDeepestAcquiredSnapshot(
        bool staleDigest, bool annotationAlias)
    {
        using Scratch scratch = new();
        await using LayerStore store = new(scratch.Path);
        byte[][] layers = Layers(4);
        await using ResolvedImage target = Source(false, layers);
        await using ResolvedImage ancestor = Source(true, layers[..1]);
        await using ResolvedImage deepest = Source(false, layers[..3], identity: "sha256:deepest");
        await using ResolvedImage later = Source(false, layers[..2], identity: "sha256:later");
        Assert.IsType<OciImageManifest>(target.Resolved.Manifest).Annotations = new Dictionary<string, string>
        {
            ["org.opencontainers.image.base.name"] = annotationAlias
                ? "registry.test/annotated:current" : "registry.test/moving:current",
            ["org.opencontainers.image.base.digest"] = staleDigest ? later.Identity : deepest.Identity
        };
        Mock<IDockerRegistryClient> client = Registry(target, "app");
        AddRegistryImage(client, deepest, "moving");
        AddRegistryImage(client, later, "moving");
        AddRegistryImage(client, deepest, "annotated");
        client.SetupSequence(value => value.Manifests.GetAsync(
                "moving", "current", It.IsAny<CancellationToken>()))
            .ReturnsAsync(deepest.Resolved.ManifestInfo)
            .ReturnsAsync(later.Resolved.ManifestInfo);
        Mock<IDockerRegistryClientFactory> registries = Registries(client);
        Factory sources = new((reference, _) =>
        {
            Assert.Equal("docker://ancestor:current", reference);
            return ancestor;
        });
        Task<ExplorerSource> Open() => ExplorerSource.OpenAsync(
            sources, registries.Object, "registry://registry.test/app:current", new(),
            ["docker://ancestor:current", "registry://registry.test/moving:current",
                "registry://registry.test/moving:current"],
            Token, store: store);

        if (staleDigest)
        {
            InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(Open);
            Assert.Contains("annotation digest", error.Message);
        }
        else
        {
            await using ExplorerSource result = await Open();
            Assert.Equal([1, 2, 3], result.BaseImages.Select(value => value.LayerCount));
            Assert.Equal("registry://registry.test/moving:current", result.BaseImages[^1].Name);
            Assert.Null(result.BaseWarning);
        }
        client.Verify(value => value.Manifests.GetAsync(
            "moving", "current", It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [Fact]
    public async Task DaemonDeepestBaseWarnsWithoutInventingRegistryManifestIdentity()
    {
        using Scratch scratch = new();
        await using LayerStore store = new(scratch.Path);
        byte[][] layers = Layers(2);
        await using ResolvedImage target = Source(false, layers);
        await using ResolvedImage candidate = Source(true, layers[..1]);
        Assert.IsType<OciImageManifest>(target.Resolved.Manifest).Annotations = new Dictionary<string, string>
        {
            ["org.opencontainers.image.base.name"] = "registry.test/base:current",
            ["org.opencontainers.image.base.digest"] = "sha256:unavailable"
        };
        Factory sources = new((_, _) => candidate);
        Mock<IDockerRegistryClientFactory> registries = new(MockBehavior.Strict);

        var (bases, warning) = await ExplorerSource.VerifySourceBasesAsync(
            sources, target.Configuration, target.Resolved, false, new(), ["docker://base:current"],
            Token, target.Content, store, registries.Object);

        Assert.Single(bases);
        Assert.Contains("cannot be verified", warning);
        registries.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RepeatedBasePrefixesHashEachStoredDigestDiffIdPairOncePerOperation(bool targetPrefixes)
    {
        using Scratch scratch = new();
        await using LayerStore store = new(scratch.Path);
        byte[][] layers = Layers(3);
        Content content = new();
        List<ResolvedImage> candidates = [];
        for (int count = 1; count <= layers.Length; count++)
        {
            candidates.Add(Source(false, layers[..count], content: content));
        }
        foreach (ResolvedImage candidate in candidates)
        {
            ResolvedImage inspected = targetPrefixes ? candidates[^1] : candidate;
            await ExplorerContentIdentity.VerifyAsync(inspected.Content, inspected.Configuration,
                inspected.Resolved, store, Token, candidate.Resolved.Manifest.Layers.Length);
        }

        Assert.Equal(3, content.Downloads.Values.Sum());
        Assert.All(content.Downloads.Values, count => Assert.Equal(1, count));
        await ExplorerContentIdentity.VerifyAsync(content, candidates[^1].Configuration,
            candidates[^1].Resolved, store, Token);
        Assert.Equal(3, content.Downloads.Values.Sum());
    }

    [Fact]
    public async Task RepeatedPrefixesKeepIndependentBaseBoundaries()
    {
        using Scratch scratch = new();
        await using LayerStore store = new(scratch.Path);
        byte[][] layers = Layers(3);
        await using ResolvedImage target = Source(true, layers);
        Content content = new();
        int next = 1;
        Factory sources = new((_, _) => Source(false, layers[..next++], content: content));

        var (bases, warning) = await ExplorerSource.VerifySourceBasesAsync(
            sources, target.Configuration, target.Resolved, true, new(),
            ["registry.test/base:one", "registry.test/base:two", "registry.test/base:three"],
            Token, store: store);

        Assert.Equal([1, 2, 3], bases.Select(value => value.LayerCount));
        Assert.Null(warning);
        Assert.Equal(3, content.Downloads.Values.Sum());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MixedBasePrefixUsesVerifiedCommonIdentityAndPublishesIndex(bool daemonTarget)
    {
        using Scratch scratch = new();
        await using LayerStore store = new(scratch.Path);
        byte[][] layers = Layers(2);
        await using ResolvedImage target = Source(daemonTarget, layers);
        await using ResolvedImage candidate = Source(!daemonTarget, layers[..1]);
        ResolvedImage registry = daemonTarget ? candidate : target;
        registry.Configuration.RootFilesystem!.DiffIds = (daemonTarget ? layers[..1] : layers)
            .Select(layer => "sha512:" + Convert.ToHexStringLower(
                System.Security.Cryptography.SHA512.HashData(layer))).ToArray();
        Factory sources = new((_, _) => candidate);

        var (bases, _) = await ExplorerSource.VerifySourceBasesAsync(
            sources, target.Configuration, target.Resolved, daemonTarget, new(),
            [daemonTarget ? "registry.test/base:current" : "docker://base:current"],
            Token, target.Content, store);

        Assert.Equal(1, Assert.Single(bases).LayerCount);
        Assert.NotNull(await store.ReadMetadataAsync<StoredLayerIndex>(
            registry.Resolved.Manifest.Layers[0].Digest!, "index", Token));
        if (!daemonTarget)
        {
            Assert.Null(await store.ReadMetadataAsync<StoredLayerIndex>(
                registry.Resolved.Manifest.Layers[1].Digest!, "index", Token));
        }
    }

    [Fact]
    public async Task SharedPrefixStillValidatesEachBaseDescriptorSize()
    {
        using Scratch scratch = new();
        await using LayerStore store = new(scratch.Path);
        byte[][] layers = Layers(3);
        await using ResolvedImage target = Source(true, layers);
        await using ResolvedImage first = Source(false, layers[..1]);
        await using ResolvedImage second = Source(false, layers[..2]);
        Assert.Equal(first.Resolved.Manifest.Layers[0].Digest, second.Resolved.Manifest.Layers[0].Digest);
        Assert.IsType<OciDescriptor>(second.Resolved.Manifest.Layers[0]).Size++;
        Factory sources = new((reference, _) => reference.EndsWith(":one", StringComparison.Ordinal) ? first : second);

        await Assert.ThrowsAsync<InvalidDataException>(() => ExplorerSource.VerifySourceBasesAsync(
            sources, target.Configuration, target.Resolved, true, new(),
            ["registry.test/base:one", "registry.test/base:two"], Token, store: store));

        Assert.Equal(2, sources.Calls.Count);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task SourceOpeningSharesTheCommandStoreForMixedBaseVerification(bool daemonTarget)
    {
        using Scratch scratch = new();
        await using LayerStore store = new(scratch.Path);
        byte[][] layers = Layers(2);
        await using ResolvedImage daemon = Source(true, layers);
        await using ResolvedImage registry = Source(false, layers);
        Mock<IDockerRegistryClient> client = Registry(registry, "app");
        Mock<IDockerRegistryClientFactory> registries = Registries(client);
        Factory sources = new((_, _) => daemon);
        await using ExplorerSource source = await ExplorerSource.OpenAsync(
            sources, registries.Object,
            daemonTarget ? "docker://app:current" : "registry://registry.test/app:current",
            new() { Os = "linux", Architecture = "amd64" },
            [daemonTarget ? "registry://registry.test/app:current" : "docker://app:current"],
            Token, store: store);

        Assert.Single(source.BaseImages);
        Assert.All(registry.Resolved.Manifest.Layers, descriptor =>
            Assert.True(File.Exists(store.GetBlobPath(descriptor.Digest!))));
        await ExplorerContentIdentity.VerifyAsync(
            registry.Content, registry.Configuration, registry.Resolved, store, Token);
        Assert.Equal(layers.Length, Assert.IsType<Content>(registry.Content).Downloads.Values.Sum());
        await store.GetIndexAsync(registry.Content, new(0, registry.Resolved.Manifest.Layers[0].Digest!),
            registry.Resolved.Manifest.Layers[0].Size, Token);
    }

    [Fact]
    public async Task VerificationCacheDoesNotCacheFailuresOrTrustADifferentDiffId()
    {
        using Scratch scratch = new();
        await using LayerStore store = new(scratch.Path);
        byte[][] layers = Layers(1);
        await using ResolvedImage image = Source(false, layers);
        string[] diffIds = image.Configuration.RootFilesystem!.DiffIds!;
        string correct = diffIds[0];
        diffIds[0] = "sha256:incorrect";
        await Assert.ThrowsAsync<InvalidDataException>(() => ExplorerContentIdentity.VerifyAsync(
            image.Content, image.Configuration, image.Resolved, store, Token));

        diffIds[0] = correct;
        await ExplorerContentIdentity.VerifyAsync(
            image.Content, image.Configuration, image.Resolved, store, Token);
        diffIds[0] = "sha256:other";
        await Assert.ThrowsAsync<InvalidDataException>(() => ExplorerContentIdentity.VerifyAsync(
            image.Content, image.Configuration, image.Resolved, store, Token));
    }

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static ExplorerSource Explorer(ResolvedImage source) => new()
    {
        Source = source, Image = source.Name, Resolved = source.Resolved,
        Config = source.Configuration, Platforms = []
    };

    private static Mock<IDockerRegistryClientFactory> Registries(Mock<IDockerRegistryClient> client)
    {
        Mock<IDockerRegistryClientFactory> factory = new(MockBehavior.Strict);
        factory.Setup(value => value.GetClientAsync("registry.test", It.IsAny<CancellationToken>()))
            .ReturnsAsync(client.Object);
        return factory;
    }

    private static Mock<IDockerRegistryClient> Registry(
        ResolvedImage source, string repo, string? architecture = null, string? variant = null, string? osVersion = null)
    {
        Mock<IDockerRegistryClient> client = new() { DefaultValue = DefaultValue.Mock };
        AddRegistryImage(client, source, repo);
        if (architecture is not null)
        {
            ManifestList index = new() { Manifests = [Reference(source.Identity, architecture, variant, osVersion)] };
            client.Setup(value => value.Manifests.GetAsync(repo, "current", It.IsAny<CancellationToken>()))
                .ReturnsAsync(new ManifestInfo("application/index", "sha256:index", index));
        }
        return client;
    }

    private static void AddRegistryImage(Mock<IDockerRegistryClient> client, ResolvedImage source, string repo)
    {
        client.Setup(value => value.Manifests.GetAsync(repo, "current", It.IsAny<CancellationToken>()))
            .ReturnsAsync(source.Resolved.ManifestInfo);
        client.Setup(value => value.Manifests.GetAsync(repo, source.Identity, It.IsAny<CancellationToken>()))
            .ReturnsAsync(source.Resolved.ManifestInfo);
        client.Setup(value => value.Blobs.GetAsync(
            repo, source.Resolved.Manifest.Config!.Digest!, It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new MemoryStream(source.ConfigurationJson));
        client.Setup(value => value.Blobs.GetRangeAsync(repo, It.IsAny<string>(), It.IsAny<long>(),
                It.IsAny<long?>(), It.IsAny<CancellationToken>()))
            .Returns((string _, string digest, long start, long? length, CancellationToken token) =>
                source.Content.GetRangeAsync(digest, start, length, token));
    }

    private static Valleysoft.DockerRegistryClient.Models.Manifests.Docker.ManifestReference Reference(
        string digest, string architecture, string? variant, string? osVersion = null) => new()
        {
            Digest = digest,
            Platform = new ManifestPlatform { Os = "linux", Architecture = architecture, Variant = variant!, OsVersion = osVersion! }
        };

    private static ResolvedImage Source(bool daemon, byte[][] layers, string architecture = "amd64",
        string? variant = null, string? osVersion = null, string? identity = null, Content? content = null)
    {
        content ??= new();
        OciDescriptor[] descriptors = layers.Select(layer =>
        {
            byte[] blob = layer;
            if (!daemon)
            {
                using MemoryStream compressed = new();
                using (GZipStream gzip = new(compressed, CompressionLevel.SmallestSize, leaveOpen: true))
                {
                    gzip.Write(layer);
                }
                blob = compressed.ToArray();
            }
            string digest = Digest(blob);
            content.Blobs[digest] = blob;
            return new OciDescriptor { Digest = digest, Size = blob.Length };
        }).ToArray();
        Dictionary<string, object?> fields = new()
        {
            ["os"] = "linux", ["architecture"] = architecture,
            ["os.version"] = osVersion,
            ["rootfs"] = new { type = "layers", diff_ids = layers.Select(Digest).ToArray() }
        };
        if (variant is not null)
        {
            fields["variant"] = variant;
        }
        string json = JsonSerializer.Serialize(fields);
        byte[] configBytes = Encoding.UTF8.GetBytes(json);
        Image config = JsonHelper.Deserialize<Image>(json)!;
        OciImageManifest manifest = new()
        {
            Config = new OciDescriptor { Digest = Digest(configBytes) }, Layers = descriptors
        };
        identity ??= Digest(Encoding.UTF8.GetBytes(json + (daemon ? "daemon" : "registry")));
        ManifestInfo info = new("application/vnd.oci.image.manifest.v1+json", identity, manifest);
        ImageSourceReference reference = ImageSourceReference.Parse(daemon
            ? "docker://app:current" : "registry://registry.test/app:current");
        return new(reference, ImageName.Parse("registry.test/app:current"), new(info, manifest),
            daemon ? null : info, config, configBytes, content, identity, "linux/" + architecture);
    }

    private static byte[][] Layers(int count) => Enumerable.Range(0, count).Select(index =>
    {
        using MemoryStream stream = new();
        using (TarWriter writer = new(stream, TarEntryFormat.Pax, leaveOpen: true))
        {
            writer.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, $"file{index}")
            {
                DataStream = new MemoryStream(Encoding.UTF8.GetBytes($"content{index}"))
            });
        }
        return stream.ToArray();
    }).ToArray();

    private sealed class Content : IImageContentSource
    {
        public Dictionary<string, byte[]> Blobs { get; } = [];
        public Dictionary<string, int> Downloads { get; } = [];

        public Task<BlobDownloadResult> GetRangeAsync(string digest, long start, long? length, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            byte[] bytes = Blobs[digest];
            lock (Downloads)
            {
                Downloads[digest] = Downloads.GetValueOrDefault(digest) + 1;
            }
            return Task.FromResult(new BlobDownloadResult(new MemoryStream(bytes), false, null, null, bytes.Length));
        }
    }

    private sealed class Factory(Func<string, PlatformOptionsBase, ResolvedImage> open) : IImageSourceFactory
    {
        public List<string> Calls { get; } = [];
        public List<ImageSourceRequirements> Requirements { get; } = [];

        public Task<ResolvedImage> OpenAsync(string reference, PlatformOptionsBase options,
            CancellationToken token, ImageSourceRequirements requirements = ImageSourceRequirements.LayerContent)
        {
            token.ThrowIfCancellationRequested();
            Calls.Add(reference);
            Requirements.Add(requirements);
            return Task.FromResult(open(reference, options));
        }
    }

    private sealed class Scratch : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            Directory.GetCurrentDirectory(), "explorer-mixed-source-" + Guid.NewGuid().ToString("N"));

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, true);
            }
        }
    }
}
