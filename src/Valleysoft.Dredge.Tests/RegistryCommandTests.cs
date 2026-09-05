namespace Valleysoft.Dredge.Tests;

using Newtonsoft.Json.Linq;
using System.Text;
using Valleysoft.DockerRegistryClient;
using Valleysoft.DockerRegistryClient.Models;
using Valleysoft.DockerRegistryClient.Models.Manifests;
using Valleysoft.DockerRegistryClient.Models.Manifests.Docker;
using Valleysoft.DockerRegistryClient.Models.Manifests.Oci;
using Valleysoft.Dredge.Commands.Image;
using DigestCommand = Valleysoft.Dredge.Commands.Manifest.DigestCommand;
using DigestOptions = Valleysoft.Dredge.Commands.Manifest.DigestOptions;
using GetCommand = Valleysoft.Dredge.Commands.Manifest.GetCommand;
using GetOptions = Valleysoft.Dredge.Commands.Manifest.GetOptions;
using ReferrerListCommand = Valleysoft.Dredge.Commands.Referrer.ListCommand;
using ReferrerListOptions = Valleysoft.Dredge.Commands.Referrer.ListOptions;
using RepoListCommand = Valleysoft.Dredge.Commands.Repo.ListCommand;
using RepoListOptions = Valleysoft.Dredge.Commands.Repo.ListOptions;
using ResolveCommand = Valleysoft.Dredge.Commands.Manifest.ResolveCommand;
using ResolveOptions = Valleysoft.Dredge.Commands.Manifest.SetOptions;
using TagListCommand = Valleysoft.Dredge.Commands.Tag.ListCommand;
using TagListOptions = Valleysoft.Dredge.Commands.Tag.ListOptions;
using OciManifestReference = Valleysoft.DockerRegistryClient.Models.Manifests.Oci.ManifestReference;

public class RegistryCommandTests
{
    private const string Registry = "registry.example";

    [Fact]
    public async Task DigestCommand_WritesManifestDigest()
    {
        Mock<IDockerRegistryClient> client = CreateClient();
        client
            .Setup(o => o.Manifests.GetDigestAsync("repo", "tag", It.IsAny<CancellationToken>()))
            .ReturnsAsync("sha256:digest");
        using StringWriter output = new();
        TestDigestCommand command = new(CreateFactory(client.Object), output)
        {
            Options = new DigestOptions { Image = $"{Registry}/repo:tag" }
        };

        await command.RunAsync();

        Assert.Equal($"sha256:digest{Environment.NewLine}", output.ToString());
    }

    [Fact]
    public async Task DigestCommand_WhenExecutionFails_ThrowsInsteadOfTerminatingTestProcess()
    {
        Mock<IDockerRegistryClient> client = CreateClient();
        client
            .Setup(o => o.Manifests.GetDigestAsync("repo", "tag", It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("failure"));
        using StringWriter output = new();
        TestDigestCommand command = new(CreateFactory(client.Object), output)
        {
            Options = new DigestOptions { Image = $"{Registry}/repo:tag" }
        };

        CommandExitException exception = await Assert.ThrowsAsync<CommandExitException>(command.RunAsync);

        Assert.Equal(1, exception.ExitCode);
    }

    [Fact]
    public async Task GetCommand_WritesManifestJson()
    {
        DockerManifest manifest = new()
        {
            SchemaVersion = 2,
            MediaType = "application/test",
            Layers = []
        };
        Mock<IDockerRegistryClient> client = CreateClient();
        client
            .Setup(o => o.Manifests.GetAsync("repo", "tag", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ManifestInfo("application/test", "sha256:digest", manifest));
        using StringWriter output = new();
        TestGetCommand command = new(CreateFactory(client.Object), output)
        {
            Options = new GetOptions { Image = $"{Registry}/repo:tag" }
        };

        await command.RunAsync();
        JObject json = JObject.Parse(output.ToString());

        Assert.Equal(2, json["schemaVersion"]);
        Assert.Equal("application/test", json["mediaType"]);
    }

    [Fact]
    public async Task ResolveCommand_WritesFullyQualifiedDigest()
    {
        Mock<IDockerRegistryClient> client = CreateClient();
        client
            .Setup(o => o.Manifests.GetAsync("repo", "tag", It.IsAny<CancellationToken>()))
            .ReturnsAsync(CreateManifestInfo("sha256:resolved"));
        using StringWriter output = new();
        TestResolveCommand command = new(CreateFactory(client.Object), output)
        {
            Options = new ResolveOptions { Image = $"{Registry}/repo:tag" }
        };

        await command.RunAsync();

        Assert.Equal($"{Registry}/repo@sha256:resolved{Environment.NewLine}", output.ToString());
    }

    [Fact]
    public async Task InspectCommand_WritesFormattedImageConfiguration()
    {
        Mock<IDockerRegistryClient> client = CreateClient();
        client
            .Setup(o => o.Manifests.GetAsync("repo", "tag", It.IsAny<CancellationToken>()))
            .ReturnsAsync(CreateManifestInfo("sha256:manifest", "sha256:config"));
        client
            .Setup(o => o.Blobs.GetAsync("repo", "sha256:config", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MemoryStream(Encoding.UTF8.GetBytes("""{"created":"today","config":{"Env":["A=B"]}}""")));
        using StringWriter output = new();
        TestInspectCommand command = new(CreateFactory(client.Object), output)
        {
            Options = new InspectOptions { Image = $"{Registry}/repo:tag" }
        };

        await command.RunAsync();
        JObject json = JObject.Parse(output.ToString());

        Assert.Equal("today", json["created"]);
        Assert.Equal("A=B", json["config"]?["Env"]?[0]);
    }

    [Fact]
    public async Task RepoListCommand_CombinesPagesAndSortsRepositories()
    {
        Mock<IDockerRegistryClient> client = CreateClient();
        client
            .Setup(o => o.Catalog.GetAsync(null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Page<Catalog>(
                new Catalog { RepositoryNames = ["zebra", "alpha"] },
                "next"));
        client
            .Setup(o => o.Catalog.GetNextAsync("next", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Page<Catalog>(
                new Catalog { RepositoryNames = ["middle"] },
                null));
        using StringWriter output = new();
        TestRepoListCommand command = new(CreateFactory(client.Object), output)
        {
            Options = new RepoListOptions { Registry = Registry }
        };

        await command.RunAsync();

        Assert.Equal(["alpha", "middle", "zebra"], JArray.Parse(output.ToString()).Values<string>());
    }

    [Fact]
    public async Task RepoListCommand_LimitWithinFirstPage_TruncatesAndDoesNotRequestNextPage()
    {
        Mock<IDockerRegistryClient> client = CreateClient();
        client
            .Setup(o => o.Catalog.GetAsync(2, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Page<Catalog>(
                new Catalog { RepositoryNames = ["zebra", "alpha", "middle"] },
                "next"));
        using StringWriter output = new();
        TestRepoListCommand command = new(CreateFactory(client.Object), output)
        {
            Options = new RepoListOptions { Registry = Registry, Limit = 2 }
        };

        await command.RunAsync();

        Assert.Equal(["alpha", "zebra"], JArray.Parse(output.ToString()).Values<string>());
        client.Verify(
            o => o.Catalog.GetNextAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task TagListCommand_CombinesPagesAndSortsTags()
    {
        Mock<IDockerRegistryClient> client = CreateClient();
        client
            .Setup(o => o.Tags.GetAsync("library/repo", null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Page<RepositoryTags>(
                new RepositoryTags { RepositoryName = "library/repo", Tags = ["z", "a"] },
                "next"));
        client
            .Setup(o => o.Tags.GetNextAsync("next", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Page<RepositoryTags>(
                new RepositoryTags { RepositoryName = "library/repo", Tags = ["m"] },
                null));
        using StringWriter output = new();
        TestTagListCommand command = new(CreateFactory(client.Object), output)
        {
            Options = new TagListOptions { Repo = "repo" }
        };

        await command.RunAsync();

        Assert.Equal(["a", "m", "z"], JArray.Parse(output.ToString()).Values<string>());
    }

    [Fact]
    public async Task TagListCommand_LimitWithinLaterPage_StopsAfterLimitAndSortsSubset()
    {
        Mock<IDockerRegistryClient> client = CreateClient();
        client
            .Setup(o => o.Tags.GetAsync("library/repo", 3, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Page<RepositoryTags>(
                new RepositoryTags { RepositoryName = "library/repo", Tags = ["z", "a"] },
                "next"));
        client
            .Setup(o => o.Tags.GetNextAsync("next", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Page<RepositoryTags>(
                new RepositoryTags { RepositoryName = "library/repo", Tags = ["y", "m"] },
                "unused"));
        using StringWriter output = new();
        TestTagListCommand command = new(CreateFactory(client.Object), output)
        {
            Options = new TagListOptions { Repo = "repo", Limit = 3 }
        };

        await command.RunAsync();

        Assert.Equal(["a", "y", "z"], JArray.Parse(output.ToString()).Values<string>());
        client.Verify(
            o => o.Tags.GetNextAsync("next", It.IsAny<CancellationToken>()),
            Times.Once);
        client.Verify(
            o => o.Tags.GetNextAsync("unused", It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task ReferrerListCommand_CombinesAllPages()
    {
        OciManifestReference first = new() { Digest = "sha256:first" };
        OciManifestReference second = new() { Digest = "sha256:second" };
        Mock<IDockerRegistryClient> client = CreateClient();
        client
            .Setup(o => o.Referrers.GetAsync("repo", "sha256:image", "application/test", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Page<OciImageIndex>(
                new OciImageIndex { Manifests = [first] },
                "next"));
        client
            .Setup(o => o.Referrers.GetNextAsync("next", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Page<OciImageIndex>(
                new OciImageIndex { Manifests = [second] },
                null));
        using StringWriter output = new();
        TestReferrerListCommand command = new(CreateFactory(client.Object), output)
        {
            Options = new ReferrerListOptions
            {
                Image = $"{Registry}/repo@sha256:image",
                ArtifactType = "application/test"
            }
        };

        await command.RunAsync();
        string[] digests = JObject.Parse(output.ToString())["manifests"]!
            .Select(manifest => (string)manifest["digest"]!)
            .ToArray();

        Assert.Equal(["sha256:first", "sha256:second"], digests);
        client.Verify(o => o.Referrers.GetNextAsync("next", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ReferrerListCommand_LimitAtPageBoundary_DoesNotRequestNextPage()
    {
        OciManifestReference first = new() { Digest = "sha256:first" };
        OciManifestReference second = new() { Digest = "sha256:second" };
        Mock<IDockerRegistryClient> client = CreateClient();
        client
            .Setup(o => o.Referrers.GetAsync("repo", "sha256:image", null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Page<OciImageIndex>(
                new OciImageIndex
                {
                    Manifests = [first, second],
                    Annotations = new Dictionary<string, string> { ["source"] = "first-page" }
                },
                "next"));
        using StringWriter output = new();
        TestReferrerListCommand command = new(CreateFactory(client.Object), output)
        {
            Options = new ReferrerListOptions
            {
                Image = $"{Registry}/repo@sha256:image",
                Limit = 2
            }
        };

        await command.RunAsync();
        JObject json = JObject.Parse(output.ToString());

        Assert.Equal(
            ["sha256:first", "sha256:second"],
            json["manifests"]!.Select(manifest => (string)manifest["digest"]!));
        Assert.Equal("first-page", (string?)json["annotations"]?["source"]);
        client.Verify(
            o => o.Referrers.GetNextAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    private static Mock<IDockerRegistryClient> CreateClient() =>
        new() { DefaultValue = DefaultValue.Mock };

    private static IDockerRegistryClientFactory CreateFactory(IDockerRegistryClient client)
    {
        Mock<IDockerRegistryClientFactory> factory = new();
        factory.Setup(o => o.GetClientAsync(It.IsAny<string?>())).ReturnsAsync(client);
        return factory.Object;
    }

    private static ManifestInfo CreateManifestInfo(string manifestDigest, string configDigest = "sha256:config") =>
        new(
            "application/test",
            manifestDigest,
            new DockerManifest
            {
                Config = new ManifestConfig { Digest = configDigest },
                Layers = []
            });

    private sealed class TestDigestCommand : DigestCommand
    {
        public TestDigestCommand(IDockerRegistryClientFactory factory, TextWriter output)
            : base(factory, output)
        {
        }

        public Task RunAsync() => ExecuteAsync(TestContext.Current.CancellationToken);

        protected override TextWriter Error => TextWriter.Null;

        protected override void Exit(int exitCode) => throw new CommandExitException(exitCode);
    }

    private sealed class TestGetCommand : GetCommand
    {
        public TestGetCommand(IDockerRegistryClientFactory factory, TextWriter output)
            : base(factory, output)
        {
        }

        public Task RunAsync() => ExecuteAsync(TestContext.Current.CancellationToken);

        protected override TextWriter Error => TextWriter.Null;

        protected override void Exit(int exitCode) => throw new CommandExitException(exitCode);
    }

    private sealed class TestResolveCommand : ResolveCommand
    {
        public TestResolveCommand(IDockerRegistryClientFactory factory, TextWriter output)
            : base(factory, output)
        {
        }

        public Task RunAsync() => ExecuteAsync(TestContext.Current.CancellationToken);

        protected override TextWriter Error => TextWriter.Null;

        protected override void Exit(int exitCode) => throw new CommandExitException(exitCode);
    }

    private sealed class TestInspectCommand : InspectCommand
    {
        public TestInspectCommand(IDockerRegistryClientFactory factory, TextWriter output)
            : base(factory, output)
        {
        }

        public Task RunAsync() => ExecuteAsync(TestContext.Current.CancellationToken);

        protected override TextWriter Error => TextWriter.Null;

        protected override void Exit(int exitCode) => throw new CommandExitException(exitCode);
    }

    private sealed class TestRepoListCommand : RepoListCommand
    {
        public TestRepoListCommand(IDockerRegistryClientFactory factory, TextWriter output)
            : base(factory, output)
        {
        }

        public Task RunAsync() => ExecuteAsync(TestContext.Current.CancellationToken);

        protected override TextWriter Error => TextWriter.Null;

        protected override void Exit(int exitCode) => throw new CommandExitException(exitCode);
    }

    private sealed class TestTagListCommand : TagListCommand
    {
        public TestTagListCommand(IDockerRegistryClientFactory factory, TextWriter output)
            : base(factory, output)
        {
        }

        public Task RunAsync() => ExecuteAsync(TestContext.Current.CancellationToken);

        protected override TextWriter Error => TextWriter.Null;

        protected override void Exit(int exitCode) => throw new CommandExitException(exitCode);
    }

    private sealed class TestReferrerListCommand : ReferrerListCommand
    {
        public TestReferrerListCommand(IDockerRegistryClientFactory factory, TextWriter output)
            : base(factory, output)
        {
        }

        public Task RunAsync() => ExecuteAsync(TestContext.Current.CancellationToken);

        protected override TextWriter Error => TextWriter.Null;

        protected override void Exit(int exitCode) => throw new CommandExitException(exitCode);
    }

    private sealed class CommandExitException : Exception
    {
        public CommandExitException(int exitCode)
        {
            ExitCode = exitCode;
        }

        public int ExitCode { get; }
    }
}
