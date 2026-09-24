using System.CommandLine;
using Spectre.Console;
using Spectre.Console.Rendering;
using Valleysoft.DockerRegistryClient;
using Valleysoft.DockerRegistryClient.Models.Manifests;
using Valleysoft.DockerRegistryClient.Models.Manifests.Docker;
using Valleysoft.DockerRegistryClient.Models.Manifests.Oci;
using Valleysoft.Dredge.Commands;
using ManifestDeleteCommand = Valleysoft.Dredge.Commands.Manifest.DeleteCommand;
using TagDeleteCommand = Valleysoft.Dredge.Commands.Tag.DeleteCommand;

namespace Valleysoft.Dredge.Tests;

public sealed class DeleteCommandTests
{
    private const string Digest = "sha256:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
    private const string Image = "registry.example/repo:tag";

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task InvalidArguments_NeverCreateClient(bool tagOnly)
    {
        string[][] cases =
        [
            [],
            ["repo"],
            ["registry.example:5000/repo"],
            ["[::1]:5000/repo"],
            ["Invalid/Repo:tag"],
            ["repo:"],
            ["repo@sha256:invalid"],
            [$"repo:tag@{Digest}"],
            [Image, "extra"],
            [Image, "--os", "linux"],
            [Image, "--arch", "amd64"],
            [Image, "--os-version", "1"]
        ];
        foreach (string[] args in cases)
        {
            using Harness harness = new(tagOnly);
            Assert.NotEqual(0, await harness.RunAsync([..args, "--yes"]));
            harness.Factory.VerifyNoOtherCalls();
        }
    }

    [Fact]
    public async Task TagDeletion_RejectsDigest()
    {
        using Harness harness = new(tagOnly: true);
        Assert.NotEqual(0, await harness.RunAsync([$"repo@{Digest}", "--yes"]));
        Assert.Contains("explicit tag, not a digest", harness.Error.ToString());
        harness.Factory.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData("repo:latest", null, "library/repo", "latest")]
    [InlineData("registry.example:5000/team/repo:v1", "registry.example:5000", "team/repo", "v1")]
    [InlineData("[::1]:5000/repo:v1", "[::1]:5000", "repo", "v1")]
    public async Task TagDeletion_UsesExplicitTagAndExistingNormalization(
        string image, string? registry, string repository, string tag)
    {
        using Harness harness = new(tagOnly: true);
        harness.ExpectClient(registry);
        harness.Manifests.Setup(m => m.DeleteTagAsync(repository, tag, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        Assert.Equal(0, await harness.RunAsync([image, "-y"]));

        harness.Manifests.Verify(m => m.DeleteTagAsync(repository, tag, It.IsAny<CancellationToken>()), Times.Once);
        harness.Manifests.VerifyNoOtherCalls();
        Assert.Contains("Deleted tag", harness.Output.ToString());
        Assert.Equal(0, harness.PromptCount);
        harness.Client.Verify(c => c.Dispose(), Times.Once);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RedirectedInput_RequiresYesBeforeClientCreation(bool tagOnly)
    {
        using Harness harness = new(tagOnly) { InputRedirected = true };

        Assert.Equal(1, await harness.RunAsync([Image]));

        Assert.Contains("--yes", harness.Error.ToString());
        Assert.Empty(harness.Output.ToString());
        Assert.Equal(0, harness.PromptCount);
        harness.Factory.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Yes_BypassesRedirectedInputAndPrompt(bool tagOnly)
    {
        using Harness harness = new(tagOnly) { InputRedirected = true };
        harness.ExpectClient();
        if (tagOnly)
        {
            harness.Manifests.Setup(m => m.DeleteTagAsync("repo", "tag", It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);
        }
        else
        {
            harness.Manifests.Setup(m => m.GetDigestAsync("repo", "tag", It.IsAny<CancellationToken>()))
                .ReturnsAsync(Digest);
            harness.Manifests.Setup(m => m.DeleteAsync("repo", Digest, It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);
        }

        Assert.Equal(0, await harness.RunAsync([Image, "--yes"]));
        Assert.Equal(0, harness.PromptCount);
        Assert.Empty(harness.Error.ToString());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task DecliningConfirmation_DoesNotDelete(bool tagOnly)
    {
        using Harness harness = new(tagOnly) { Confirm = false };
        if (!tagOnly)
        {
            harness.ExpectClient();
            harness.ExpectInspection(new DockerManifest());
        }

        Assert.Equal(1, await harness.RunAsync([Image]));

        Assert.Equal(1, harness.PromptCount);
        Assert.Contains("Deletion canceled.", harness.Error.ToString());
        Assert.Empty(harness.Output.ToString());
        if (tagOnly)
        {
            harness.Factory.VerifyNoOtherCalls();
        }
        else
        {
            harness.Manifests.Verify(m => m.GetAsync("repo", "tag", It.IsAny<CancellationToken>()), Times.Once);
            harness.Manifests.VerifyNoOtherCalls();
            harness.Client.Verify(c => c.Dispose(), Times.Once);
        }
    }

    [Theory]
    [InlineData("image")]
    [InlineData("docker-list")]
    [InlineData("oci-index")]
    public async Task ManifestConfirmation_InspectsTopLevelAndPinsDigest(string kind)
    {
        using Harness harness = new(tagOnly: false);
        harness.ExpectClient();
        IManifest manifest = kind switch
        {
            "docker-list" => new ManifestList(),
            "oci-index" => new OciImageIndex(),
            _ => new DockerManifest()
        };
        harness.ExpectInspection(manifest);
        harness.Manifests.Setup(m => m.DeleteAsync("repo", Digest, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        harness.OnPrompt = () =>
        {
            harness.Manifests.Setup(m => m.GetAsync("repo", "tag", It.IsAny<CancellationToken>()))
                .ThrowsAsync(new InvalidOperationException("The tag changed; it must not be resolved again."));
        };

        Assert.Equal(0, await harness.RunAsync([Image]));

        Assert.Contains(Image, harness.Prompt);
        Assert.Contains(Digest, harness.Prompt);
        Assert.Contains("All tags", harness.Prompt);
        Assert.Equal(kind != "image", harness.Prompt.Contains("manifest list/image index"));
        if (kind != "image")
        {
            Assert.Contains("all platforms", harness.Prompt);
            Assert.Contains("Child manifests and layers will not be deleted", harness.Prompt);
        }
        harness.Manifests.Verify(m => m.GetAsync("repo", "tag", It.IsAny<CancellationToken>()), Times.Once);
        harness.Manifests.Verify(m => m.DeleteAsync("repo", Digest, It.IsAny<CancellationToken>()), Times.Once);
        harness.Manifests.VerifyNoOtherCalls();
        Assert.Equal($"Deleted manifest 'registry.example/repo@{Digest}'.{Environment.NewLine}", harness.Output.ToString());
    }

    [Fact]
    public async Task ManifestDigest_WithYes_DoesNotResolveOrInspect()
    {
        using Harness harness = new(tagOnly: false);
        harness.ExpectClient();
        harness.Manifests.Setup(m => m.DeleteAsync("repo", Digest, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        Assert.Equal(0, await harness.RunAsync([$"registry.example/repo@{Digest}", "--yes"]));

        harness.Manifests.Verify(m => m.DeleteAsync("repo", Digest, It.IsAny<CancellationToken>()), Times.Once);
        harness.Manifests.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task ManifestDigest_ConfirmationInspectsButPreservesExplicitDigest()
    {
        using Harness harness = new(tagOnly: false);
        harness.ExpectClient();
        harness.Manifests.Setup(m => m.GetAsync("repo", Digest, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ManifestInfo("application/test", "sha256:" + new string('a', 64), new OciImageIndex()));
        harness.Manifests.Setup(m => m.DeleteAsync("repo", Digest, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        Assert.Equal(0, await harness.RunAsync([$"registry.example/repo@{Digest}"]));

        Assert.Contains(Digest, harness.Prompt);
        Assert.Contains("manifest list/image index", harness.Prompt);
        harness.Manifests.Verify(m => m.GetAsync("repo", Digest, It.IsAny<CancellationToken>()), Times.Once);
        harness.Manifests.Verify(m => m.DeleteAsync("repo", Digest, It.IsAny<CancellationToken>()), Times.Once);
        harness.Manifests.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task TagConfirmation_ExplainsPreservedManifest()
    {
        using Harness harness = new(tagOnly: true);
        harness.ExpectClient();
        harness.Manifests.Setup(m => m.DeleteTagAsync("repo", "tag", It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        Assert.Equal(0, await harness.RunAsync([Image]));

        Assert.Contains(Image, harness.Prompt);
        Assert.Contains("manifest and other tags will not be deleted", harness.Prompt);
        Assert.Equal(1, harness.PromptCount);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task DeleteFailure_IsNotSuppressedByYesAndDoesNotFallback(bool tagOnly)
    {
        using Harness harness = new(tagOnly);
        harness.ExpectClient();
        if (tagOnly)
        {
            harness.Manifests.Setup(m => m.DeleteTagAsync("repo", "tag", It.IsAny<CancellationToken>()))
                .ThrowsAsync(new InvalidOperationException("Deletion is not supported."));
        }
        else
        {
            harness.Manifests.Setup(m => m.DeleteAsync("repo", Digest, It.IsAny<CancellationToken>()))
                .ThrowsAsync(new InvalidOperationException("Manifest not found."));
        }
        string reference = tagOnly ? Image : $"registry.example/repo@{Digest}";

        Assert.Equal(1, await harness.RunAsync([reference, "--yes"]));

        Assert.Empty(harness.Output.ToString());
        Assert.Contains(tagOnly ? "Deletion is not supported." : "Manifest not found.", harness.Error.ToString());
        harness.Manifests.Verify(m => m.DeleteAsync("repo", It.IsAny<string>(), It.IsAny<CancellationToken>()),
            tagOnly ? Times.Never() : Times.Once());
        harness.Client.Verify(c => c.Dispose(), Times.Once);
    }

    [Fact]
    public async Task ResolutionFailure_DoesNotPromptOrDelete()
    {
        using Harness harness = new(tagOnly: false);
        harness.ExpectClient();
        harness.Manifests.Setup(m => m.GetAsync("repo", "tag", It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Manifest not found."));

        Assert.Equal(1, await harness.RunAsync([Image]));

        Assert.Equal(0, harness.PromptCount);
        Assert.Empty(harness.Output.ToString());
        harness.Manifests.Verify(m => m.GetAsync("repo", "tag", It.IsAny<CancellationToken>()), Times.Once);
        harness.Manifests.VerifyNoOtherCalls();
        harness.Client.Verify(c => c.Dispose(), Times.Once);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CancellationDuringConfirmation_DoesNotDelete(bool tagOnly)
    {
        using CancellationTokenSource cancellation = new();
        using Harness harness = new(tagOnly) { OnPrompt = cancellation.Cancel };
        if (!tagOnly)
        {
            harness.ExpectClient();
            harness.ExpectInspection(new DockerManifest());
        }

        Assert.NotEqual(0, await harness.RunAsync([Image], cancellation.Token));

        Assert.Empty(harness.Output.ToString());
        harness.Manifests.Verify(m => m.DeleteAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        harness.Manifests.Verify(m => m.DeleteTagAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [InlineData('y', ConsoleKey.Y, true)]
    [InlineData('Y', ConsoleKey.Y, true)]
    [InlineData('n', ConsoleKey.N, false)]
    [InlineData('\r', ConsoleKey.Enter, false)]
    [InlineData('\u0004', ConsoleKey.D, false)]
    [InlineData('\u001a', ConsoleKey.Z, false)]
    [InlineData('\0', ConsoleKey.None, false)]
    public async Task ActualPrompt_DefaultsToNoAndHandlesEndOfInput(char character, ConsoleKey key, bool expected)
    {
        using StringWriter error = new();
        IAnsiConsole realConsole = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Out = new AnsiConsoleOutput(error),
            Interactive = InteractionSupport.Yes,
            Ansi = AnsiSupport.No
        });
        Mock<IAnsiConsoleInput> input = new(MockBehavior.Strict);
        input.Setup(i => i.ReadKeyAsync(true, It.IsAny<CancellationToken>()))
            .ReturnsAsync(key == ConsoleKey.None ? null : new ConsoleKeyInfo(character, key, false, false, false));
        Mock<IAnsiConsole> console = new(MockBehavior.Strict);
        console.SetupGet(c => c.Profile).Returns(realConsole.Profile);
        console.SetupGet(c => c.Input).Returns(input.Object);
        console.SetupGet(c => c.ExclusivityMode).Returns(realConsole.ExclusivityMode);
        console.SetupGet(c => c.Cursor).Returns(realConsole.Cursor);
        console.SetupGet(c => c.Pipeline).Returns(realConsole.Pipeline);
        console.Setup(c => c.Write(It.IsAny<IRenderable>()))
            .Callback<IRenderable>(realConsole.Write);

        bool confirmed = await DeleteConfirmation.PromptAsync(
            "Delete '[::1]/repo:tag'?", console.Object, TestContext.Current.CancellationToken);

        Assert.Equal(expected, confirmed);
        Assert.Contains("[::1]/repo:tag", error.ToString());
        Assert.Contains("(n)", error.ToString());
        input.Verify(i => i.ReadKeyAsync(true, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task DeleteReceivesCancellationTokenAndDisposesOnCancellation(bool tagOnly)
    {
        using CancellationTokenSource cancellation = new();
        using Harness harness = new(tagOnly);
        harness.ExpectClient();
        CancellationToken observedToken = default;
        Task Delete(string repository, string reference, CancellationToken ct)
        {
            observedToken = ct;
            cancellation.Cancel();
            ct.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }
        if (tagOnly)
        {
            harness.Manifests.Setup(m => m.DeleteTagAsync("repo", "tag", It.IsAny<CancellationToken>()))
                .Returns((Func<string, string, CancellationToken, Task>)Delete);
        }
        else
        {
            harness.Manifests.Setup(m => m.DeleteAsync("repo", Digest, It.IsAny<CancellationToken>()))
                .Returns((Func<string, string, CancellationToken, Task>)Delete);
        }

        Assert.NotEqual(0, await harness.RunAsync(
            [tagOnly ? Image : $"registry.example/repo@{Digest}", "--yes"], cancellation.Token));

        Assert.True(observedToken.IsCancellationRequested);
        Assert.Empty(harness.Output.ToString());
        harness.Client.Verify(c => c.Dispose(), Times.Once);
    }

    private sealed class Harness : IDisposable
    {
        private readonly Command command;
        private readonly RecordingProcessTerminator terminator = new();

        public Harness(bool tagOnly)
        {
            DeleteConfirmation confirmation = new(
                () => InputRedirected,
                (message, error, ct) =>
                {
                    Assert.Same(Error, error);
                    Assert.True(ct.CanBeCanceled);
                    PromptCount++;
                    Prompt = message;
                    OnPrompt?.Invoke();
                    return Task.FromResult(Confirm);
                });
            command = tagOnly
                ? new TestTagDeleteCommand(Factory.Object, Output, Error, confirmation)
                : new TestManifestDeleteCommand(Factory.Object, Output, Error, confirmation);
            ((IProcessTerminationAware)command).ProcessTerminator = terminator;
            Client.Setup(c => c.Manifests).Returns(Manifests.Object);
            Client.Setup(c => c.Dispose());
        }

        public Mock<IDockerRegistryClientFactory> Factory { get; } = new(MockBehavior.Strict);
        public Mock<IDockerRegistryClient> Client { get; } = new(MockBehavior.Strict);
        public Mock<IManifestWriteOperations> Manifests { get; } = new(MockBehavior.Strict);
        public StringWriter Output { get; } = new();
        public StringWriter Error { get; } = new();
        public bool InputRedirected { get; set; }
        public bool Confirm { get; set; } = true;
        public Action? OnPrompt { get; set; }
        public int PromptCount { get; private set; }
        public string Prompt { get; private set; } = string.Empty;

        public void ExpectClient(string? registry = "registry.example") =>
            Factory.Setup(f => f.GetClientAsync(registry, It.IsAny<CancellationToken>()))
                .ReturnsAsync(Client.Object);

        public void ExpectInspection(IManifest manifest) =>
            Manifests.Setup(m => m.GetAsync("repo", "tag", It.IsAny<CancellationToken>()))
                .ReturnsAsync(new ManifestInfo("application/test", Digest, manifest));

        public async Task<int> RunAsync(string[] args, CancellationToken cancellationToken = default)
        {
            int result = await command.Parse(args).InvokeAsync(
                new InvocationConfiguration { Output = Output, Error = Error },
                cancellationToken);
            return terminator.ExitCode ?? result;
        }

        public void Dispose()
        {
            Output.Dispose();
            Error.Dispose();
        }
    }

    private sealed class TestTagDeleteCommand(
        IDockerRegistryClientFactory factory, TextWriter output, TextWriter error, DeleteConfirmation confirmation)
        : TagDeleteCommand(factory, output, confirmation)
    {
        protected override TextWriter Error => error;
    }

    private sealed class TestManifestDeleteCommand(
        IDockerRegistryClientFactory factory, TextWriter output, TextWriter error, DeleteConfirmation confirmation)
        : ManifestDeleteCommand(factory, output, confirmation)
    {
        protected override TextWriter Error => error;
    }
}
