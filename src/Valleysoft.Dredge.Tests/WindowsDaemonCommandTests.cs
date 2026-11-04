using System.CommandLine;
using System.IO.Compression;
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
    private static readonly byte[] LayerBytes =
        WindowsImageTestData.Layer(WindowsImageTestData.File("Files/hello.txt", "hello"));
    private static readonly string LayerDigest = LayerCacheTestContext.Digest(LayerBytes);
    private static readonly string DiffId = GetDiffId(LayerBytes);
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
        await using LayerCacheTestContext cache = new();
        Assert.Equal(0, await InvokeAsync(new OsCommand(registry.Object, os, cache.Paths), sources, Reference));
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
    public async Task FilesystemCommands_UseWindowsFilesystemSupport()
    {
        WindowsSources sources = new();
        Mock<IDockerRegistryClientFactory> registry = new(MockBehavior.Strict);
        await using LayerCacheTestContext cache = new();
        using MemoryStream cat = new();
        Assert.Equal(0, await InvokeAsync(new CatCommand(registry.Object, cat, cache.Paths),
            sources, Reference, "/hello.txt"));
        Assert.Equal("hello", Encoding.UTF8.GetString(cat.ToArray()));

        using StringWriter listing = new();
        Assert.Equal(0, await InvokeAsync(new LsCommand(registry.Object, ConsoleFor(listing), cache.Paths),
            sources, Reference, "--recursive", "--output", "json"));
        Assert.Contains(JsonNode.Parse(listing.ToString())!.AsArray(),
            static entry => entry!["path"]!.GetValue<string>() == "hello.txt");

        string extracted = Path.Combine(cache.Root, "extracted");
        Assert.Equal(0, await InvokeAsync(new ExtractCommand(registry.Object, cache.Paths),
            sources, Reference, "/hello.txt", extracted));
        Assert.Equal("hello", await File.ReadAllTextAsync(extracted));

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
        Assert.True(sources.OpenedImages > 0);
        Assert.True(sources.LayerReadAttempts > 0);
        registry.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task OsCommand_RejectsWindowsDaemonLayersThatDoNotMatchConfiguredDiffIds()
    {
        WindowsSources sources = new(corruptDiffId: true);
        Mock<IDockerRegistryClient> mcr = new() { DefaultValue = DefaultValue.Mock };
        Mock<IDockerRegistryClientFactory> registry = new(MockBehavior.Strict);
        registry.Setup(factory => factory.GetClientAsync(RegistryHelper.McrRegistry, It.IsAny<CancellationToken>()))
            .ReturnsAsync(mcr.Object);
        await using LayerCacheTestContext cache = new();
        using StringWriter output = new();

        Assert.Equal(1, await InvokeAsync(new OsCommand(registry.Object, output, cache.Paths), sources, Reference));
        registry.Verify(factory => factory.GetClientAsync(
            It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
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
            Layers = [new ManifestLayer
            {
                Digest = LayerDigest, Size = LayerBytes.Length,
                MediaType = "application/vnd.docker.image.rootfs.diff.tar.gzip"
            }]
        });

    private sealed class WindowsSources(bool omitConfigurationVersion = false, bool differentFallbackVersion = false,
        bool corruptDiffId = false) :
        IImageSourceFactory, IImageContentSource
    {
        private readonly Image configuration = new()
        {
            Os = "windows",
            OsVersion = omitConfigurationVersion ? null : OsVersion,
            Architecture = "amd64",
            RootFilesystem = new RootFilesystem
            {
                Type = "layers",
                DiffIds = [corruptDiffId ? "sha256:" + new string('0', 64) : DiffId]
            },
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
            Assert.Equal(LayerDigest, digest);
            return Task.FromResult(new Valleysoft.DockerRegistryClient.BlobDownloadResult(
                new MemoryStream(LayerBytes), false, null, null, LayerBytes.Length));
        }
    }

    private static string GetDiffId(byte[] layer)
    {
        using MemoryStream compressed = new(layer);
        using GZipStream gzip = new(compressed, CompressionMode.Decompress);
        using MemoryStream decoded = new();
        gzip.CopyTo(decoded);
        return LayerCacheTestContext.Digest(decoded.ToArray());
    }
}
