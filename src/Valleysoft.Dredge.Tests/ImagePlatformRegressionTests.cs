using System.CommandLine;
using System.Text;
using System.Text.Json;
using Valleysoft.DockerRegistryClient.Models.Images;
using Valleysoft.DockerRegistryClient.Models.Manifests;
using Valleysoft.DockerRegistryClient.Models.Manifests.Docker;
using Valleysoft.DockerRegistryClient.Models.Manifests.Oci;
using Valleysoft.Dredge.Commands;
using Valleysoft.Dredge.Commands.Image;
using static Valleysoft.Dredge.Tests.LayerCacheTestContext;

namespace Valleysoft.Dredge.Tests;

public sealed class ImagePlatformRegressionTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void EmptyOsVersionDoesNotConsumePositionals(bool optionFirst)
    {
        InspectOptions options = new();
        Command command = new("inspect");
        options.SetCommandOptions(command);
        var parsed = command.Parse(optionFirst
            ? ["--os-version=", "docker://app"]
            : ["docker://app", "--os-version="]);
        Assert.Empty(parsed.Errors);
        options.SetParseResult(parsed);
        Assert.Equal("docker://app", options.Image);
        Assert.Equal("", options.OsVersion);
    }

    [Theory]
    [InlineData("--os-version=", "")]
    [InlineData("--os-version=1", "1")]
    [InlineData("--os-version=2", "2")]
    public void LastOsVersionOccurrenceWins(string last, string expected)
    {
        InspectOptions options = new();
        Command command = new("inspect");
        options.SetCommandOptions(command);
        var parsed = command.Parse(["--os-version=1", "--os-version=", "docker://app", last]);
        Assert.Empty(parsed.Errors);
        options.SetParseResult(parsed);
        Assert.Equal(expected, options.OsVersion);
        Assert.Equal("docker://app", options.Image);
    }

    [Fact]
    public void MissingOsVersionValueIsStillAnError()
    {
        InspectOptions options = new();
        Command command = new("inspect");
        options.SetCommandOptions(command);
        Assert.NotEmpty(command.Parse(["docker://app", "--os-version"]).Errors);
    }

    [Fact]
    public void SeparateEmptyValueStillSuppressesTheDefault()
    {
        InspectOptions options = new();
        Command command = new("inspect");
        options.SetCommandOptions(command);
        var parsed = command.Parse(["--os-version", "", "docker://app"]);
        Assert.Empty(parsed.Errors);
        options.SetParseResult(parsed);
        Assert.Equal("", options.OsVersion);
        Assert.Equal("docker://app", options.Image);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("v7")]
    public async Task DigestFirstArmBaselineCanResolveItsOwnIndexedManifest(string? configVariant)
    {
        byte[] config = JsonSerializer.SerializeToUtf8Bytes(new
        {
            os = "linux", architecture = "arm", variant = configVariant,
            rootfs = new { type = "layers", diff_ids = Array.Empty<string>() }
        });
        DockerManifest manifest = new() { Config = new() { Digest = Digest(config) }, Layers = [] };
        string digest = Digest(Encoding.UTF8.GetBytes("manifest"));
        ManifestInfo info = new("application/manifest", digest, manifest);
        Mock<IDockerRegistryClient> client = new() { DefaultValue = DefaultValue.Mock };
        client.Setup(value => value.Manifests.GetAsync("app", digest, It.IsAny<CancellationToken>()))
            .ReturnsAsync(info);
        client.Setup(value => value.Blobs.GetAsync("app", Digest(config), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new MemoryStream(config));
        ManifestList index = new() { Manifests = [Reference(digest, "v7")] };
        client.Setup(value => value.Manifests.GetAsync("app", "latest", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ManifestInfo("application/index", "sha256:index", index));
        PlatformOptionsBase options = new() { Os = "linux", Architecture = "arm", UsePlatformDefaults = false };
        await using ExplorerSource baseline = await ExplorerSource.OpenAsync(
            client.Object, Mock.Of<IDockerRegistryClientFactory>(), ImageName.Parse($"registry.test/app@{digest}"),
            options, null, Token);
        await using ExplorerSource target = await ExplorerSource.OpenAsync(
            client.Object, Mock.Of<IDockerRegistryClientFactory>(), ImageName.Parse("registry.test/app:latest"),
            options, null, Token, exactPlatform: baseline.Platform);
        Assert.Same(manifest, target.Resolved.Manifest);
        Assert.Equal("v7", target.Platform!.Variant);
    }

    [Fact]
    public async Task UnknownVariantCanResolveAnIndexedBase()
    {
        byte[] config = JsonSerializer.SerializeToUtf8Bytes(new
        {
            os = "linux", architecture = "arm",
            rootfs = new { type = "layers", diff_ids = Array.Empty<string>() }
        });
        DockerManifest manifest = new() { Config = new() { Digest = Digest(config) }, Layers = [] };
        string manifestDigest = Digest(Encoding.UTF8.GetBytes("manifest"));
        ManifestInfo info = new("application/manifest", manifestDigest, manifest);
        Mock<IDockerRegistryClient> client = new() { DefaultValue = DefaultValue.Mock };
        client.Setup(value => value.Manifests.GetAsync("app", manifestDigest, It.IsAny<CancellationToken>()))
            .ReturnsAsync(info);
        client.Setup(value => value.Manifests.GetAsync("app", "latest", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ManifestInfo("application/index", "sha256:index",
                new ManifestList { Manifests = [Reference(manifestDigest, "v7")] }));
        client.Setup(value => value.Blobs.GetAsync("app", Digest(config), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new MemoryStream(config));
        await using ExplorerSource baseline = await ExplorerSource.OpenAsync(
            client.Object, Mock.Of<IDockerRegistryClientFactory>(),
            ImageName.Parse("registry.test/app@" + manifestDigest),
            new() { Os = "linux", Architecture = "arm", UsePlatformDefaults = false },
            ["registry.test/app:latest"], Token);
        Assert.Single(baseline.BaseImages);
    }

    [Fact]
    public async Task UnknownVariantDoesNotChooseBetweenMultipleKnownVariants()
    {
        Mock<IDockerRegistryClient> client = new() { DefaultValue = DefaultValue.Mock };
        ManifestList index = new() { Manifests = [Reference("sha256:v6", "v6"), Reference("sha256:v7", "v7")] };
        client.Setup(value => value.Manifests.GetAsync("app", "latest", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ManifestInfo("application/index", "sha256:index", index));
        InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ExplorerSource.ResolveAsync(client.Object, ImageName.Parse("registry.test/app:latest"),
                new(), new("linux", "arm", null, null), Token));
        Assert.Contains("multiple", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("linux/arm/v6", error.Message);
        Assert.Contains("linux/arm/v7", error.Message);
    }

    [Theory]
    [InlineData("arm", null, "v7", true)]
    [InlineData("arm", "v7", null, true)]
    [InlineData("arm", "v6", "v7", false)]
    [InlineData("arm64", null, "v8", true)]
    [InlineData("arm64", null, "v9", false)]
    public void VariantCompatibilityPreservesKnownConstraints(
        string architecture, string? baseline, string? target, bool compatible)
    {
        ExplorerPlatform first = new("linux", architecture, baseline, null);
        Assert.Equal(compatible, first.IsCompatibleWith(new("linux", architecture, target, null)));
        Assert.False(first.IsCompatibleWith(new("linux", architecture, target, "different-version")));
    }

    [Fact]
    public async Task EquivalentRegistryBaseNameDoesNotResolveMutableTagAgain()
    {
        byte[][] layers = Enumerable.Range(0, 3).Select(index =>
            DockerArchiveFixture.BuildTar(($"file{index}", Encoding.UTF8.GetBytes($"payload{index}")))).ToArray();
        await using ResolvedImage target = Source(layers, false);
        await using ResolvedImage registryBase = Source(layers[..2], false);
        await using ResolvedImage daemonBase = Source(layers[..1], true);
        ((OciImageManifest)target.Resolved.Manifest).Annotations = new Dictionary<string, string>
        {
            ["org.opencontainers.image.base.name"] = "registry.test/base:latest",
            ["org.opencontainers.image.base.digest"] = registryBase.Identity
        };
        List<string> calls = [];
        Factory sources = new(reference =>
            {
                calls.Add(reference);
                return reference switch
                {
                    "docker://ancestor" => daemonBase,
                    "registry://registry.test/base" => registryBase,
                    _ => throw new InvalidOperationException("Equivalent mutable tag was resolved again.")
                };
            });
        await using LayerCacheTestContext cache = new();
        var result = await ExplorerSource.VerifySourceBasesAsync(
            sources, target.Configuration, target.Resolved, false,
            new() { Os = "linux", Architecture = "amd64" },
            ["docker://ancestor", "registry://registry.test/base"], Token, target.Content, cache.Store);
        Assert.Equal(2, result.Item1.Count);
        Assert.Equal(["docker://ancestor", "registry://registry.test/base"], calls);
    }

    private static Valleysoft.DockerRegistryClient.Models.Manifests.Docker.ManifestReference Reference(
        string digest, string variant) => new()
        {
            Digest = digest,
            Platform = new ManifestPlatform { Os = "linux", Architecture = "arm", Variant = variant }
        };

    private static ResolvedImage Source(byte[][] layers, bool daemon)
    {
        Content content = new();
        OciDescriptor[] descriptors = layers.Select(layer =>
        {
            string digest = Digest(layer);
            content.Blobs[digest] = layer;
            return new OciDescriptor { Digest = digest, Size = layer.Length };
        }).ToArray();
        byte[] configBytes = JsonSerializer.SerializeToUtf8Bytes(new
        {
            os = "linux", architecture = "amd64",
            rootfs = new { type = "layers", diff_ids = layers.Select(Digest).ToArray() }
        });
        Image config = JsonHelper.Deserialize<Image>(Encoding.UTF8.GetString(configBytes))!;
        OciImageManifest manifest = new()
        {
            Config = new() { Digest = Digest(configBytes), Size = configBytes.Length }, Layers = descriptors
        };
        ManifestInfo info = new("application/vnd.oci.image.manifest.v1+json", Digest(configBytes), manifest);
        ImageSourceReference reference = ImageSourceReference.Parse(
            daemon ? "docker://ancestor" : "registry://registry.test/app:latest");
        return new(reference, reference.GetImageName(), new(info, manifest), daemon ? null : info,
            config, configBytes, content, info.DockerContentDigest, "linux/amd64");
    }

    private sealed class Content : IImageContentSource
    {
        public Dictionary<string, byte[]> Blobs { get; } = [];
        public Task<Valleysoft.DockerRegistryClient.BlobDownloadResult> GetRangeAsync(
            string digest, long start, long? length, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            byte[] bytes = Blobs[digest];
            return Task.FromResult(new Valleysoft.DockerRegistryClient.BlobDownloadResult(
                new MemoryStream(bytes), false, null, null, bytes.Length));
        }
    }

    private sealed class Factory(Func<string, ResolvedImage> open) : IImageSourceFactory
    {
        public Task<ResolvedImage> OpenAsync(string reference, PlatformOptionsBase options,
            CancellationToken token, ImageSourceRequirements requirements = ImageSourceRequirements.LayerContent)
        {
            token.ThrowIfCancellationRequested();
            return Task.FromResult(open(reference));
        }
    }
}
