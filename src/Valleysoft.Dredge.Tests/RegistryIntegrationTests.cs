using Newtonsoft.Json.Linq;
using System.CommandLine;
using System.Text;
using Valleysoft.DockerRegistryClient;
using DigestCommand = Valleysoft.Dredge.Commands.Manifest.DigestCommand;
using GetCommand = Valleysoft.Dredge.Commands.Manifest.GetCommand;
using ResolveCommand = Valleysoft.Dredge.Commands.Manifest.ResolveCommand;
using ReferrerCheckCommand = Valleysoft.Dredge.Commands.Referrer.CheckCommand;
using ReferrerGetCommand = Valleysoft.Dredge.Commands.Referrer.GetCommand;
using ReferrerInspectCommand = Valleysoft.Dredge.Commands.Referrer.InspectCommand;
using ReferrerListCommand = Valleysoft.Dredge.Commands.Referrer.ListCommand;
using RepoListCommand = Valleysoft.Dredge.Commands.Repo.ListCommand;
using TagListCommand = Valleysoft.Dredge.Commands.Tag.ListCommand;

namespace Valleysoft.Dredge.Tests;

internal sealed class RegistryIntegrationScenarios
{
    private readonly RegistryFixture fixture;

    public RegistryIntegrationScenarios(RegistryFixture fixture)
    {
        this.fixture = fixture;
    }

    public async Task ManifestCommands_QueryLiveRegistry()
    {
        string repository = fixture.GetRepositoryName(nameof(ManifestCommands_QueryLiveRegistry));
        ImageSeed seed = await fixture.PutImageAsync(
            repository,
            "latest",
            []);
        string image = $"{fixture.Registry}/{repository}:latest";

        using StringWriter digestOutput = new();
        int digestExitCode = await InvokeAsync(
            new DigestCommand(fixture.CreateClientFactory(), digestOutput),
            image);

        using StringWriter manifestOutput = new();
        int getExitCode = await InvokeAsync(
            new GetCommand(fixture.CreateClientFactory(), manifestOutput),
            image);

        Assert.Equal(0, digestExitCode);
        Assert.Equal(seed.Manifest.Digest, digestOutput.ToString().Trim());
        Assert.Equal(0, getExitCode);
        JObject manifestJson = JObject.Parse(manifestOutput.ToString());
        Assert.Equal(2, manifestJson["schemaVersion"]);
        Assert.Equal(seed.Config.Digest, (string?)manifestJson["config"]?["digest"]);
    }

    public async Task ManifestCommand_WhenTagDoesNotExistReturnsFailure()
    {
        string repository = fixture.GetRepositoryName(
            nameof(ManifestCommand_WhenTagDoesNotExistReturnsFailure));
        using StringWriter output = new();
        using StringWriter error = new();
        RecordingProcessTerminator terminator = new();
        TestLiveDigestCommand command = new(
            fixture.CreateClientFactory(),
            output,
            error);
        ((IProcessTerminationAware)command).ProcessTerminator = terminator;

        await command
            .Parse([$"{fixture.Registry}/{repository}:missing"])
            .InvokeAsync(
                new InvocationConfiguration(),
                TestContext.Current.CancellationToken);

        Assert.Equal(1, terminator.ExitCode);
        Assert.Empty(output.ToString());
        Assert.NotEmpty(error.ToString());
    }

    public async Task ListCommands_QueryLiveRegistry()
    {
        string repository = fixture.GetRepositoryName(nameof(ListCommands_QueryLiveRegistry));
        await fixture.PutImageAsync(repository, "stable", []);
        await fixture.PutImageAsync(repository, "latest", []);

        using StringWriter tagOutput = new();
        int tagExitCode = await InvokeAsync(
            new TagListCommand(fixture.CreateClientFactory(), tagOutput),
            $"{fixture.Registry}/{repository}");

        using StringWriter limitedTagOutput = new();
        int limitedTagExitCode = await InvokeAsync(
            new TagListCommand(fixture.CreateClientFactory(), limitedTagOutput),
            $"{fixture.Registry}/{repository}",
            "--limit",
            "1");

        using StringWriter repoOutput = new();
        int repoExitCode = await InvokeAsync(
            new RepoListCommand(fixture.CreateClientFactory(), repoOutput),
            fixture.Registry);

        Assert.Equal(0, tagExitCode);
        Assert.Equal(["latest", "stable"], JArray.Parse(tagOutput.ToString()).Values<string>());
        Assert.Equal(0, limitedTagExitCode);
        string? limitedTag = Assert.Single(
            JArray.Parse(limitedTagOutput.ToString()).Values<string>());
        Assert.NotNull(limitedTag);
        Assert.Contains(limitedTag, new[] { "latest", "stable" });
        Assert.Equal(0, repoExitCode);
        Assert.Contains(repository, JArray.Parse(repoOutput.ToString()).Values<string>());
    }

    public async Task ResolveCommand_SelectsPlatformFromLiveImageIndex()
    {
        string repository = fixture.GetRepositoryName(
            nameof(ResolveCommand_SelectsPlatformFromLiveImageIndex));
        ImageSeed amd64 = await fixture.PutImageAsync(
            repository,
            "amd64",
            [],
            architecture: "amd64");
        ImageSeed arm64 = await fixture.PutImageAsync(
            repository,
            "arm64",
            [],
            architecture: "arm64");
        const string WindowsVersion = "10.0.20348.2849";
        ImageSeed windows = await fixture.PutImageAsync(
            repository,
            "windows",
            [],
            architecture: "amd64",
            os: "windows");
        await fixture.PutIndexAsync(
            repository,
            "multi",
            new PlatformImageSeed(amd64, "linux", "amd64"),
            new PlatformImageSeed(arm64, "linux", "arm64"),
            new PlatformImageSeed(windows, "windows", "amd64", WindowsVersion));
        using StringWriter output = new();

        int exitCode = await InvokeAsync(
            new ResolveCommand(fixture.CreateClientFactory(), output),
                $"{fixture.Registry}/{repository}:multi",
                "--os",
                "linux",
                "--arch",
                "arm64");

        Assert.Equal(0, exitCode);
        Assert.Equal(
            $"{fixture.Registry}/{repository}@{arm64.Manifest.Digest}",
            output.ToString().Trim());

        string settingsRoot = Path.Combine(
            Path.GetTempPath(),
            $"dredge-resolve-settings-{Guid.NewGuid():N}");
        string settingsPath = Path.Combine(settingsRoot, "settings.json");
        try
        {
            AppSettings settings = AppSettings.Load(settingsPath);
            settings.Platform.Os = "windows";
            settings.Platform.OsVersion = WindowsVersion;
            settings.Platform.Architecture = "amd64";
            settings.Save();
            using StringWriter settingsOutput = new();

            int settingsExitCode = await InvokeAsync(
                new ResolveCommand(
                    fixture.CreateClientFactory(),
                    settingsOutput,
                    new AppSettingsStore(settingsPath)),
                $"{fixture.Registry}/{repository}:multi");

            Assert.Equal(0, settingsExitCode);
            Assert.Equal(
                $"{fixture.Registry}/{repository}@{windows.Manifest.Digest}",
                settingsOutput.ToString().Trim());
        }
        finally
        {
            if (Directory.Exists(settingsRoot))
            {
                Directory.Delete(settingsRoot, recursive: true);
            }
        }
    }

    public async Task ReferrerCommands_QueryAndReadLiveArtifact()
    {
        const string ArtifactType = "application/vnd.example.sbom";
        const string PayloadMediaType = "application/spdx+json";
        const string Payload = """
            {
              "spdxVersion": "SPDX-2.3",
              "name": "integration-sbom",
              "documentNamespace": "https://example.test/integration",
              "creationInfo": {
                "created": "2026-09-04T00:00:00Z",
                "creators": ["Tool: dredge-tests"]
              },
              "packages": [{}, {}],
              "files": [{}],
              "relationships": []
            }
            """;
        string repository = fixture.GetRepositoryName(
            nameof(ReferrerCommands_QueryAndReadLiveArtifact));
        ImageSeed subject = await fixture.PutImageAsync(repository, "subject", []);
        ArtifactSeed artifact = await fixture.PutArtifactAsync(
            subject,
            "sbom",
            ArtifactType,
            PayloadMediaType,
            Encoding.UTF8.GetBytes(Payload));
        string image = $"{fixture.Registry}/{repository}:subject";
        string digest = artifact.Manifest.Digest;
        IDockerRegistryClientFactory factory = fixture.CreateClientFactory();

        using StringWriter listOutput = new();
        int listExitCode = await InvokeAsync(
            new ReferrerListCommand(factory, listOutput),
            image,
            "--artifact-type",
            ArtifactType);

        using StringWriter checkOutput = new();
        int checkExitCode = await InvokeAsync(
            new ReferrerCheckCommand(factory, checkOutput),
            image,
            "--artifact-type",
            ArtifactType,
            "--output",
            "json");

        using MemoryStream payloadOutput = new();
        int getExitCode = await InvokeAsync(
            new ReferrerGetCommand(factory, payloadOutput),
            image,
            digest);

        using StringWriter inspectOutput = new();
        int inspectExitCode = await InvokeAsync(
            new ReferrerInspectCommand(factory, inspectOutput),
            image,
            digest,
            "--output",
            "json");

        Assert.Equal(0, listExitCode);
        JObject list = JObject.Parse(listOutput.ToString());
        Assert.Contains(
            list["manifests"]!,
            manifest =>
                (string?)manifest["digest"] == digest &&
                (string?)manifest["artifactType"] == ArtifactType);
        Assert.Equal(0, checkExitCode);
        JObject check = JObject.Parse(checkOutput.ToString());
        Assert.True((bool)check["succeeded"]!);
        Assert.Equal(0, getExitCode);
        Assert.Equal(Payload, Encoding.UTF8.GetString(payloadOutput.ToArray()));
        Assert.Equal(0, inspectExitCode);
        JObject inspection = JObject.Parse(inspectOutput.ToString());
        Assert.Equal(digest, (string?)inspection["artifactDigest"]);
        Assert.Equal("SPDX", (string?)inspection["payloads"]?[0]?["format"]);
        Assert.Equal(2, (int?)inspection["payloads"]?[0]?["summary"]?["packageCount"]);
    }

    private static Task<int> InvokeAsync(Command command, params string[] args)
    {
        ((IProcessTerminationAware)command).ProcessTerminator = new TestProcessTerminator();
        return command
            .Parse(args)
            .InvokeAsync(
                new InvocationConfiguration(),
                TestContext.Current.CancellationToken);
    }

}

internal sealed class TestLiveDigestCommand : DigestCommand
{
    private readonly TextWriter error;

    public TestLiveDigestCommand(
        IDockerRegistryClientFactory dockerRegistryClientFactory,
        TextWriter output,
        TextWriter error)
        : base(dockerRegistryClientFactory, output)
    {
        this.error = error;
    }

    protected override TextWriter Error => error;
}

[Trait("Category", "Integration")]
public sealed class ManifestCommandIntegrationTests
{
    private readonly RegistryFixture fixture;

    public ManifestCommandIntegrationTests(RegistryFixture fixture)
    {
        this.fixture = fixture;
    }

    [Trait("Category", "Integration")]
    public sealed class ManifestCommandFailureIntegrationTests
    {
        private readonly RegistryFixture fixture;

        public ManifestCommandFailureIntegrationTests(RegistryFixture fixture)
        {
            this.fixture = fixture;
        }

        [Fact]
        public async Task ManifestCommand_WhenTagDoesNotExistReturnsFailure()
        {
            await fixture.EnsureInitializedAsync();
            await new RegistryIntegrationScenarios(fixture)
                .ManifestCommand_WhenTagDoesNotExistReturnsFailure();
        }
    }

    [Fact]
    public async Task ManifestCommands_QueryLiveRegistry()
    {
        await fixture.EnsureInitializedAsync();
        await new RegistryIntegrationScenarios(fixture).ManifestCommands_QueryLiveRegistry();
    }
}

[Trait("Category", "Integration")]
public sealed class RegistryListCommandIntegrationTests
{
    private readonly RegistryFixture fixture;

    public RegistryListCommandIntegrationTests(RegistryFixture fixture)
    {
        this.fixture = fixture;
    }

    [Fact]
    public async Task ListCommands_QueryLiveRegistry()
    {
        await fixture.EnsureInitializedAsync();
        await new RegistryIntegrationScenarios(fixture).ListCommands_QueryLiveRegistry();
    }
}

[Trait("Category", "Integration")]
public sealed class ManifestResolveCommandIntegrationTests
{
    private readonly RegistryFixture fixture;

    public ManifestResolveCommandIntegrationTests(RegistryFixture fixture)
    {
        this.fixture = fixture;
    }

    [Fact]
    public async Task ResolveCommand_SelectsPlatformFromLiveImageIndex()
    {
        await fixture.EnsureInitializedAsync();
        await new RegistryIntegrationScenarios(fixture)
            .ResolveCommand_SelectsPlatformFromLiveImageIndex();
    }
}

[Trait("Category", "Integration")]
public sealed class ReferrerCommandIntegrationTests
{
    private readonly ZotRegistryFixture fixture;

    public ReferrerCommandIntegrationTests(ZotRegistryFixture fixture)
    {
        this.fixture = fixture;
    }

    [Fact]
    public async Task ReferrerCommands_QueryAndReadLiveArtifact()
    {
        await fixture.EnsureInitializedAsync();
        await new RegistryIntegrationScenarios(fixture).ReferrerCommands_QueryAndReadLiveArtifact();
    }
}
