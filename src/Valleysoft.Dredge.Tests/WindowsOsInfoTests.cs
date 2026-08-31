namespace Valleysoft.Dredge.Tests;

using Valleysoft.DockerRegistryClient.Models.Images;
using Valleysoft.Dredge.Commands.Image;

public class WindowsOsInfoTests
{
    [Theory]
    [InlineData("windows/nanoserver", WindowsType.NanoServer)]
    [InlineData("windows/servercore", WindowsType.ServerCore)]
    [InlineData("windows/server", WindowsType.Server)]
    [InlineData("windows", WindowsType.Windows)]
    public async Task GetWindowsOsInfoAsync_WhenLayerExists_ReturnsMatchingWindowsType(
        string matchingRepo,
        WindowsType expectedType)
    {
        Mock<IDockerRegistryClient> client = new() { DefaultValue = DefaultValue.Mock };
        client
            .Setup(o => o.Blobs.ExistsAsync(
                It.IsAny<string>(),
                "sha256:layer",
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((string repo, string _, CancellationToken _) => repo == matchingRepo);
        Mock<IDockerRegistryClientFactory> factory = new();
        factory
            .Setup(o => o.GetClientAsync(RegistryHelper.McrRegistry))
            .ReturnsAsync(client.Object);

        (WindowsOsInfo Info, string Repo)? result = await OsCommand.GetWindowsOsInfoAsync(
            new Image { OsVersion = "10.0" },
            "sha256:layer",
            factory.Object);

        Assert.NotNull(result);
        Assert.Equal(expectedType, result.Value.Info.Type);
        Assert.Equal("10.0", result.Value.Info.Version);
        Assert.Equal(matchingRepo, result.Value.Repo);
    }

    [Fact]
    public async Task GetWindowsOsInfoAsync_WhenLayerIsUnknown_ReturnsNull()
    {
        Mock<IDockerRegistryClient> client = new() { DefaultValue = DefaultValue.Mock };
        client
            .Setup(o => o.Blobs.ExistsAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);
        Mock<IDockerRegistryClientFactory> factory = new();
        factory
            .Setup(o => o.GetClientAsync(RegistryHelper.McrRegistry))
            .ReturnsAsync(client.Object);

        (WindowsOsInfo Info, string Repo)? result = await OsCommand.GetWindowsOsInfoAsync(
            new Image(),
            "sha256:layer",
            factory.Object);

        Assert.Null(result);
    }
}
