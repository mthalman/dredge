using System.CommandLine;
using System.Text;
using System.Text.Json.Nodes;
using Spectre.Console;
using Valleysoft.DockerRegistryClient.Models.Images;
using Valleysoft.DockerRegistryClient.Models.Manifests;
using Valleysoft.DockerRegistryClient.Models.Manifests.Docker;
using Valleysoft.Dredge.Commands;
using Valleysoft.Dredge.Commands.Image;

namespace Valleysoft.Dredge.Tests;

public class WindowsDaemonCommandTests
{
    private const string Reference = "docker://windows-local:dev";
    private const string OsVersion = "10.0.20348.1";
    private const string BaseTag = OsVersion + "-amd64";
    private static readonly string DiffId = "sha256:" + new string('a', 64);
    private static readonly string ImageId = "sha256:" + new string('b', 64);

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task MetadataCommands_WorkWithoutWindowsFilesystemAccess(
        bool omitConfigurationVersion, bool differentFallbackVersion)
    {
        WindowsSources sources = new(omitConfigurationVersion, differentFallbackVersion);
        Mock<IDockerRegistryClient> mcr = new() { DefaultValue = DefaultValue.Mock };
        mcr.Setup(client => client.Manifests.ExistsAsync(
                It.IsAny<string>(), BaseTag, It.IsAny<CancellationToken>()))
            .ReturnsAsync((string repo, string _, CancellationToken _) => repo == "windows/servercore");
        mcr.Setup(client => client.Manifests.GetAsync(
                "windows/servercore", BaseTag, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Manifest());
        mcr.Setup(client => client.Blobs.GetAsync(
                "windows/servercore", ImageId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new MemoryStream(sources.ConfigurationJson));
        Mock<IDockerRegistryClientFactory> registry = new(MockBehavior.Strict);
        registry.Setup(factory => factory.GetClientAsync(RegistryHelper.McrRegistry, It.IsAny<CancellationToken>()))
            .ReturnsAsync(mcr.Object);

        using StringWriter inspect = new();
        Assert.Equal(0, await InvokeAsync(new InspectCommand(registry.Object, inspect), sources, Reference));
        Assert.Equal("windows", JsonNode.Parse(inspect.ToString())!["os"]!.GetValue<string>());
        Assert.Equal(omitConfigurationVersion, JsonNode.Parse(inspect.ToString())!["os.version"] is null);

        using StringWriter os = new();
        Assert.Equal(0, await InvokeAsync(new OsCommand(registry.Object, os), sources, Reference));
        Assert.Equal(OsVersion, JsonNode.Parse(os.ToString())!["Version"]!.GetValue<string>());

        using StringWriter dockerfile = new();
        Assert.Equal(0, await InvokeAsync(new DockerfileCommand(registry.Object, ConsoleFor(dockerfile)),
            sources, Reference, "--no-color"));
        Assert.Contains($"FROM mcr.microsoft.com/windows/servercore:{BaseTag}", dockerfile.ToString());

        using StringWriter layers = new();
        Assert.Equal(0, await InvokeAsync(new CompareLayersCommand(registry.Object, ConsoleFor(layers)),
            sources, Reference, Reference, "--output", "json", "--compressed-size"));
        JsonNode layerResult = JsonNode.Parse(layers.ToString())!;
        Assert.True(layerResult["summary"]!["areEqual"]!.GetValue<bool>());
        Assert.Null(layerResult["layerComparisons"]![0]!["base"]!["compressedSize"]);

        using StringWriter metadata = new();
        Assert.Equal(0, await InvokeAsync(new CompareMetadataCommand(registry.Object, ConsoleFor(metadata)),
            sources, Reference, Reference, "--output", "json"));
        Assert.True(JsonNode.Parse(metadata.ToString())!["summary"]!["areEqual"]!.GetValue<bool>());
        mcr.Verify(client => client.Blobs.ExistsAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task FilesystemCommands_RejectWindowsBeforeReadingLayers()
    {
        WindowsSources sources = new();
        Mock<IDockerRegistryClientFactory> registry = new(MockBehavior.Strict);
        await using LayerCacheTestContext cache = new();
        using MemoryStream cat = new();
        Assert.Equal(1, await InvokeAsync(new CatCommand(registry.Object, cat, cache.Paths),
            sources, Reference, "/hello"));
        Assert.Equal(1, await InvokeAsync(new LsCommand(registry.Object, null, cache.Paths),
            sources, Reference));
        Assert.Equal(1, await InvokeAsync(new ExtractCommand(registry.Object, cache.Paths),
            sources, Reference, "/hello", Path.Combine(cache.Root, "extracted")));
        Assert.Equal(1, await InvokeAsync(new SaveLayersCommand(registry.Object, cache.Paths),
            sources, Reference, Path.Combine(cache.Root, "saved")));
        AppSettings settings = new();
        settings.FileCompareTool.ExePath = "must-not-run";
        settings.FileCompareTool.Args = "\"{0}\" \"{1}\"";
        TestProcessLauncher launcher = new();
        Assert.Equal(1, await InvokeAsync(new CompareFilesCommand(registry.Object,
                new TestAppSettingsStore(settings), cache.Paths, launcher),
            sources, Reference, Reference));
        Assert.Null(launcher.StartInfo);
        Assert.Equal(6, sources.OpenedImages);
        Assert.Equal(0, sources.LayerReadAttempts);
        registry.VerifyNoOtherCalls();
    }

    private static Task<int> InvokeAsync<TOptions>(
        RegistryCommandBase<TOptions> command, IImageSourceFactory sources, params string[] arguments)
        where TOptions : OptionsBase, new()
    {
        command.ImageSourceFactory = sources;
        ((IProcessTerminationAware)command).ProcessTerminator = new TestProcessTerminator();
        return command.Parse(arguments).InvokeAsync(new InvocationConfiguration(), TestContext.Current.CancellationToken);
    }

    private static IAnsiConsole ConsoleFor(TextWriter output) =>
        AnsiConsole.Create(new AnsiConsoleSettings { Out = new AnsiConsoleOutput(output) });

    private static ManifestInfo Manifest() => new(
        "application/vnd.docker.distribution.manifest.v2+json", ImageId,
        new DockerManifest
        {
            Config = new ManifestConfig { Digest = ImageId },
            Layers = [new ManifestLayer { Digest = DiffId }]
        });

    private sealed class WindowsSources(bool omitConfigurationVersion = false, bool differentFallbackVersion = false) :
        IImageSourceFactory, IImageContentSource
    {
        private readonly Image configuration = new()
        {
            Os = "windows",
            OsVersion = omitConfigurationVersion ? null : OsVersion,
            Architecture = "amd64",
            RootFilesystem = new RootFilesystem { Type = "layers", DiffIds = [DiffId] },
            History = [new LayerHistory { CreatedBy = "base image" }]
        };

        public byte[] ConfigurationJson => Encoding.UTF8.GetBytes(JsonHelper.Serialize(configuration));
        public int OpenedImages { get; private set; }
        public int LayerReadAttempts { get; private set; }

        public Task<ResolvedImage> OpenAsync(
            string reference, PlatformOptionsBase options, CancellationToken cancellationToken,
            ImageSourceRequirements requirements = ImageSourceRequirements.LayerContent)
        {
            OpenedImages++;
            ImageSourceReference parsed = ImageSourceReference.Parse(reference);
            ManifestInfo info = Manifest();
            return Task.FromResult(new ResolvedImage(
                parsed, parsed.GetImageName(), new ResolvedManifest(info, (IImageManifest)info.Manifest),
                null, configuration, ConfigurationJson, this, ImageId, $"windows/amd64 ({OsVersion})",
                selectedPlatform: new("windows", "amd64", null,
                    differentFallbackVersion ? "10.0.17763.1" : OsVersion)));
        }

        public Task<Valleysoft.DockerRegistryClient.BlobDownloadResult> GetRangeAsync(
            string digest, long start, long? length, CancellationToken cancellationToken)
        {
            LayerReadAttempts++;
            throw new InvalidOperationException("Windows metadata commands must not read filesystem layers.");
        }
    }
}
