using System.CommandLine;
using Docker.DotNet.Models;
using Valleysoft.DockerRegistryClient;
using Valleysoft.DockerRegistryClient.Models.Manifests;
using Valleysoft.DockerRegistryClient.Models.Manifests.Docker;
using Valleysoft.DockerRegistryClient.Models.Manifests.Oci;
using Valleysoft.Dredge.Commands;
using Valleysoft.Dredge.Commands.Image;
using Valleysoft.Dredge.Explorer;
using DockerManifestReference = Valleysoft.DockerRegistryClient.Models.Manifests.Docker.ManifestReference;

namespace Valleysoft.Dredge.Tests;

public class ImageSourceFactoryTests : IDisposable
{
    private readonly string directory = Path.GetFullPath(Path.Combine(".test-docker-source", Guid.NewGuid().ToString("N")));

    [Fact]
    public async Task DaemonAcquisition_PinsIdBeforeExportAndPreservesRawConfiguration()
    {
        DockerArchiveFixture fixture = DockerArchiveFixture.Create();
        FakeEngine engine = new(fixture);
        IImageSourceFactory factory = CreateFactory(engine);
        await using ResolvedImage result = await factory.OpenAsync("docker://unpublished:tag", HostOptions(), TestContext.Current.CancellationToken);
        Assert.Equal(["unpublished:tag", fixture.Id], engine.Inspections);
        Assert.Equal([fixture.Id], engine.Exports);
        Assert.Equal(fixture.Id, result.Identity);
        Assert.Equal(fixture.Config, result.ConfigurationJson);
        Assert.Equal("unpublished", result.Name.Repo);
        Assert.Null(result.InitialManifest);
        Assert.True(engine.Disposed);
        using StringWriter diagnostics = new();
        result.Report(diagnostics);
        Assert.Contains("Docker image ID: " + fixture.Id, diagnostics.ToString());
        Assert.DoesNotContain("manifest", diagnostics.ToString());
    }

    [Fact]
    public async Task Connection_IsFrozenAcrossOperands()
    {
        DockerArchiveFixture fixture = DockerArchiveFixture.Create();
        List<DockerConnection> connections = [];
        ImageSourceFactory factory = new(Mock.Of<IDockerRegistryClientFactory>(), Settings(),
            connection =>
            {
                connections.Add(connection);
                return new FakeEngine(fixture);
            }, directory);
        await using ResolvedImage first = await factory.OpenAsync("docker://first:tag", HostOptions("http://localhost:1234"), TestContext.Current.CancellationToken);
        await using ResolvedImage second = await factory.OpenAsync("docker://second:tag", HostOptions("http://localhost:9999"), TestContext.Current.CancellationToken);
        Assert.Equal(2, connections.Count);
        Assert.Same(connections[0], connections[1]);
        Assert.Equal(1234, connections[1].Endpoint.Port);
    }

    [Theory]
    [InlineData("dev")]
    [InlineData("abc")]
    [InlineData("0123456789ab")]
    public async Task Sha256RepositoryTagsReachTheEngineWithoutRegistryNormalization(string tag)
    {
        DockerArchiveFixture fixture = DockerArchiveFixture.Create();
        FakeEngine engine = new(fixture);
        string value = $"sha256:{tag}";

        await using ResolvedImage result = await CreateFactory(engine).OpenAsync(
            $"docker://{value}", HostOptions(), TestContext.Current.CancellationToken);

        Assert.Equal([value, fixture.Id], engine.Inspections);
        Assert.Equal([fixture.Id], engine.Exports);
        Assert.Equal("sha256", result.Name.Repo);
        Assert.Equal(tag, result.Name.Tag);
        Assert.Null(result.Name.Registry);
        Assert.Equal(fixture.Id, result.Identity);
    }

    [Fact]
    public async Task ChangedDaemonIdentity_IsRejectedBeforeInspect()
    {
        DockerArchiveFixture fixture = DockerArchiveFixture.Create();
        FakeEngine secondEngine = new(fixture) { DaemonId = "different-daemon" };
        int calls = 0;
        ImageSourceFactory factory = new(Mock.Of<IDockerRegistryClientFactory>(), Settings(),
            _ => calls++ == 0 ? new FakeEngine(fixture) : secondEngine, directory);
        await using ResolvedImage first = await factory.OpenAsync("docker://first", HostOptions(), TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<InvalidOperationException>(() => factory.OpenAsync("docker://second", HostOptions(), TestContext.Current.CancellationToken));
        Assert.Empty(secondEngine.Inspections);
    }

    [Fact]
    public async Task PlatformMismatch_FailsBeforeExport()
    {
        FakeEngine engine = new(DockerArchiveFixture.Create());
        PlatformOptionsBase options = HostOptions();
        options.Os = "windows";
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            CreateFactory(engine).OpenAsync("docker://local", options, TestContext.Current.CancellationToken));
        Assert.Empty(engine.Exports);
        Assert.True(engine.Disposed);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WindowsFilesystemAcquisition_ExportsPinnedImage(bool contentOnly)
    {
        FakeEngine engine = new(DockerArchiveFixture.Create(oci: true, gzip: true, windows: true))
        {
            Os = "windows",
            OsVersion = "10.0.20348.1"
        };

        await using ResolvedImage source = await CreateFactory(engine).OpenAsync(
            "docker://local", HostOptions(), TestContext.Current.CancellationToken,
            contentOnly ? ImageSourceRequirements.LinuxLayerContent : ImageSourceRequirements.LinuxFileSystem);

        Assert.Equal("windows", source.Configuration.Os);
        Assert.Single(engine.Exports);
        Assert.True(engine.Disposed);
    }

    [Theory]
    [InlineData("cat", true)]
    [InlineData("ls", true)]
    [InlineData("extract", true)]
    [InlineData("save-layers", false)]
    [InlineData("compare-files", false)]
    public async Task WindowsFilesystemCommands_RespectSupportedOperations(string operation, bool supported)
    {
        FakeEngine engine = new(DockerArchiveFixture.Create(oci: true, gzip: true, windows: true))
        {
            Os = "windows",
            OsVersion = "10.0.20348.1"
        };
        ImageSourceFactory factory = CreateFactory(engine);
        IDockerRegistryClientFactory registry = Mock.Of<IDockerRegistryClientFactory>();
        await using LayerCacheTestContext cache = new();
        using MemoryStream output = new();
        AppSettings settings = new();
        settings.FileCompareTool.ExePath = "must-not-run";
        settings.FileCompareTool.Args = "\"{0}\" \"{1}\"";
        TestProcessLauncher launcher = new();
        string destination = Path.Combine(cache.Root, "output");
        (Command Command, string[] Arguments) invocation = operation switch
        {
            "cat" => (Configure(new CatCommand(registry, output, cache.Paths)), ["docker://local", "/etc/example"]),
            "ls" => (Configure(new LsCommand(registry, null, cache.Paths)), ["docker://local"]),
            "extract" => (Configure(new ExtractCommand(registry, cache.Paths)), ["docker://local", "/etc/example", destination]),
            "save-layers" => (Configure(new SaveLayersCommand(registry, cache.Paths)), ["docker://local", destination]),
            "compare-files" => (Configure(new CompareFilesCommand(registry, Settings(settings), cache.Paths, launcher)),
                ["docker://local", "docker://local"]),
            _ => throw new ArgumentOutOfRangeException(nameof(operation))
        };

        int result = await invocation.Command.Parse([.. invocation.Arguments, "--docker-host", "http://localhost:2375"])
            .InvokeAsync(new InvocationConfiguration(), TestContext.Current.CancellationToken);

        Assert.Equal(supported ? 0 : 1, result);
        Assert.NotEmpty(engine.Inspections);
        Assert.Equal(supported, engine.Exports.Count > 0);
        Assert.True(engine.Disposed);
        Assert.Null(launcher.StartInfo);

        Command Configure<TOptions>(RegistryCommandBase<TOptions> command) where TOptions : OptionsBase, new()
        {
            command.ImageSourceFactory = factory;
            ((IProcessTerminationAware)command).ProcessTerminator = new TestProcessTerminator();
            return command;
        }
    }

    [Fact]
    public async Task WindowsExplorerAcquisition_ExportsPinnedImage()
    {
        FakeEngine engine = new(DockerArchiveFixture.Create(oci: true, gzip: true, windows: true))
        {
            Os = "windows",
            OsVersion = "10.0.20348.1"
        };

        await using ExplorerSource source = await ExplorerSource.OpenAsync(
            CreateFactory(engine), Mock.Of<IDockerRegistryClientFactory>(),
            "docker://local", HostOptions(), [], TestContext.Current.CancellationToken);

        Assert.NotEmpty(engine.Inspections);
        Assert.Single(engine.Exports);
        Assert.Equal("windows", source.Config.Os);
        Assert.True(engine.Disposed);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task WindowsNonFilesystemAcquisition_ReachesExport(bool metadataOnly)
    {
        FakeEngine engine = new(DockerArchiveFixture.Create()) { Os = "windows", FailExport = true };

        InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            CreateFactory(engine).OpenAsync("docker://local", HostOptions(),
                TestContext.Current.CancellationToken,
                metadataOnly ? ImageSourceRequirements.Metadata : ImageSourceRequirements.LayerContent));

        Assert.Equal("The pinned image was removed.", error.Message);
        Assert.Single(engine.Exports);
    }

    [Fact]
    public async Task PersistedPlatformMismatch_FailsBeforeExport()
    {
        FakeEngine engine = new(DockerArchiveFixture.Create());
        IAppSettingsStore settings = Settings(new AppSettings { Platform = new PlatformSettings { Architecture = "arm64" } });
        ImageSourceFactory factory = new(Mock.Of<IDockerRegistryClientFactory>(), settings, _ => engine, directory);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            factory.OpenAsync("docker://local", HostOptions(), TestContext.Current.CancellationToken));
        Assert.Empty(engine.Exports);
    }

    [Fact]
    public async Task ExplicitPlatform_OverridesSettings()
    {
        FakeEngine engine = new(DockerArchiveFixture.Create());
        IAppSettingsStore settings = Settings(new AppSettings { Platform = new PlatformSettings { Architecture = "arm64" } });
        ImageSourceFactory factory = new(Mock.Of<IDockerRegistryClientFactory>(), settings, _ => engine, directory);
        PlatformOptionsBase options = HostOptions();
        options.Architecture = "amd64";
        await using ResolvedImage result = await factory.OpenAsync("docker://local", options, TestContext.Current.CancellationToken);
        Assert.Equal("linux/amd64", result.Platform);
    }

    [Fact]
    public async Task ExactPlatformOptions_DoNotInheritSavedWindowsVersion()
    {
        FakeEngine engine = new(DockerArchiveFixture.Create());
        ImageSourceFactory factory = new(Mock.Of<IDockerRegistryClientFactory>(),
            Settings(new AppSettings { Platform = new() { OsVersion = "10.0.20348.1" } }),
            _ => engine, directory);
        PlatformOptionsBase options = ExplorerSource.ForPlatform(new("linux", "amd64", null, null));
        options.DockerHost = HostOptions().DockerHost;

        await using ResolvedImage result = await factory.OpenAsync(
            "docker://local", options, TestContext.Current.CancellationToken);

        Assert.Null(result.Configuration.OsVersion);
        Assert.Single(engine.Exports);
    }

    [Fact]
    public async Task ExplicitOsAndArchitecture_StillInheritSavedVersionForOrdinaryOptions()
    {
        FakeEngine engine = new(DockerArchiveFixture.Create());
        ImageSourceFactory factory = new(Mock.Of<IDockerRegistryClientFactory>(),
            Settings(new AppSettings { Platform = new() { OsVersion = "10.0.20348.1" } }),
            _ => engine, directory);
        PlatformOptionsBase options = HostOptions();
        options.Os = "linux";
        options.Architecture = "amd64";

        await Assert.ThrowsAsync<InvalidOperationException>(() => factory.OpenAsync(
            "docker://local", options, TestContext.Current.CancellationToken));

        Assert.Empty(engine.Exports);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ResolvedPlatformOptions_ResolveRegistryIndexWithoutSavedWindowsVersion(bool exactPlatform)
    {
        DockerArchiveFixture fixture = DockerArchiveFixture.Create(scratch: true);
        OciImageManifest manifest = new()
        {
            Config = new OciDescriptor { Digest = fixture.Id }, Layers = []
        };
        ManifestList index = new()
        {
            Manifests = [new DockerManifestReference
            {
                Digest = "sha256:linux",
                Platform = new ManifestPlatform { Os = "linux", Architecture = "amd64" }
            }]
        };
        Mock<IDockerRegistryClient> client = new() { DefaultValue = DefaultValue.Mock };
        client.Setup(value => value.Manifests.GetAsync("app", "tag", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ManifestInfo("application/index", "sha256:index", index));
        client.Setup(value => value.Manifests.GetAsync("app", "sha256:linux", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ManifestInfo("application/manifest", "sha256:linux", manifest));
        client.Setup(value => value.Blobs.GetAsync("app", fixture.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new MemoryStream(fixture.Config));
        Mock<IDockerRegistryClientFactory> registries = new();
        registries.Setup(value => value.GetClientAsync("registry.test", It.IsAny<CancellationToken>()))
            .ReturnsAsync(client.Object);
        ImageSourceFactory factory = new(registries.Object,
            Settings(new AppSettings { Platform = new() { OsVersion = "10.0.20348.1" } }));

        await using ResolvedImage result = await factory.OpenAsync(
            "registry://registry.test/app:tag",
            exactPlatform ? ExplorerSource.ForPlatform(new("linux", "amd64", null, null))
                : new() { Os = "linux", Architecture = "amd64", OsVersion = "" },
            TestContext.Current.CancellationToken);

        Assert.Equal("sha256:linux", result.Identity);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExplorerRegistryPlatform_DoesNotReapplySavedWindowsVersion(bool daemonBase)
    {
        DockerArchiveFixture fixture = DockerArchiveFixture.Create();
        FakeEngine engine = new(fixture);
        OciImageManifest manifest = new()
        {
            Config = new OciDescriptor { Digest = fixture.Id },
            Layers = [new OciDescriptor
            {
                Digest = DockerImageArchive.Hash(fixture.StoredLayer), Size = fixture.StoredLayer.Length
            }]
        };
        ManifestInfo info = new("application/vnd.oci.image.manifest.v1+json", "sha256:manifest", manifest);
        Mock<IDockerRegistryClient> client = new() { DefaultValue = DefaultValue.Mock };
        client.Setup(value => value.Manifests.GetAsync("app", "tag", It.IsAny<CancellationToken>()))
            .ReturnsAsync(info);
        client.Setup(value => value.Blobs.GetAsync("app", fixture.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new MemoryStream(fixture.Config));
        client.Setup(value => value.Blobs.GetRangeAsync("app", manifest.Layers[0].Digest!,
                It.IsAny<long>(), It.IsAny<long?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new BlobDownloadResult(new MemoryStream(fixture.StoredLayer),
                false, null, null, fixture.StoredLayer.Length));
        Mock<IDockerRegistryClientFactory> registries = new();
        registries.Setup(value => value.GetClientAsync("registry.test", It.IsAny<CancellationToken>()))
            .ReturnsAsync(client.Object);
        ImageSourceFactory sources = new(registries.Object,
            Settings(new AppSettings { Platform = new() { OsVersion = "10.0.20348.1" } }),
            _ => engine, directory);
        PlatformOptionsBase options = HostOptions();
        options.Os = "linux";
        options.Architecture = "amd64";
        await using LayerCacheTestContext cache = new();

        await using ExplorerSource baseline = await ExplorerSource.OpenAsync(
            sources, registries.Object, "registry://registry.test/app:tag", options,
            daemonBase ? ["docker://local"] : null, TestContext.Current.CancellationToken,
            store: cache.Store);
        if (daemonBase)
        {
            Assert.Single(baseline.BaseImages);
        }
        else
        {
            await using ExplorerSource target = await ExplorerSource.OpenAsync(
                sources, registries.Object, "docker://local", options, null,
                TestContext.Current.CancellationToken,
                exactPlatform: ExplorerPlatform.FromConfig(baseline.Config, baseline.Platform),
                store: cache.Store);
            Assert.True(ExplorerPlatform.FromConfig(baseline.Config, baseline.Platform)
                .IsEquivalentTo(target.Platform!));
        }
        Assert.Single(engine.Exports);
    }

    [Fact]
    public async Task RemovedPinnedImage_FailsWithoutResolvingTagAgain()
    {
        FakeEngine engine = new(DockerArchiveFixture.Create()) { FailExport = true };
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            CreateFactory(engine).OpenAsync("docker://local:tag", HostOptions(), TestContext.Current.CancellationToken));
        Assert.Equal(2, engine.Inspections.Count);
        Assert.Single(engine.Exports);
        Assert.True(engine.Disposed);
    }

    [Fact]
    public async Task InvalidImmutableId_IsRejectedBeforeExport()
    {
        FakeEngine engine = new(DockerArchiveFixture.Create()) { ImageId = "not-a-concrete-config-id" };
        await Assert.ThrowsAsync<NotSupportedException>(() =>
            CreateFactory(engine).OpenAsync("docker://local", HostOptions(), TestContext.Current.CancellationToken));
        Assert.Empty(engine.Exports);
    }

    [Fact]
    public async Task RegistryAcquisition_RetainsOriginalManifestAndExactRawConfig()
    {
        DockerArchiveFixture fixture = DockerArchiveFixture.Create(scratch: true);
        OciImageManifest manifest = new()
        {
            Config = new OciDescriptor { Digest = fixture.Id },
            Layers = []
        };
        ManifestInfo initial = new("application/vnd.oci.image.manifest.v1+json", "sha256:manifest", manifest);
        Mock<IDockerRegistryClient> client = new() { DefaultValue = DefaultValue.Mock };
        client.Setup(value => value.Manifests.GetAsync("library/local", "tag", It.IsAny<CancellationToken>())).ReturnsAsync(initial);
        client.Setup(value => value.Blobs.GetAsync("library/local", fixture.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new MemoryStream(fixture.Config));
        Mock<IDockerRegistryClientFactory> registryFactory = new();
        registryFactory.Setup(value => value.GetClientAsync(It.IsAny<string?>(), It.IsAny<CancellationToken>())).ReturnsAsync(client.Object);
        ImageSourceFactory factory = new(registryFactory.Object, Settings());
        ResolvedImage result = await factory.OpenAsync("registry://local:tag", new PlatformOptionsBase(), TestContext.Current.CancellationToken);
        Assert.Same(initial, result.InitialManifest);
        Assert.Same(initial, result.Resolved.ManifestInfo);
        Assert.Equal(fixture.Config, result.ConfigurationJson);
        Assert.Equal("sha256:manifest", result.Identity);
        using StringWriter output = new();
        result.Report(output);
        Assert.Empty(output.ToString());
        await result.DisposeAsync();
        await result.DisposeAsync();
        client.Verify(value => value.Dispose(), Times.Once);
    }

    private ImageSourceFactory CreateFactory(FakeEngine engine) =>
        new(Mock.Of<IDockerRegistryClientFactory>(), Settings(), _ => engine, directory);

    [Theory]
    [InlineData("arm64")]
    [InlineData("arm")]
    public async Task CopiedDaemonCommandRetainsOverridesAgainstSavedPlatformDefaults(string architecture)
    {
        await using LayerCacheTestContext cache = new();
        DockerArchiveFixture fixture = DockerArchiveFixture.Create(architecture: architecture);
        AppSettings settings = new();
        settings.Platform.Os = "windows";
        settings.Platform.Architecture = "amd64";
        settings.Platform.OsVersion = "10.0.20348.1";
        IDockerRegistryClientFactory registries = Mock.Of<IDockerRegistryClientFactory>();
        ImageSourceFactory sources = new(registries, Settings(settings),
            _ => new FakeEngine(fixture) { Architecture = architecture }, directory);
        PlatformOptionsBase options = ExplorerSource.ForPlatform(new("linux", architecture, null, null), HostOptions());
        await using ExplorerSource source = await ExplorerSource.OpenAsync(
            sources, registries, "docker://local:dev", options, null, TestContext.Current.CancellationToken,
            store: cache.Store);
        ExplorerImage model = ExplorerImage.FromSource(source);
        ExplorerPresenter presenter = new(model, 180, 50);
        string copied = presenter.CopyCommandText("etc/example", false);
        Assert.Contains("--os linux", copied);
        Assert.Contains("--arch " + architecture, copied);
        Assert.Contains("--os-version=\"\"", copied);
        Assert.Contains("--docker-host http://localhost:2375", copied);
        Assert.Contains("docker://" + fixture.Id, copied);
        Assert.Contains("--arch " + architecture, presenter.CopyCommandText("etc", true));
        using MemoryStream output = new();
        CatCommand command = new(registries, output, cache.Paths) { ImageSourceFactory = sources };
        ((IProcessTerminationAware)command).ProcessTerminator = new TestProcessTerminator();

        var parsed = command.Parse(copied["dredge image cat ".Length..]);
        Assert.Empty(parsed.Errors);
        int exitCode = await parsed.InvokeAsync(new InvocationConfiguration(), TestContext.Current.CancellationToken);

        Assert.Equal(0, exitCode);
        Assert.Equal("", command.Options.OsVersion);
        Assert.Equal("fixture"u8.ToArray(), output.ToArray());
    }

    [Theory]
    [InlineData(null, "10.0.20348.1")]
    [InlineData("--os-version=\"\"", null)]
    [InlineData("--os-version=", null)]
    [InlineData("--os-version 10.0", "10.0")]
    public void ParsedOsVersion_PreservesOmittedEmptyAndExplicitValues(string? option, string? expectedVersion)
    {
        PlatformOptionsBase options = new();
        Command command = new("platform");
        options.SetCommandOptions(command);
        var parsed = command.Parse("--os linux --arch amd64" + (option is null ? "" : " " + option));
        Assert.Empty(parsed.Errors);
        options.SetParseResult(parsed);

        var platform = options.ResolvePlatform(Settings(
            new AppSettings { Platform = new() { OsVersion = "10.0.20348.1" } }));

        Assert.Equal(expectedVersion, platform.OsVersion);
        Assert.Equal("linux", platform.Os);
        Assert.Equal("amd64", platform.Architecture);
    }

    [Theory]
    [InlineData("--os-version")]
    [InlineData("--os-version --os linux")]
    public void ParsedOsVersion_StillRejectsMissingValue(string arguments)
    {
        PlatformOptionsBase options = new();
        Command command = new("platform");
        options.SetCommandOptions(command);

        var parsed = command.Parse(arguments);

        Assert.Contains(parsed.Errors, error => error.Message.Contains("Required argument missing"));
    }

    private static IAppSettingsStore Settings(AppSettings? settings = null) =>
        new TestAppSettingsStore(settings ?? new AppSettings(), settingsPath: "");

    private static PlatformOptionsBase HostOptions(string host = "http://localhost:2375") => new() { DockerHost = host };

    public void Dispose()
    {
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    internal sealed class FakeEngine : IDockerImageEngine
    {
        private readonly DockerArchiveFixture fixture;

        public FakeEngine(DockerArchiveFixture fixture)
        {
            this.fixture = fixture;
            ImageId = fixture.Id;
        }

        public List<string> Inspections { get; } = [];
        public List<string> Exports { get; } = [];
        public string DaemonId { get; init; } = "daemon";
        public string ImageId { get; init; }
        public string Os { get; init; } = "linux";
        public string Architecture { get; init; } = "amd64";
        public string? Variant { get; init; }
        public string? OsVersion { get; init; }
        public bool FailExport { get; init; }
        public bool Disposed { get; private set; }

        public Task<string> GetDaemonIdentityAsync(CancellationToken cancellationToken) => Task.FromResult(DaemonId);

        public Task<ImageInspectResponse> InspectAsync(string reference, CancellationToken cancellationToken)
        {
            Inspections.Add(reference);
            return Task.FromResult(new ImageInspectResponse
            {
                ID = ImageId, Os = Os, Architecture = Architecture, Variant = Variant, OsVersion = OsVersion
            });
        }

        public Task<Stream> SaveAsync(string imageId, CancellationToken cancellationToken)
        {
            Exports.Add(imageId);
            if (FailExport)
            {
                throw new InvalidOperationException("The pinned image was removed.");
            }
            return Task.FromResult<Stream>(new MemoryStream(fixture.Export));
        }

        public void Dispose() => Disposed = true;
    }
}
