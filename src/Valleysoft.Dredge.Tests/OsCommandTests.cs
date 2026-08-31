namespace Valleysoft.Dredge.Tests;

using System.Text;
using System.Text.Json;
using Valleysoft.DockerRegistryClient.Models.Images;
using Valleysoft.DockerRegistryClient.Models.Manifests;
using Valleysoft.DockerRegistryClient.Models.Manifests.Docker;
using Valleysoft.Dredge.Commands.Image;

public class OsCommandTests
{
    [Fact]
    public async Task GetWindowsOsInfoAsyncUsesLongestDiffIdPrefixMatch()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        const string OsVersion = "10.0.20348.1366";
        const string Architecture = "amd64";
        const string BaseImageTag = $"{OsVersion}-{Architecture}";

        Image targetImage = new()
        {
            Os = "windows",
            OsVersion = OsVersion,
            Architecture = Architecture,
            RootFilesystem = new RootFilesystem
            {
                Type = "layers",
                DiffIds = ["baseDiff0", "baseDiff1", "appDiff"]
            }
        };
        DockerManifest targetManifest = new()
        {
            Layers =
            [
                new ManifestLayer
                {
                    Digest = "repackedLayerDigest"
                }
            ]
        };

        Mock<IDockerRegistryClientFactory> clientFactoryMock = new();
        Mock<IDockerRegistryClient> mcrClientMock = new();
        clientFactoryMock
            .Setup(o => o.GetClientAsync(RegistryHelper.McrRegistry))
            .ReturnsAsync(mcrClientMock.Object);
        mcrClientMock
            .Setup(o => o.Blobs.ExistsAsync(
                It.IsAny<string>(), "repackedLayerDigest", It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);
        mcrClientMock
            .Setup(o => o.Manifests.ExistsAsync(
                It.IsAny<string>(), BaseImageTag, It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        SetupBaseImage(
            mcrClientMock,
            "windows/nanoserver",
            BaseImageTag,
            "nanoConfig",
            ["baseDiff0"],
            historyCount: 1);
        SetupBaseImage(
            mcrClientMock,
            "windows/servercore",
            BaseImageTag,
            "serverCoreConfig",
            ["baseDiff0", "baseDiff1"],
            historyCount: 2);

        WindowsImageInfo? result = await OsCommand.GetWindowsOsInfoAsync(
            targetImage, targetManifest, clientFactoryMock.Object, cancellationToken);

        Assert.NotNull(result);
        Assert.Equal(WindowsType.ServerCore, result.Info.Type);
        Assert.Equal(OsVersion, result.Info.Version);
        Assert.Equal("windows/servercore", result.Repo);
        Assert.Equal(2, result.BaseHistoryCount);
        mcrClientMock.Verify(o => o.Manifests.ExistsAsync(
            "windows/servercore", BaseImageTag, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task GetWindowsOsInfoAsyncRetainsLegacyDigestDetection()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        Image targetImage = new()
        {
            Os = "windows",
            OsVersion = "10.0.17763.1",
            Architecture = "amd64"
        };
        DockerManifest targetManifest = new()
        {
            Layers =
            [
                new ManifestLayer { Digest = "baseLayer0" },
                new ManifestLayer { Digest = "baseLayer1" },
                new ManifestLayer { Digest = "appLayer" }
            ]
        };

        Mock<IDockerRegistryClientFactory> clientFactoryMock = new();
        Mock<IDockerRegistryClient> mcrClientMock = new();
        clientFactoryMock
            .Setup(o => o.GetClientAsync(RegistryHelper.McrRegistry))
            .ReturnsAsync(mcrClientMock.Object);
        mcrClientMock
            .Setup(o => o.Blobs.ExistsAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);
        mcrClientMock
            .Setup(o => o.Blobs.ExistsAsync(
                "windows/servercore",
                It.Is<string>(digest => digest == "baseLayer0" || digest == "baseLayer1"),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        WindowsImageInfo? result = await OsCommand.GetWindowsOsInfoAsync(
            targetImage, targetManifest, clientFactoryMock.Object, cancellationToken);

        Assert.NotNull(result);
        Assert.Equal(WindowsType.ServerCore, result.Info.Type);
        Assert.Equal(2, result.BaseHistoryCount);
        mcrClientMock.Verify(o => o.Manifests.ExistsAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    private static void SetupBaseImage(
        Mock<IDockerRegistryClient> mcrClientMock,
        string repo,
        string tag,
        string configDigest,
        string[] diffIds,
        int historyCount)
    {
        mcrClientMock
            .Setup(o => o.Manifests.ExistsAsync(repo, tag, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        mcrClientMock
            .Setup(o => o.Manifests.GetAsync(repo, tag, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ManifestInfo("media-type", "manifest-digest",
                new DockerManifest
                {
                    Config = new ManifestConfig
                    {
                        Digest = configDigest
                    }
                }));

        Image baseImage = new()
        {
            RootFilesystem = new RootFilesystem
            {
                Type = "layers",
                DiffIds = diffIds
            },
            History = Enumerable.Range(0, historyCount)
                .Select(index => new LayerHistory { CreatedBy = $"base instruction {index}" })
                .ToArray()
        };
        mcrClientMock
            .Setup(o => o.Blobs.GetAsync(repo, configDigest, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MemoryStream(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(baseImage))));
    }
}
