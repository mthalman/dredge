using System.CommandLine;
using Valleysoft.DockerRegistryClient;
using Valleysoft.DockerRegistryClient.Models.Images;
using Valleysoft.DockerRegistryClient.Models.Manifests;
using Valleysoft.DockerRegistryClient.Models.Manifests.Docker;
using Valleysoft.Dredge.Commands;
using Valleysoft.Dredge.Commands.Image;

namespace Valleysoft.Dredge.Tests;

public sealed class EmptyImageOutputTests : IDisposable
{
    private readonly string root = Path.Combine(
        Directory.GetCurrentDirectory(), $".empty-image-output-{Guid.NewGuid():N}");

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task SaveLayers_CreatesEmptyDestination(bool daemon, bool noSquash)
    {
        string destination = Path.Combine(root, "saved");
        SaveLayersCommand command = new(Registry(), new TestDredgePathProvider(root));
        string[] arguments = noSquash
            ? [Reference(daemon), destination, "--no-squash"]
            : [Reference(daemon), destination];

        Assert.Equal(0, await InvokeAsync(command, arguments));

        Assert.True(Directory.Exists(destination));
        Assert.Empty(Directory.EnumerateFileSystemEntries(destination));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task CompareFiles_EmptyImagesProvideRealDirectoriesToExternalTool(bool baseDaemon, bool targetDaemon)
    {
        AppSettings settings = new();
        settings.FileCompareTool.ExePath = "test-compare";
        settings.FileCompareTool.Args = "\"{0}\" \"{1}\"";
        CheckingLauncher launcher = new(root);
        CompareFilesCommand command = new(Registry(),
            new TestAppSettingsStore(settings), new TestDredgePathProvider(root), launcher);

        Assert.Equal(0, await InvokeAsync(command, [Reference(baseDaemon), Reference(targetDaemon)]));

        Assert.True(launcher.Started);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(1)]
    public async Task SaveLayers_EmptyImageStillRejectsLayerIndexWithoutCreatingDestination(int index)
    {
        string destination = Path.Combine(root, "invalid-index");
        await using ResolvedImage source = await new EmptySources().OpenAsync(
            "docker://image:dev", new PlatformOptionsBase(), TestContext.Current.CancellationToken);

        Exception error = await Assert.ThrowsAsync<Exception>(() => ImageHelper.SaveImageLayersToDiskAsync(
            Registry(), "docker://image:dev", destination, index, "--layer-index", false,
            new PlatformOptionsBase(), TestContext.Current.CancellationToken,
            new TestDredgePathProvider(root), resolvedImage: source));

        Assert.Equal("The image has no layers, so the '--layer-index' option cannot be used.", error.Message);
        Assert.False(Directory.Exists(destination));
    }

    [Fact]
    public async Task SaveLayers_CanceledEmptyImageDoesNotCreateDestination()
    {
        string destination = Path.Combine(root, "canceled");
        await using ResolvedImage source = await new EmptySources().OpenAsync(
            "docker://image:dev", new PlatformOptionsBase(), TestContext.Current.CancellationToken);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ImageHelper.SaveImageLayersToDiskAsync(
            Registry(), "docker://image:dev", destination, null, "--layer-index", false,
            new PlatformOptionsBase(), new CancellationToken(canceled: true),
            new TestDredgePathProvider(root), resolvedImage: source));

        Assert.False(Directory.Exists(destination));
    }

    private static string Reference(bool daemon) => daemon ? "docker://image:dev" : "image";

    private static ManifestInfo Manifest() => new(
        "application/vnd.docker.distribution.manifest.v2+json", "sha256:empty", new DockerManifest { Layers = [] });

    private static IDockerRegistryClientFactory Registry()
    {
        Mock<IDockerRegistryClient> client = new() { DefaultValue = DefaultValue.Mock };
        client.Setup(value => value.Manifests.GetAsync("library/image", "latest", It.IsAny<CancellationToken>()))
            .ReturnsAsync(Manifest());
        Mock<IDockerRegistryClientFactory> factory = new();
        factory.Setup(value => value.GetClientAsync(null, It.IsAny<CancellationToken>())).ReturnsAsync(client.Object);
        return factory.Object;
    }

    private static Task<int> InvokeAsync<TOptions>(RegistryCommandBase<TOptions> command, string[] arguments)
        where TOptions : OptionsBase, new()
    {
        command.ImageSourceFactory = new EmptySources();
        ((IProcessTerminationAware)command).ProcessTerminator = new TestProcessTerminator();
        return command.Parse(arguments).InvokeAsync(new InvocationConfiguration(), TestContext.Current.CancellationToken);
    }

    public void Dispose()
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private sealed class EmptySources : IImageSourceFactory, IImageContentSource
    {
        public Task<ResolvedImage> OpenAsync(
            string reference, PlatformOptionsBase options, CancellationToken cancellationToken,
            ImageSourceRequirements requirements = ImageSourceRequirements.LayerContent)
        {
            ImageSourceReference parsed = ImageSourceReference.Parse(reference);
            ManifestInfo info = Manifest();
            return Task.FromResult(new ResolvedImage(
                parsed, parsed.GetImageName(), new ResolvedManifest(info, (IImageManifest)info.Manifest), info,
                new Image
                {
                    Os = "linux",
                    Architecture = "amd64",
                    RootFilesystem = new RootFilesystem { Type = "layers", DiffIds = [] },
                    History = []
                }, [], this, "sha256:empty", "linux/amd64"));
        }

        public Task<BlobDownloadResult> GetRangeAsync(
            string digest, long start, long? length, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("An empty image must not read layer content.");
    }

    private sealed class CheckingLauncher(string root) : IProcessLauncher
    {
        public bool Started { get; private set; }

        public void Start(System.Diagnostics.ProcessStartInfo startInfo)
        {
            string workspace = Assert.Single(Directory.GetDirectories(Path.Combine(root, "compare")));
            foreach (string side in new[] { "base", "target" })
            {
                string path = Path.Combine(workspace, side);
                Assert.True(Directory.Exists(path));
                Assert.Empty(Directory.EnumerateFileSystemEntries(path));
                Assert.Contains(path, startInfo.Arguments);
            }
            Started = true;
        }
    }
}
