using Spectre.Console;
using System.CommandLine;
using System.Text;
using System.Text.Json.Nodes;
using Valleysoft.Dredge.Commands.Image;

namespace Valleysoft.Dredge.Tests;

internal sealed class ImageCommandIntegrationScenarios
{
    private readonly RegistryFixture fixture;

    public ImageCommandIntegrationScenarios(RegistryFixture fixture)
    {
        this.fixture = fixture;
    }

    public async Task FileSystemCommands_ReadAndExtractLiveImageLayers()
    {
        ImageSeed image = await SeedFileSystemImageAsync(
            nameof(FileSystemCommands_ReadAndExtractLiveImageLayers));
        string imageName = $"{fixture.Registry}/{image.Repository}:{image.Reference}";
        IDockerRegistryClientFactory factory = fixture.CreateClientFactory();
        string tempRoot = GetTempPath();
        IDredgePathProvider pathProvider = new TestDredgePathProvider(tempRoot);

        try
        {
            using MemoryStream catOutput = new();
            int catExitCode = await InvokeAsync(
                new CatCommand(factory, catOutput),
                imageName,
                "app/value");

            StringWriter listOutput = new();
            int listExitCode = await InvokeAsync(
                new LsCommand(factory, CreateConsole(listOutput)),
                imageName,
                "--recursive",
                "--show-deleted",
                "--output",
                "json");

            string extractPath = Path.Combine(tempRoot, "extract");
            string savePath = Path.Combine(tempRoot, "save");
            string firstLayerPath = Path.Combine(tempRoot, "first-layer");
            string separateLayersPath = Path.Combine(tempRoot, "separate-layers");
            string selectedSeparateLayerPath = Path.Combine(tempRoot, "selected-separate-layer");
            int extractExitCode = await InvokeAsync(
                new ExtractCommand(factory),
                imageName,
                "app",
                extractPath);
            int saveExitCode = await InvokeAsync(
                new SaveLayersCommand(factory, pathProvider),
                imageName,
                savePath);
            int firstLayerExitCode = await InvokeAsync(
                new SaveLayersCommand(factory, pathProvider),
                imageName,
                firstLayerPath,
                "--layer-index",
                "0");
            int separateLayersExitCode = await InvokeAsync(
                new SaveLayersCommand(factory, pathProvider),
                imageName,
                separateLayersPath,
                "--no-squash");
            int selectedSeparateLayerExitCode = await InvokeAsync(
                new SaveLayersCommand(factory, pathProvider),
                imageName,
                selectedSeparateLayerPath,
                "--no-squash",
                "--layer-index",
                "1");

            Assert.Equal(0, extractExitCode);
            Assert.Equal("new", await File.ReadAllTextAsync(
                Path.Combine(extractPath, "value"),
                TestContext.Current.CancellationToken));
            Assert.False(File.Exists(Path.Combine(extractPath, "removed")));
            Assert.Equal(0, saveExitCode);
            Assert.Equal("new", await File.ReadAllTextAsync(
                Path.Combine(savePath, "app", "value"),
                TestContext.Current.CancellationToken));
            Assert.False(File.Exists(Path.Combine(savePath, "app", "removed")));
            Assert.Equal(0, firstLayerExitCode);
            Assert.Equal("old", await File.ReadAllTextAsync(
                Path.Combine(firstLayerPath, "app", "value"),
                TestContext.Current.CancellationToken));
            Assert.Equal("remove me", await File.ReadAllTextAsync(
                Path.Combine(firstLayerPath, "app", "removed"),
                TestContext.Current.CancellationToken));
            Assert.Equal(0, separateLayersExitCode);
            string[] layerDirectories = Directory.GetDirectories(separateLayersPath);
            Assert.Equal(2, layerDirectories.Length);
            string firstLayerDirectory = Assert.Single(
                layerDirectories,
                path => Path.GetFileName(path).StartsWith("layer0-", StringComparison.Ordinal));
            string secondLayerDirectory = Assert.Single(
                layerDirectories,
                path => Path.GetFileName(path).StartsWith("layer1-", StringComparison.Ordinal));
            Assert.Equal("old", await File.ReadAllTextAsync(
                Path.Combine(firstLayerDirectory, "app", "value"),
                TestContext.Current.CancellationToken));
            Assert.Equal("new", await File.ReadAllTextAsync(
                Path.Combine(secondLayerDirectory, "app", "value"),
                TestContext.Current.CancellationToken));
            Assert.True(File.Exists(Path.Combine(secondLayerDirectory, "app", ".wh.removed")));
            Assert.Equal(0, selectedSeparateLayerExitCode);
            string selectedLayerDirectory = Assert.Single(
                Directory.GetDirectories(selectedSeparateLayerPath));
            Assert.StartsWith(
                "layer1-",
                Path.GetFileName(selectedLayerDirectory),
                StringComparison.Ordinal);
            Assert.Equal("new", await File.ReadAllTextAsync(
                Path.Combine(selectedLayerDirectory, "app", "value"),
                TestContext.Current.CancellationToken));
            Assert.True(File.Exists(Path.Combine(selectedLayerDirectory, "app", ".wh.removed")));

            Assert.Equal(0, catExitCode);
            Assert.Equal("new", Encoding.UTF8.GetString(catOutput.ToArray()));
            Assert.Equal(0, listExitCode);
            JsonArray entries = JsonNode.Parse(listOutput.ToString())!.AsArray();
            Assert.Contains(entries, entry =>
                entry?["path"]?.GetValue<string>() == "app/value" &&
                entry["modifiedLayer"]?["index"]?.GetValue<int>() == 1);
            Assert.Contains(entries, entry =>
                entry?["path"]?.GetValue<string>() == "app/removed" &&
                entry["deletedLayer"]?["index"]?.GetValue<int>() == 1);
        }
        finally
        {
            DeleteDirectory(tempRoot);
        }
    }

    public async Task InspectionCommands_ReadLiveImageMetadata()
    {
        string repository = fixture.GetRepositoryName(
            nameof(InspectionCommands_ReadLiveImageMetadata));
        LayerSeed layer = await fixture.UploadLayerAsync(
            repository,
            LayerEntry.File(
                "etc/os-release",
                """
                NAME="Integration Linux"
                VERSION="1.0"
                ID=integration
                PRETTY_NAME="Integration Linux 1.0"
                """));
        object[] history =
        [
            new { created_by = "/bin/sh -c #(nop) ADD file:abc in /", empty_layer = false },
            new { created_by = "/bin/sh -c echo hello", empty_layer = true }
        ];
        ImageSeed image = await fixture.PutImageAsync(
            repository,
            "latest",
            [layer],
            history: history);
        string imageName = $"{fixture.Registry}/{repository}:latest";
        IDockerRegistryClientFactory factory = fixture.CreateClientFactory();

        using StringWriter inspectOutput = new();
        int inspectExitCode = await InvokeAsync(
            new InspectCommand(factory, inspectOutput),
            imageName);

        using StringWriter osOutput = new();
        int osExitCode = await InvokeAsync(
            new OsCommand(factory, osOutput),
            imageName);

        using StringWriter dockerfileOutput = new();
        int dockerfileExitCode = await InvokeAsync(
            new DockerfileCommand(factory, CreateConsole(dockerfileOutput)),
            imageName,
            "--no-color");
        string dockerfile = dockerfileOutput.ToString();

        Assert.Equal(0, inspectExitCode);
        JsonObject config = JsonNode.Parse(inspectOutput.ToString())!.AsObject();
        Assert.Equal("amd64", config["architecture"]?.GetValue<string>());
        Assert.Equal("linux", config["os"]?.GetValue<string>());
        Assert.Equal(layer.DiffId, config["rootfs"]?["diff_ids"]?[0]?.GetValue<string>());
        Assert.Equal(0, osExitCode);
        JsonObject os = JsonNode.Parse(osOutput.ToString())!.AsObject();
        Assert.Equal("Integration Linux", os["NAME"]?.GetValue<string>());
        Assert.Equal("1.0", os["VERSION"]?.GetValue<string>());
        Assert.Equal(0, dockerfileExitCode);
        Assert.Contains("FROM scratch", dockerfile);
        Assert.Contains("ADD file:abc /", dockerfile);
        Assert.Contains("RUN echo hello", dockerfile);
    }

    public async Task CompareCommands_UseLiveLayerAndConfigurationData()
    {
        string repository = fixture.GetRepositoryName(
            nameof(CompareCommands_UseLiveLayerAndConfigurationData));
        LayerSeed common = await fixture.UploadLayerAsync(
            repository,
            LayerEntry.File("common", "same"));
        LayerSeed changed = await fixture.UploadLayerAsync(
            repository,
            LayerEntry.File("changed", "target"));
        ImageSeed baseImage = await fixture.PutImageAsync(
            repository,
            "base",
            [common],
            config: new
            {
                Env = new[] { "VALUE=base" },
                Labels = new Dictionary<string, string> { ["shared"] = "yes" }
            });
        ImageSeed targetImage = await fixture.PutImageAsync(
            repository,
            "target",
            [common, changed],
            config: new
            {
                Env = new[] { "VALUE=target", "ADDED=true" },
                Labels = new Dictionary<string, string> { ["shared"] = "yes" }
            });
        string baseName = $"{fixture.Registry}/{repository}:{baseImage.Reference}";
        string targetName = $"{fixture.Registry}/{repository}:{targetImage.Reference}";
        IDockerRegistryClientFactory factory = fixture.CreateClientFactory();

        StringWriter layerOutput = new();
        CompareLayersCommand layerCommand = new(factory, CreateConsole(layerOutput));
        RecordingProcessTerminator layerProcessTerminator = new();
        ((IProcessTerminationAware)layerCommand).ProcessTerminator = layerProcessTerminator;
        await layerCommand
            .Parse([
                baseName,
                targetName,
                "--output",
                "json",
                "--history",
                "--compressed-size"])
            .InvokeAsync(
                new InvocationConfiguration(),
                TestContext.Current.CancellationToken);

        StringWriter metadataOutput = new();
        string settingsRoot = GetTempPath();
        int metadataExitCode;
        try
        {
            metadataExitCode = await InvokeAsync(
                new CompareMetadataCommand(
                    factory,
                    CreateConsole(metadataOutput),
                    new AppSettingsStore(Path.Combine(settingsRoot, "settings.json"))),
                baseName,
                targetName,
                "--output",
                "json");
        }
        finally
        {
            DeleteDirectory(settingsRoot);
        }

        Assert.Equal(2, layerProcessTerminator.ExitCode);
        JsonObject layers = JsonNode.Parse(layerOutput.ToString())!.AsObject();
        Assert.False(layers["summary"]!["areEqual"]!.GetValue<bool>());
        Assert.True(layers["summary"]!["targetIncludesAllBaseLayers"]!.GetValue<bool>());
        Assert.Equal("added", layers["layerComparisons"]?[1]?["layerDiff"]?.GetValue<string>());
        Assert.Equal(0, metadataExitCode);
        JsonObject metadata = JsonNode.Parse(metadataOutput.ToString())!.AsObject();
        Assert.False(metadata["summary"]!["areEqual"]!.GetValue<bool>());
        Assert.Contains(
            metadata["comparisons"]!.AsArray(),
            comparison => comparison?["path"]?.GetValue<string>() == "environment[\"VALUE\"]");
        Assert.Contains(
            metadata["comparisons"]!.AsArray(),
            comparison => comparison?["path"]?.GetValue<string>() == "environment[\"ADDED\"]");
    }

    public async Task CompareFilesCommand_ExtractsLiveImagesForConfiguredTool()
    {
        string repository = fixture.GetRepositoryName(
            nameof(CompareFilesCommand_ExtractsLiveImagesForConfiguredTool));
        LayerSeed baseLayer = await fixture.UploadLayerAsync(
            repository,
            LayerEntry.File("value", "base-initial"));
        LayerSeed baseUpdateLayer = await fixture.UploadLayerAsync(
            repository,
            LayerEntry.File("value", "base-final"));
        LayerSeed targetLayer = await fixture.UploadLayerAsync(
            repository,
            LayerEntry.File("value", "target-initial"));
        LayerSeed targetUpdateLayer = await fixture.UploadLayerAsync(
            repository,
            LayerEntry.File("value", "target-final"));
        await fixture.PutImageAsync(repository, "base", [baseLayer, baseUpdateLayer]);
        await fixture.PutImageAsync(repository, "target", [targetLayer, targetUpdateLayer]);

        string tempRoot = GetTempPath();
        string settingsPath = Path.Combine(tempRoot, "settings.json");
        string comparePath = Path.Combine(tempRoot, "compare");
        AppSettings settings = AppSettings.Load(settingsPath);
        settings.FileCompareTool.ExePath = "comparison-tool";
        settings.FileCompareTool.Args = "\"{0}\" \"{1}\"";
        settings.Save();
        string? launchedFile = null;
        string? launchedArguments = null;
        string? baseContent = null;
        string? targetContent = null;
        TestProcessLauncher processLauncher = new();
        CompareFilesCommand command = new(
            fixture.CreateClientFactory(),
            new AppSettingsStore(settingsPath),
            new TestDredgePathProvider(tempRoot),
            processLauncher);

        try
        {
            int exitCode = await InvokeAsync(
                command,
                $"{fixture.Registry}/{repository}:base",
                $"{fixture.Registry}/{repository}:target",
                "--base-layer-index",
                "0",
                "--target-layer-index",
                "0");

            Assert.Equal(0, exitCode);
            launchedFile = processLauncher.StartInfo?.FileName;
            launchedArguments = processLauncher.StartInfo?.Arguments;
            baseContent = File.ReadAllText(Path.Combine(comparePath, "base", "value"));
            targetContent = File.ReadAllText(Path.Combine(comparePath, "target", "value"));
            Assert.Equal("comparison-tool", launchedFile);
            Assert.Contains(Path.Combine(comparePath, "base"), launchedArguments);
            Assert.Contains(Path.Combine(comparePath, "target"), launchedArguments);
            Assert.Equal("base-initial", baseContent);
            Assert.Equal("target-initial", targetContent);
        }
        finally
        {
            DeleteDirectory(tempRoot);
        }
    }

    private async Task<ImageSeed> SeedFileSystemImageAsync(string testName)
    {
        string repository = fixture.GetRepositoryName(testName);
        LayerSeed baseLayer = await fixture.UploadLayerAsync(
            repository,
            LayerEntry.File("app/value", "old"),
            LayerEntry.File("app/removed", "remove me"));
        LayerSeed updateLayer = await fixture.UploadLayerAsync(
            repository,
            LayerEntry.File("app/value", "new"),
            LayerEntry.File("app/.wh.removed", string.Empty));
        return await fixture.PutImageAsync(repository, "latest", [baseLayer, updateLayer]);
    }

    private static IAnsiConsole CreateConsole(TextWriter output) =>
        AnsiConsole.Create(new AnsiConsoleSettings
        {
            Ansi = AnsiSupport.No,
            ColorSystem = ColorSystemSupport.NoColors,
            Out = new AnsiConsoleOutput(output)
        });

    private static Task<int> InvokeAsync(Command command, params string[] args) =>
        InvokeCoreAsync(command, args);

    private static Task<int> InvokeCoreAsync(Command command, string[] args)
    {
        if (command is IProcessTerminationAware terminationAware)
        {
            terminationAware.ProcessTerminator = new TestProcessTerminator();
        }

        return command
            .Parse(args)
            .InvokeAsync(
                new InvocationConfiguration(),
                TestContext.Current.CancellationToken);
    }

    private static string GetTempPath() =>
        Path.Combine(Path.GetTempPath(), $"dredge-integration-{Guid.NewGuid():N}");

    private static void DeleteDirectory(string path)
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }
    }
}

[Trait("Category", "Integration")]
public sealed class ImageFileSystemCommandIntegrationTests
{
    private readonly RegistryFixture fixture;

    public ImageFileSystemCommandIntegrationTests(RegistryFixture fixture)
    {
        this.fixture = fixture;
    }

    [Fact]
    public async Task FileSystemCommands_ReadAndExtractLiveImageLayers()
    {
        await fixture.EnsureInitializedAsync();
        await new ImageCommandIntegrationScenarios(fixture)
            .FileSystemCommands_ReadAndExtractLiveImageLayers();
    }
}

[Trait("Category", "Integration")]
public sealed class ImageInspectionCommandIntegrationTests
{
    private readonly RegistryFixture fixture;

    public ImageInspectionCommandIntegrationTests(RegistryFixture fixture)
    {
        this.fixture = fixture;
    }

    [Fact]
    public async Task InspectionCommands_ReadLiveImageMetadata()
    {
        await fixture.EnsureInitializedAsync();
        await new ImageCommandIntegrationScenarios(fixture)
            .InspectionCommands_ReadLiveImageMetadata();
    }
}

[Trait("Category", "Integration")]
public sealed class ImageComparisonCommandIntegrationTests
{
    private readonly RegistryFixture fixture;

    public ImageComparisonCommandIntegrationTests(RegistryFixture fixture)
    {
        this.fixture = fixture;
    }

    [Fact]
    public async Task CompareCommands_UseLiveLayerAndConfigurationData()
    {
        await fixture.EnsureInitializedAsync();
        await new ImageCommandIntegrationScenarios(fixture)
            .CompareCommands_UseLiveLayerAndConfigurationData();
    }
}

[Trait("Category", "Integration")]
public sealed class ImageCompareFilesCommandIntegrationTests
{
    private readonly RegistryFixture fixture;

    public ImageCompareFilesCommandIntegrationTests(RegistryFixture fixture)
    {
        this.fixture = fixture;
    }

    [Fact]
    public async Task CompareFilesCommand_ExtractsLiveImagesForConfiguredTool()
    {
        await fixture.EnsureInitializedAsync();
        await new ImageCommandIntegrationScenarios(fixture)
            .CompareFilesCommand_ExtractsLiveImagesForConfiguredTool();
    }
}
