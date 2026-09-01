namespace Valleysoft.Dredge.Tests;

using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Spectre.Console;
using Spectre.Console.Rendering;
using System.CommandLine;
using System.Text;
using Valleysoft.DockerRegistryClient.Models.Images;
using Valleysoft.DockerRegistryClient.Models.Manifests;
using Valleysoft.DockerRegistryClient.Models.Manifests.Oci;
using Valleysoft.Dredge.Commands.Image;
using ImageData = Valleysoft.DockerRegistryClient.Models.Images.Image;

public class CompareMetadataCommandTests
{
    private const string Registry = "test-registry.io";
    private const string ManifestMediaType = "application/vnd.oci.image.manifest.v1+json";
    private const string IndexMediaType = "application/vnd.oci.image.index.v1+json";
    private const string ConfigMediaType = "application/vnd.oci.image.config.v1+json";
    private const string LayerMediaType = "application/vnd.oci.image.layer.v1.tar+gzip";
    private static readonly ImageName baseImageName = ImageName.Parse($"{Registry}/base:latest");
    private static readonly ImageName targetImageName = ImageName.Parse($"{Registry}/target:latest");

    [Fact]
    public async Task EqualMetadataIgnoresMapAndSetOrdering()
    {
        ImageData baseConfig = CreateImageConfig(
            environmentVariables: ["B=2", "A=1"],
            labels: new Dictionary<string, string> { ["second"] = "2", ["first"] = "1" },
            osFeatures: ["feature-b", "feature-a"]);
        ImageData targetConfig = CreateImageConfig(
            environmentVariables: ["A=1", "B=2"],
            labels: new Dictionary<string, string> { ["first"] = "1", ["second"] = "2" },
            osFeatures: ["feature-a", "feature-b"]);
        ImageSetup setup = CreateSingleManifestSetup(baseConfig);
        ImageSetup targetSetup = CreateSingleManifestSetup(targetConfig);
        CompareMetadataCommand command = CreateCommand(setup, targetSetup);

        CompareMetadataResult result = await command.GetResultAsync(TestContext.Current.CancellationToken);

        Assert.True(result.Summary.AreEqual);
        Assert.All(result.Comparisons, comparison => Assert.Equal(CompareDiff.Equal, comparison.Diff));
    }

    [Fact]
    public async Task ReportsAddedRemovedAndChangedMetadata()
    {
        ImageData baseConfig = CreateImageConfig(
            environmentVariables: ["A=1"],
            labels: new Dictionary<string, string> { ["removed"] = "value" });
        ImageData targetConfig = CreateImageConfig(
            environmentVariables: ["A=2", "ADDED=value"],
            labels: new Dictionary<string, string>());
        ImageSetup baseSetup = CreateSingleManifestSetup(baseConfig);
        ImageSetup targetSetup = CreateSingleManifestSetup(
            targetConfig,
            layerDigest: "sha256:changed-layer",
            manifestAnnotations: new Dictionary<string, string> { ["org.example.annotation"] = "new" });
        CompareMetadataCommand command = CreateCommand(baseSetup, targetSetup);

        CompareMetadataResult result = await command.GetResultAsync(TestContext.Current.CancellationToken);

        Assert.False(result.Summary.AreEqual);
        AssertComparison(result, "Config", "environment[\"A\"]", CompareDiff.NotEqual);
        AssertComparison(result, "Config", "environment[\"ADDED\"]", CompareDiff.Added);
        AssertComparison(result, "Config", "labels[\"removed\"]", CompareDiff.Removed);
        AssertComparison(result, "ResolvedManifest", "layers[0].digest", CompareDiff.NotEqual);
        AssertComparison(result, "Manifest", "annotations[\"org.example.annotation\"]", CompareDiff.Added);
    }

    [Fact]
    public async Task HandlesNullOptionalConfigurationFields()
    {
        const string NullConfig = """
            {
              "architecture": "amd64",
              "os": "linux",
              "os.features": null,
              "config": {
                "Env": null,
                "Entrypoint": null,
                "Cmd": null,
                "Labels": null,
                "ExposedPorts": null,
                "Volumes": null
              },
              "rootfs": {
                "type": "layers",
                "diff_ids": []
              },
              "history": null
            }
            """;
        const string OmittedConfig = """
            {
              "architecture": "amd64",
              "os": "linux",
              "config": {},
              "rootfs": {
                "type": "layers",
                "diff_ids": []
              }
            }
            """;
        CompareMetadataCommand command = CreateCommand(
            CreateSingleManifestSetup(NullConfig),
            CreateSingleManifestSetup(OmittedConfig));

        CompareMetadataResult result = await command.GetResultAsync(TestContext.Current.CancellationToken);

        Assert.True(result.Summary.AreEqual);
    }

    [Fact]
    public async Task HandlesNullOptionalManifestCollections()
    {
        OciImageManifest resolvedManifest = CreateManifest();
        resolvedManifest.Annotations = null!;
        resolvedManifest.Config.Annotations = null!;
        resolvedManifest.Config.Urls = null!;
        resolvedManifest.Layers[0].Annotations = null!;
        resolvedManifest.Layers[0].Urls = null!;
        Valleysoft.DockerRegistryClient.Models.Manifests.Oci.ManifestReference reference =
            CreateReference("linux", "amd64", "sha256:linux");
        reference.Annotations = null!;
        reference.Urls = null!;
        reference.Platform!.OsFeatures = null!;
        reference.Platform.Features = null!;
        ImageSetup setup = CreateIndexSetup(
            CreateImageConfig(),
            resolvedManifest,
            [reference],
            "sha256:index");
        ((OciImageIndex)setup.InitialManifest.Manifest).Annotations = null!;
        CompareMetadataCommand command = CreateCommand(setup, setup);
        command.Options.Os = "linux";
        command.Options.Architecture = "amd64";

        CompareMetadataResult result = await command.GetResultAsync(TestContext.Current.CancellationToken);

        Assert.True(result.Summary.AreEqual);
    }

    [Fact]
    public async Task ComparesConfigurationFieldsNotExposedByTheRegistryClientModel()
    {
        const string BaseConfig = """
            {
              "architecture": "amd64",
              "os": "linux",
              "config": {
                "Healthcheck": {
                  "Test": ["CMD", "base"]
                }
              },
              "rootfs": {
                "type": "layers",
                "diff_ids": []
              }
            }
            """;
        const string TargetConfig = """
            {
              "architecture": "amd64",
              "os": "linux",
              "config": {
                "Healthcheck": {
                  "Test": ["CMD", "target"]
                }
              },
              "rootfs": {
                "type": "layers",
                "diff_ids": []
              }
            }
            """;
        CompareMetadataCommand command = CreateCommand(
            CreateSingleManifestSetup(BaseConfig),
            CreateSingleManifestSetup(TargetConfig));

        CompareMetadataResult result = await command.GetResultAsync(TestContext.Current.CancellationToken);

        AssertComparison(result, "Config", "healthcheck[\"Test\"][1]", CompareDiff.NotEqual);
    }

    [Fact]
    public async Task PreservesEmptyAnnotationAndLabelValues()
    {
        ImageData baseConfig = CreateImageConfig(labels: new Dictionary<string, string>());
        ImageData targetConfig = CreateImageConfig(
            labels: new Dictionary<string, string> { ["org.example.empty"] = string.Empty });
        CompareMetadataCommand command = CreateCommand(
            CreateSingleManifestSetup(baseConfig),
            CreateSingleManifestSetup(
                targetConfig,
                manifestAnnotations: new Dictionary<string, string>
                {
                    ["org.example.empty"] = string.Empty
                }));

        CompareMetadataResult result = await command.GetResultAsync(TestContext.Current.CancellationToken);

        AssertComparison(result, "Config", "labels[\"org.example.empty\"]", CompareDiff.Added);
        AssertComparison(result, "Manifest", "annotations[\"org.example.empty\"]", CompareDiff.Added);
    }

    [Fact]
    public async Task PreservesDateLikeConfigurationStrings()
    {
        const string BaseTimestamp = "2024-01-15T10:30:00+01:00";
        const string TargetTimestamp = "2024-01-15T09:30:00Z";
        CompareMetadataCommand command = CreateCommand(
            CreateSingleManifestSetup(CreateImageConfigJson(BaseTimestamp)),
            CreateSingleManifestSetup(CreateImageConfigJson(TargetTimestamp)));

        CompareMetadataResult result = await command.GetResultAsync(TestContext.Current.CancellationToken);

        MetadataComparison comparison = FindComparison(
            result,
            "Config",
            "labels[\"org.opencontainers.image.created\"]");
        Assert.Equal(CompareDiff.NotEqual, comparison.Diff);
        Assert.Equal(JTokenType.String, comparison.BaseValue!.Type);
        Assert.Equal(BaseTimestamp, comparison.BaseValue.Value<string>());
        Assert.Equal(TargetTimestamp, comparison.TargetValue!.Value<string>());
    }

    [Fact]
    public async Task TreatsCommandOrderAsSignificant()
    {
        ImageData baseConfig = CreateImageConfig();
        ImageData targetConfig = CreateImageConfig();
        baseConfig.Config!.CommandArgs = ["first", "second"];
        targetConfig.Config!.CommandArgs = ["second", "first"];
        CompareMetadataCommand command = CreateCommand(
            CreateSingleManifestSetup(baseConfig),
            CreateSingleManifestSetup(targetConfig));

        CompareMetadataResult result = await command.GetResultAsync(TestContext.Current.CancellationToken);

        AssertComparison(result, "Config", "command[0]", CompareDiff.NotEqual);
        AssertComparison(result, "Config", "command[1]", CompareDiff.NotEqual);
    }

    [Fact]
    public async Task ComparesFullPlatformIndexWhileResolvingSelectedPlatform()
    {
        ImageData config = CreateImageConfig();
        OciImageManifest selectedManifest = CreateManifest();
        ImageSetup baseSetup = CreateIndexSetup(
            config,
            selectedManifest,
            [
                CreateReference("linux", "amd64", "sha256:linux"),
                CreateReference("windows", "amd64", "sha256:windows-base", "10.0")
            ],
            "sha256:index-base");
        ImageSetup targetSetup = CreateIndexSetup(
            config,
            selectedManifest,
            [
                CreateReference("linux", "amd64", "sha256:linux"),
                CreateReference("windows", "amd64", "sha256:windows-target", "10.0"),
                CreateReference("linux", "arm64", "sha256:arm64")
            ],
            "sha256:index-target");
        CompareMetadataCommand command = CreateCommand(baseSetup, targetSetup);
        command.Options.Os = "linux";
        command.Options.Architecture = "amd64";

        CompareMetadataResult result = await command.GetResultAsync(TestContext.Current.CancellationToken);

        AssertComparison(
            result,
            "Platforms",
            "available[\"windows/amd64/10.0\"].digest",
            CompareDiff.NotEqual);
        AssertComparison(
            result,
            "Platforms",
            "available[\"linux/arm64\"].digest",
            CompareDiff.Added);
        Assert.Equal(
            CompareDiff.Equal,
            FindComparison(result, "ResolvedManifest", "config.digest").Diff);
    }

    [Theory]
    [InlineData(CompareOutput.Inline, typeof(Rows))]
    [InlineData(CompareOutput.SideBySide, typeof(Table))]
    [InlineData(CompareOutput.Json, typeof(Text))]
    public async Task SupportsOutputFormats(CompareOutput output, Type expectedType)
    {
        ImageSetup baseSetup = CreateSingleManifestSetup(CreateImageConfig(environmentVariables: ["A=1"]));
        ImageSetup targetSetup = CreateSingleManifestSetup(CreateImageConfig(environmentVariables: ["A=2"]));
        CompareMetadataCommand command = CreateCommand(baseSetup, targetSetup, output);

        IRenderable rendered = await command.GetOutputAsync(TestContext.Current.CancellationToken);

        Assert.IsType(expectedType, rendered);
        string text = TestHelper.GetString(rendered.GetSegments(AnsiConsole.Console));
        Assert.Contains(output == CompareOutput.Json ? "\"areEqual\": false" : "Config.environment[\"A\"]", text);
    }

    [Fact]
    public async Task SideBySideUsesComparisonColumnWhenColorIsUnavailable()
    {
        ImageSetup setup = CreateSingleManifestSetup(CreateImageConfig());
        CompareMetadataCommand command = CreateCommand(
            setup,
            setup,
            ansiSupported: false);

        Table output = (Table)await command.GetOutputAsync(TestContext.Current.CancellationToken);

        Assert.Equal(4, output.Columns.Count);
        Assert.Equal("Compare", TestHelper.GetString(output.Columns[2].Header.GetSegments(AnsiConsole.Console)));
    }

    [Fact]
    public async Task CommandInvocationReturnsSuccessWhenDifferencesAreFound()
    {
        CompareMetadataCommand command = CreateCommand(
            CreateSingleManifestSetup(CreateImageConfig(environmentVariables: ["A=1"])),
            CreateSingleManifestSetup(CreateImageConfig(environmentVariables: ["A=2"])));

        int exitCode = await command
            .Parse([baseImageName.ToString(), targetImageName.ToString(), "--output", "Json"])
            .InvokeAsync(new InvocationConfiguration(), TestContext.Current.CancellationToken);

        Assert.Equal(0, exitCode);
    }

    [Fact]
    public async Task CommandInvocationWritesParseableJsonWithoutWrapping()
    {
        string longValue = new('x', 200);
        StringWriter output = new();
        CompareMetadataCommand command = CreateCommand(
            CreateSingleManifestSetup(CreateImageConfig(labels:
                new Dictionary<string, string> { ["long"] = longValue })),
            CreateSingleManifestSetup(CreateImageConfig()),
            CompareOutput.Json,
            outputWriter: output);

        int exitCode = await command
            .Parse([baseImageName.ToString(), targetImageName.ToString(), "--output", "Json"])
            .InvokeAsync(new InvocationConfiguration(), TestContext.Current.CancellationToken);

        Assert.Equal(0, exitCode);
        CompareMetadataResult? result =
            JsonConvert.DeserializeObject<CompareMetadataResult>(output.ToString());
        Assert.NotNull(result);
        Assert.Contains(
            result.Comparisons,
            comparison => comparison.BaseValue?.Value<string>() == longValue);
    }

    [Fact]
    public void IsRegisteredUnderImageCompare()
    {
        Mock<IDockerRegistryClientFactory> factory = new();
        CompareCommand compareCommand = new(factory.Object);

        Command metadataCommand = Assert.Single(
            compareCommand.Subcommands,
            command => command.Name == "metadata");

        Assert.Contains(metadataCommand.Options, option => option.Name == "--output");
        Assert.Contains(metadataCommand.Options, option => option.Name == "--no-color");
        Assert.Contains(metadataCommand.Options, option => option.Name == "--os");
        Assert.Contains(metadataCommand.Options, option => option.Name == "--arch");
        Assert.Contains(metadataCommand.Options, option => option.Name == "--os-version");
    }

    private static void AssertComparison(
        CompareMetadataResult result,
        string category,
        string path,
        CompareDiff expectedDiff) =>
        Assert.Equal(expectedDiff, FindComparison(result, category, path).Diff);

    private static MetadataComparison FindComparison(
        CompareMetadataResult result,
        string category,
        string path) =>
        Assert.Single(
            result.Comparisons,
            comparison => comparison.Category == category && comparison.Path == path);

    private static CompareMetadataCommand CreateCommand(
        ImageSetup baseSetup,
        ImageSetup targetSetup,
        CompareOutput output = CompareOutput.SideBySide,
        bool ansiSupported = true,
        StringWriter? outputWriter = null)
    {
        Mock<IDockerRegistryClient> client = new();
        SetupImage(client, baseImageName, baseSetup);
        SetupImage(client, targetImageName, targetSetup);

        Mock<IDockerRegistryClientFactory> factory = new();
        factory
            .Setup(clientFactory => clientFactory.GetClientAsync(Registry))
            .ReturnsAsync(client.Object);

        CompareMetadataCommand command = new(
            factory.Object,
            CreateConsole(ansiSupported, outputWriter),
            () => new PlatformSettings());
        command.Options.BaseImage = baseImageName.ToString();
        command.Options.TargetImage = targetImageName.ToString();
        command.Options.OutputFormat = output;
        return command;
    }

    private static IAnsiConsole CreateConsole(bool ansiSupported = true, StringWriter? output = null) =>
        AnsiConsole.Create(new AnsiConsoleSettings
        {
            Ansi = ansiSupported ? AnsiSupport.Yes : AnsiSupport.No,
            ColorSystem = ansiSupported ? ColorSystemSupport.EightBit : ColorSystemSupport.NoColors,
            Out = new AnsiConsoleOutput(output ?? new StringWriter()),
            Enrichment = new ProfileEnrichment
            {
                UseDefaultEnrichers = false
            }
        });

    private static string CreateImageConfigJson(string created) =>
        $$"""
          {
            "architecture": "amd64",
            "os": "linux",
            "config": {
              "Labels": {
                "org.opencontainers.image.created": "{{created}}"
              }
            },
            "rootfs": {
              "type": "layers",
              "diff_ids": []
            }
          }
          """;

    private static void SetupImage(
        Mock<IDockerRegistryClient> client,
        ImageName imageName,
        ImageSetup setup)
    {
        client
            .Setup(registryClient => registryClient.Manifests.GetAsync(
                imageName.Repo,
                imageName.Tag!,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(setup.InitialManifest);

        if (setup.ResolvedManifest is not null)
        {
            string resolvedDigest = ((IManifestList)setup.InitialManifest.Manifest).Manifests
                .Single(reference => reference.Platform?.Os == "linux" && reference.Platform.Architecture == "amd64")
                .Digest;
            client
                .Setup(registryClient => registryClient.Manifests.GetAsync(
                    imageName.Repo,
                    resolvedDigest,
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(setup.ResolvedManifest);
        }

        string configDigest = (setup.ResolvedManifest?.Manifest as IImageManifest ??
            (IImageManifest)setup.InitialManifest.Manifest).Config!.Digest;
        client
            .Setup(registryClient => registryClient.Blobs.GetAsync(
                imageName.Repo,
                configDigest,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MemoryStream(Encoding.UTF8.GetBytes(setup.Config)));
    }

    private static ImageSetup CreateSingleManifestSetup(
        ImageData config,
        string layerDigest = "sha256:layer",
        IDictionary<string, string>? manifestAnnotations = null)
        => CreateSingleManifestSetup(
            System.Text.Json.JsonSerializer.Serialize(config),
            layerDigest,
            manifestAnnotations);

    private static ImageSetup CreateSingleManifestSetup(
        string config,
        string layerDigest = "sha256:layer",
        IDictionary<string, string>? manifestAnnotations = null)
    {
        OciImageManifest manifest = CreateManifest(layerDigest, manifestAnnotations);
        return new ImageSetup(
            new ManifestInfo(ManifestMediaType, "sha256:manifest", manifest),
            null,
            config);
    }

    private static ImageSetup CreateIndexSetup(
        ImageData config,
        OciImageManifest resolvedManifest,
        Valleysoft.DockerRegistryClient.Models.Manifests.Oci.ManifestReference[] references,
        string indexDigest)
    {
        OciImageIndex index = new()
        {
            SchemaVersion = 2,
            Manifests = references,
            Annotations = new Dictionary<string, string> { ["org.example.index"] = "value" }
        };

        return new ImageSetup(
            new ManifestInfo(IndexMediaType, indexDigest, index),
            new ManifestInfo(ManifestMediaType, "sha256:linux", resolvedManifest),
            System.Text.Json.JsonSerializer.Serialize(config));
    }

    private static OciImageManifest CreateManifest(
        string layerDigest = "sha256:layer",
        IDictionary<string, string>? annotations = null) =>
        new()
        {
            Config = new OciDescriptor
            {
                MediaType = ConfigMediaType,
                Digest = "sha256:config",
                Size = 100
            },
            Layers =
            [
                new OciDescriptor
                {
                    MediaType = LayerMediaType,
                    Digest = layerDigest,
                    Size = 200,
                    Annotations = new Dictionary<string, string>
                    {
                        ["org.example.layer"] = "value"
                    }
                }
            ],
            Annotations = annotations ?? new Dictionary<string, string>()
        };

    private static Valleysoft.DockerRegistryClient.Models.Manifests.Oci.ManifestReference CreateReference(
        string os,
        string architecture,
        string digest,
        string? osVersion = null) =>
        new()
        {
            MediaType = ManifestMediaType,
            Digest = digest,
            Size = 300,
            Platform = new ManifestPlatform
            {
                Os = os,
                Architecture = architecture,
                OsVersion = osVersion
            }
        };

    private static ImageData CreateImageConfig(
        string[]? environmentVariables = null,
        IDictionary<string, string>? labels = null,
        string[]? osFeatures = null) =>
        new()
        {
            Created = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc),
            Author = "Dredge",
            Architecture = "amd64",
            Os = "linux",
            OsFeatures = osFeatures ?? [],
            Config = new ImageConfig
            {
                User = "1000",
                WorkingDir = "/app",
                EntrypointArgs = ["/app/start"],
                CommandArgs = ["--serve"],
                EnvironmentVariables = environmentVariables ?? [],
                Labels = labels ?? new Dictionary<string, string>(),
                ExposedPorts = new Dictionary<string, object> { ["8080/tcp"] = new() },
                Volumes = new Dictionary<string, object> { ["/data"] = new() }
            },
            RootFilesystem = new RootFilesystem
            {
                Type = "layers",
                DiffIds = ["sha256:diff"]
            },
            History =
            [
                new LayerHistory
                {
                    Created = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc),
                    CreatedBy = "COPY . /app",
                    Author = "Dredge",
                    Comment = "build",
                    IsEmptyLayer = false
                }
            ]
        };

    private sealed record ImageSetup(
        ManifestInfo InitialManifest,
        ManifestInfo? ResolvedManifest,
        string Config);
}
