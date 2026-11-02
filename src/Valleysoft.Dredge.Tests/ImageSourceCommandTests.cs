using System.CommandLine;
using Valleysoft.Dredge.Commands;
using Valleysoft.Dredge.Commands.Image;
using Valleysoft.Dredge.Commands.Manifest;

namespace Valleysoft.Dredge.Tests;

public class ImageSourceCommandTests
{
    [Theory]
    [InlineData("docker://myimage:dev")]
    [InlineData("docker://sha256:dev")]
    [InlineData("docker://sha256:abc")]
    [InlineData("registry://localhost:5000/team/image:dev")]
    [InlineData("docker:latest")]
    [InlineData("registry:2")]
    public void ImageCommands_AcceptUnambiguousSources(string reference)
    {
        Command image = new ImageCommand(Mock.Of<IDockerRegistryClientFactory>());
        foreach (string command in new[] { "inspect", "os", "dockerfile", "explore", "ls", "cat", "extract", "save-layers" })
        {
            string[] arguments = command switch
            {
                "cat" => [command, reference, "/hello"],
                "extract" => [command, reference, "/hello", "output"],
                "save-layers" => [command, reference, "output"],
                _ => [command, reference]
            };
            Assert.Empty(image.Parse(arguments).Errors);
        }
    }

    [Theory]
    [InlineData("metadata")]
    [InlineData("layers")]
    [InlineData("files")]
    public void CompareCommands_AcceptIndependentOperandSources(string command)
    {
        Command image = new ImageCommand(Mock.Of<IDockerRegistryClientFactory>());
        foreach (string baseImage in new[] { "docker://candidate:dev", "docker://sha256:dev", "registry://team/image:release" })
        {
            foreach (string targetImage in new[] { "docker://candidate:dev", "docker://sha256:abc", "team/image:release" })
            {
                Assert.Empty(image.Parse(["compare", command, baseImage, targetImage]).Errors);
            }
        }
    }

    [Fact]
    public void ImageCommands_RejectConflictingConnectionFlags()
    {
        Command image = new ImageCommand(Mock.Of<IDockerRegistryClientFactory>());
        var result = image.Parse(
            ["inspect", "docker://image:dev", "--docker-context", "desktop-linux", "--docker-host", "unix:///var/run/docker.sock"]);
        Assert.Contains(result.Errors, static error => error.Message.Contains("cannot be combined", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("podman://image")]
    [InlineData("docker://")]
    [InlineData("registry://")]
    public void ImageCommands_RejectInvalidSources(string reference)
    {
        Command image = new ImageCommand(Mock.Of<IDockerRegistryClientFactory>());
        Assert.NotEmpty(image.Parse(["inspect", reference]).Errors);
    }

    [Fact]
    public void RegistryOnlyCommands_DoNotAcceptDaemonReferences()
    {
        Command manifests = new ManifestCommand(Mock.Of<IDockerRegistryClientFactory>());
        Assert.NotEmpty(manifests.Parse(["get", "docker://image:dev"]).Errors);
        Assert.NotEmpty(manifests.Parse(["delete", "docker://image:dev", "--yes"]).Errors);
    }

    [Fact]
    public async Task DaemonAcquisitionFailure_DoesNotFallbackToRegistry()
    {
        Mock<IDockerRegistryClientFactory> registry = new(MockBehavior.Strict);
        FailingSourceFactory sources = new();
        RecordingProcessTerminator terminator = new();
        using StringWriter output = new();
        InspectCommand command = new(registry.Object, output) { ImageSourceFactory = sources };
        ((IProcessTerminationAware)command).ProcessTerminator = terminator;

        await command.Parse(["docker://missing:dev"]).InvokeAsync(
            new InvocationConfiguration(), TestContext.Current.CancellationToken);

        Assert.Equal(1, terminator.ExitCode);
        Assert.Equal(string.Empty, output.ToString());
        Assert.Equal("docker://missing:dev", sources.RequestedReference);
        registry.VerifyNoOtherCalls();
    }

    private sealed class FailingSourceFactory : IImageSourceFactory
    {
        public string? RequestedReference { get; private set; }

        public Task<ResolvedImage> OpenAsync(
            string reference, PlatformOptionsBase options, CancellationToken cancellationToken,
            ImageSourceRequirements requirements = ImageSourceRequirements.LayerContent)
        {
            RequestedReference = reference;
            return Task.FromException<ResolvedImage>(new IOException("The selected Docker daemon is inaccessible."));
        }
    }
}
