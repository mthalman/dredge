namespace Valleysoft.Dredge.Tests;

using Valleysoft.DockerRegistryClient.Models.Manifests;
using Valleysoft.DockerRegistryClient.Models.Manifests.Docker;
using Valleysoft.Dredge.Commands;
using DockerManifestReference = Valleysoft.DockerRegistryClient.Models.Manifests.Docker.ManifestReference;

public class ExplorerSourceTests
{
    [Fact]
    public async Task ResolveAsync_ExactPlatformDistinguishesVariants()
    {
        Mock<IDockerRegistryClient> client = CreateClient(out DockerManifest v7);

        (ResolvedManifest resolved, IReadOnlyList<ExplorerPlatform> platforms, ExplorerPlatform? platform) =
            await ExplorerSource.ResolveAsync(
                client.Object,
                ImageName.Parse("image"),
                new PlatformOptionsBase(),
                new ExplorerPlatform("linux", "arm", "v7", null),
                TestContext.Current.CancellationToken);

        Assert.Same(v7, resolved.Manifest);
        Assert.Equal(new ExplorerPlatform("linux", "arm", "v7", null), platform);
        Assert.Equal(
            [new ExplorerPlatform("linux", "arm", "v6", null), new ExplorerPlatform("linux", "arm", "v7", null)],
            platforms);
    }

    [Fact]
    public async Task ResolveAsync_MissingExactPlatformExplainsAvailablePlatforms()
    {
        Mock<IDockerRegistryClient> client = CreateClient(out _);

        InvalidOperationException exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ExplorerSource.ResolveAsync(
                client.Object,
                ImageName.Parse("image"),
                new PlatformOptionsBase(),
                new ExplorerPlatform("linux", "arm64", null, null),
                TestContext.Current.CancellationToken));

        Assert.Contains("linux/arm/v6", exception.Message);
        Assert.Contains("linux/arm/v7", exception.Message);
    }

    private static Mock<IDockerRegistryClient> CreateClient(out DockerManifest v7Manifest)
    {
        ManifestList list = new()
        {
            Manifests =
            [
                CreateReference("sha256:v6", "linux", "arm", "v6"),
                CreateReference("sha256:v7", "linux", "arm", "v7"),
                CreateReference("sha256:win", "windows", "amd64", null)
            ]
        };
        v7Manifest = new DockerManifest { Layers = [] };
        Mock<IDockerRegistryClient> client = new() { DefaultValue = DefaultValue.Mock };
        client
            .Setup(o => o.Manifests.GetAsync("library/image", "latest", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ManifestInfo("application/index", "sha256:index", list));
        client
            .Setup(o => o.Manifests.GetAsync("library/image", "sha256:v6", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ManifestInfo("application/manifest", "sha256:v6", new DockerManifest { Layers = [] }));
        client
            .Setup(o => o.Manifests.GetAsync("library/image", "sha256:v7", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ManifestInfo("application/manifest", "sha256:v7", v7Manifest));
        return client;
    }

    private static DockerManifestReference CreateReference(
        string digest, string os, string architecture, string? variant) =>
        new()
        {
            Digest = digest,
            Platform = new ManifestPlatform { Os = os, Architecture = architecture, Variant = variant! }
        };
}
