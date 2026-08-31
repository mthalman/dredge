namespace Valleysoft.Dredge.Tests;

using Valleysoft.DockerRegistryClient.Models.Manifests;
using Valleysoft.DockerRegistryClient.Models.Manifests.Docker;
using Valleysoft.Dredge.Commands;
using DockerManifestReference = Valleysoft.DockerRegistryClient.Models.Manifests.Docker.ManifestReference;

public class ManifestHelperTests
{
    [Fact]
    public async Task GetResolvedManifestAsync_SelectsMatchingPlatform()
    {
        ManifestList manifestList = new()
        {
            Manifests =
            [
                CreateReference("sha256:amd64", "linux", "amd64", "1"),
                CreateReference("sha256:arm64", "linux", "arm64", "1")
            ]
        };
        DockerManifest resolvedManifest = new() { Layers = [] };
        Mock<IDockerRegistryClient> client = new() { DefaultValue = DefaultValue.Mock };
        client
            .Setup(o => o.Manifests.GetAsync("library/image", "latest", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ManifestInfo("application/index", "sha256:index", manifestList));
        client
            .Setup(o => o.Manifests.GetAsync("library/image", "sha256:arm64", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ManifestInfo("application/manifest", "sha256:arm64", resolvedManifest));

        ResolvedManifest result = await ManifestHelper.GetResolvedManifestAsync(
            client.Object,
            ImageName.Parse("image"),
            new PlatformOptionsBase
            {
                Os = "linux",
                OsVersion = "1",
                Architecture = "arm64"
            });

        Assert.Same(resolvedManifest, result.Manifest);
        Assert.Equal("sha256:arm64", result.ManifestInfo.DockerContentDigest);
    }

    [Fact]
    public async Task GetResolvedManifestAsync_UsesSettingsForMissingPlatformOptions()
    {
        ManifestList manifestList = new()
        {
            Manifests =
            [
                CreateReference("sha256:amd64", "linux", "amd64", "1"),
                CreateReference("sha256:arm64", "linux", "arm64", "1")
            ]
        };
        DockerManifest resolvedManifest = new() { Layers = [] };
        Mock<IDockerRegistryClient> client = new() { DefaultValue = DefaultValue.Mock };
        client
            .Setup(o => o.Manifests.GetAsync("library/image", "latest", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ManifestInfo("application/index", "sha256:index", manifestList));
        client
            .Setup(o => o.Manifests.GetAsync("library/image", "sha256:arm64", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ManifestInfo("application/manifest", "sha256:arm64", resolvedManifest));
        AppSettings settings = (AppSettings)Activator.CreateInstance(typeof(AppSettings), nonPublic: true)!;
        settings.Platform.Architecture = "arm64";

        ResolvedManifest result = await ManifestHelper.GetResolvedManifestAsync(
            client.Object,
            ImageName.Parse("image"),
            new PlatformOptionsBase
            {
                Os = "linux",
                OsVersion = "1"
            },
            settings);

        Assert.Same(resolvedManifest, result.Manifest);
        Assert.Equal("sha256:arm64", result.ManifestInfo.DockerContentDigest);
    }

    [Fact]
    public async Task GetResolvedManifestAsync_WhenNoPlatformMatches_Throws()
    {
        ManifestList manifestList = new()
        {
            Manifests = [CreateReference("sha256:amd64", "linux", "amd64", "1")]
        };
        Mock<IDockerRegistryClient> client = new() { DefaultValue = DefaultValue.Mock };
        client
            .Setup(o => o.Manifests.GetAsync("library/image", "latest", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ManifestInfo("application/index", "sha256:index", manifestList));

        Exception exception = await Assert.ThrowsAsync<Exception>(
            () => ManifestHelper.GetResolvedManifestAsync(
                client.Object,
                ImageName.Parse("image"),
                new PlatformOptionsBase
                {
                    Os = "linux",
                    OsVersion = "1",
                    Architecture = "s390x"
                }));

        Assert.StartsWith("Unable to resolve the manifest list tag", exception.Message);
    }

    [Fact]
    public async Task GetResolvedManifestAsync_WhenResolvedTypeIsNotAnImageManifest_Throws()
    {
        Mock<IManifest> manifest = new();
        Mock<IDockerRegistryClient> client = new() { DefaultValue = DefaultValue.Mock };
        client
            .Setup(o => o.Manifests.GetAsync("library/image", "latest", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ManifestInfo("application/unsupported", "sha256:digest", manifest.Object));

        NotSupportedException exception = await Assert.ThrowsAsync<NotSupportedException>(
            () => ManifestHelper.GetResolvedManifestAsync(
                client.Object,
                ImageName.Parse("image"),
                new PlatformOptionsBase()));

        Assert.Contains("application/unsupported", exception.Message);
    }

    private static DockerManifestReference CreateReference(
        string digest,
        string os,
        string architecture,
        string osVersion) =>
        new()
        {
            Digest = digest,
            Platform = new ManifestPlatform
            {
                Os = os,
                Architecture = architecture,
                OsVersion = osVersion
            }
        };
}
