namespace Valleysoft.Dredge.Tests;

using System.Text.Json.Nodes;
using Valleysoft.DockerRegistryClient;
using Valleysoft.DockerRegistryClient.Models.Manifests;
using Valleysoft.DockerRegistryClient.Models.Manifests.Docker;
using Valleysoft.DockerRegistryClient.Models.Manifests.Oci;
using Valleysoft.Dredge.Commands.Referrer;
using OciManifestReference = Valleysoft.DockerRegistryClient.Models.Manifests.Oci.ManifestReference;

public class ReferrerCheckCommandTests
{
    private const string Registry = "registry.example";

    [Fact]
    public async Task CheckCommand_ResolvesTagCombinesPagesAndReportsAllMatches()
    {
        OciManifestReference sbom = new()
        {
            ArtifactType = "application/spdx+json",
            Digest = "sha256:sbom"
        };
        OciManifestReference provenance = new()
        {
            ArtifactType = "application/vnd.in-toto+json",
            Digest = "sha256:provenance"
        };
        Mock<IDockerRegistryClient> client = CreateClient();
        client
            .Setup(o => o.Manifests.GetAsync("repo", "tag", It.IsAny<CancellationToken>()))
            .ReturnsAsync(CreateManifestInfo("sha256:image"));
        client
            .Setup(o => o.Referrers.GetAsync("repo", "sha256:image", null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Page<OciImageIndex>(
                new OciImageIndex { Manifests = [sbom] },
                "next"));
        client
            .Setup(o => o.Referrers.GetNextAsync("next", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Page<OciImageIndex>(
                new OciImageIndex { Manifests = [provenance] },
                null));
        using StringWriter output = new();
        TestCheckCommand command = new(CreateFactory(client.Object), output)
        {
            Options = new CheckOptions
            {
                Image = $"{Registry}/repo:tag",
                ArtifactTypes =
                [
                    "application/spdx+json",
                    "application/vnd.in-toto+json"
                ]
            }
        };

        await command.RunAsync();

        Assert.Equal(
            $"""
            PASS application/spdx+json
              sha256:sbom
            PASS application/vnd.in-toto+json
              sha256:provenance

            """.ReplaceLineEndings(),
            output.ToString());
        client.Verify(
            o => o.Referrers.GetAsync("repo", "sha256:image", null, It.IsAny<CancellationToken>()),
            Times.Once);
        client.Verify(
            o => o.Referrers.GetNextAsync("next", It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task CheckCommand_WhenTypeIsMissing_ReportsEveryResultAndExitsTwo()
    {
        Mock<IDockerRegistryClient> client = CreateClient();
        client
            .Setup(o => o.Referrers.GetAsync("repo", "sha256:image", null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Page<OciImageIndex>(
                new OciImageIndex
                {
                    Manifests =
                    [
                        new OciManifestReference
                        {
                            ArtifactType = "application/spdx+json",
                            Digest = "sha256:sbom"
                        }
                    ]
                },
                null));
        using StringWriter output = new();
        TestCheckCommand command = new(CreateFactory(client.Object), output)
        {
            Options = new CheckOptions
            {
                Image = $"{Registry}/repo@sha256:image",
                ArtifactTypes =
                [
                    "application/spdx+json",
                    "application/vnd.in-toto+json"
                ]
            }
        };

        CommandExitException exception = await Assert.ThrowsAsync<CommandExitException>(command.RunAsync);

        Assert.Equal(2, exception.ExitCode);
        Assert.Equal(
            $"""
            PASS application/spdx+json
              sha256:sbom
            FAIL application/vnd.in-toto+json

            """.ReplaceLineEndings(),
            output.ToString());
        client.Verify(
            o => o.Manifests.GetAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task CheckCommand_WithJsonOutput_IncludesMatchingDescriptors()
    {
        Mock<IDockerRegistryClient> client = CreateClient();
        client
            .Setup(o => o.Referrers.GetAsync("repo", "sha256:image", null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Page<OciImageIndex>(
                new OciImageIndex
                {
                    Manifests =
                    [
                        new OciManifestReference
                        {
                            ArtifactType = "application/spdx+json",
                            Digest = "sha256:sbom",
                            MediaType = "application/vnd.oci.image.manifest.v1+json",
                            Size = 123
                        }
                    ]
                },
                null));
        using StringWriter output = new();
        TestCheckCommand command = new(CreateFactory(client.Object), output)
        {
            Options = new CheckOptions
            {
                Image = $"{Registry}/repo@sha256:image",
                ArtifactTypes = ["application/spdx+json"],
                OutputFormat = CheckOutput.Json
            }
        };

        await command.RunAsync();
        JsonObject json = JsonNode.Parse(output.ToString())!.AsObject();

        Assert.True(json["succeeded"]!.GetValue<bool>());
        Assert.True(json["results"]![0]!["found"]!.GetValue<bool>());
        Assert.Equal("application/spdx+json", json["results"]![0]!["artifactType"]!.GetValue<string>());
        Assert.Equal("sha256:sbom", json["results"]![0]!["referrers"]![0]!["digest"]!.GetValue<string>());
        Assert.Equal(123, json["results"]![0]!["referrers"]![0]!["size"]!.GetValue<int>());
    }

    [Fact]
    public async Task CheckCommand_WhenRegistryRequestFails_ExitsOne()
    {
        Mock<IDockerRegistryClient> client = CreateClient();
        client
            .Setup(o => o.Referrers.GetAsync("repo", "sha256:image", null, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("failure"));
        using StringWriter output = new();
        TestCheckCommand command = new(CreateFactory(client.Object), output)
        {
            Options = new CheckOptions
            {
                Image = $"{Registry}/repo@sha256:image",
                ArtifactTypes = ["application/spdx+json"]
            }
        };

        CommandExitException exception = await Assert.ThrowsAsync<CommandExitException>(command.RunAsync);

        Assert.Equal(1, exception.ExitCode);
    }

    private static Mock<IDockerRegistryClient> CreateClient() =>
        new() { DefaultValue = DefaultValue.Mock };

    private static IDockerRegistryClientFactory CreateFactory(IDockerRegistryClient client)
    {
        Mock<IDockerRegistryClientFactory> factory = new();
        factory.Setup(o => o.GetClientAsync(It.IsAny<string?>())).ReturnsAsync(client);
        return factory.Object;
    }

    private static ManifestInfo CreateManifestInfo(string manifestDigest) =>
        new(
            "application/test",
            manifestDigest,
            new DockerManifest
            {
                Config = new ManifestConfig { Digest = "sha256:config" },
                Layers = []
            });

    private sealed class TestCheckCommand : CheckCommand
    {
        public TestCheckCommand(IDockerRegistryClientFactory factory, TextWriter output)
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
