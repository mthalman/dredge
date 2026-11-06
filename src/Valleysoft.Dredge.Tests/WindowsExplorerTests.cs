using System.Formats.Tar;
using System.Text;
using Valleysoft.DockerRegistryClient.Models.Manifests;
using Valleysoft.DockerRegistryClient.Models.Manifests.Docker;
using Valleysoft.Dredge.Commands;
using Valleysoft.Dredge.Explorer;
using Valleysoft.Dredge.Explorer.Tui;
using static Valleysoft.Dredge.Tests.WindowsImageTestData;

namespace Valleysoft.Dredge.Tests;

[Collection(ExplorerUiCollection.Name)]
public sealed class WindowsExplorerTests : IAsyncDisposable
{
    private readonly LayerCacheTestContext cache = new();
    private CancellationToken Token => TestContext.Current.CancellationToken;
    public ValueTask DisposeAsync() => cache.DisposeAsync();

    private async Task<(ExplorerSource Source, ExplorerSession Session)> Load(
        Mock<IDockerRegistryClient> client)
    {
        ExplorerSource source = await ExplorerSource.OpenAsync(client.Object,
            Mock.Of<IDockerRegistryClientFactory>(), Name, new(), null, Token);
        ExplorerSession session = await ExplorerSession.CreateAsync(client.Object, source, cache.Store, null, Token);
        await session.EnsurePackagesAsync(Token);
        return (source, session);
    }

    [Fact]
    public async Task ComparesAcrossWindowsVersionsWithoutCaseOnlyDeleteAddPairs()
    {
        (_, ExplorerSession baseline) = await Load(Client("windows", "10.0.20348.1",
            Layer(File("Files/App/Kept", "same"), File("Files/App/Edited", "old"), File("Files/App/Gone", "gone"))));
        (_, ExplorerSession target) = await Load(Client("windows", "10.0.26100.1",
            Layer(File("Files/app/kept", "same"), File("Files/app/EDITED", "new"), File("Files/app/Fresh", "fresh"))));
        await using ImageFileSystem before = baseline.Files;
        await using ImageFileSystem after = target.Files;
        ExplorerComparison comparison = ExplorerSession.Compare(baseline, target);
        Assert.Equal(
            [("App/Edited", LayerChangeKind.Modified), ("app/Fresh", LayerChangeKind.Added), ("App/Gone", LayerChangeKind.Deleted)],
            comparison.Files.Where(static file => file.Baseline?.Type != ImageFileType.Directory &&
                file.Target?.Type != ImageFileType.Directory).Select(static file => (file.Path, file.Kind)));
        Assert.DoesNotContain(comparison.Files, static file => file.Path.EndsWith("kept", StringComparison.OrdinalIgnoreCase));
        Assert.All(baseline.Packages.Ecosystems.Values, static ecosystem =>
            Assert.Equal(InstalledPackageMetadataAvailability.Unavailable, ecosystem.Availability));
        Assert.Contains(baseline.Packages.Diagnostics, static diagnostic => diagnostic.Message.Contains("Windows"));
    }

    [Fact]
    public async Task MetadataChangesAreNotSilentlyReportedAsIdentical()
    {
        (_, ExplorerSession before) = await Load(Client(Layer(File("Files/value", "same",
            new() { ["MSWINDOWS.fileattr"] = "32" }))));
        (_, ExplorerSession after) = await Load(Client(Layer(File("Files/value", "same",
            new() { ["MSWINDOWS.fileattr"] = "33" }))));
        await using ImageFileSystem first = before.Files;
        await using ImageFileSystem second = after.Files;
        Assert.Equal(LayerChangeKind.Modified, Assert.Single(ExplorerSession.Compare(before, after).Files).Kind);
    }

    [Fact]
    public async Task WindowsComparisonsIgnoreUnixMetadataAndDiscloseUnsupportedEntries()
    {
        PaxTarEntry original = File("Files/value", "same");
        PaxTarEntry changed = File("Files/value", "same");
        changed.Uid = 42;
        changed.Gid = 43;
        changed.Mode = UnixFileMode.UserRead;
        (_, ExplorerSession before) = await Load(Client(Layer(original,
            File("Files/stream:secret", "before"))));
        (_, ExplorerSession after) = await Load(Client(Layer(changed,
            File("Files/stream:secret", "after"))));
        await using ImageFileSystem first = before.Files;
        await using ImageFileSystem second = after.Files;
        ExplorerComparison comparison = ExplorerSession.Compare(before, after);
        Assert.Empty(comparison.Files);
        Assert.Contains("stream:secret", Assert.Single(comparison.Limitations));
    }

    [Fact]
    public async Task TreesSearchAndHistoryShareCaseInsensitiveIdentity()
    {
        Mock<IDockerRegistryClient> client = Client(
            Layer(File("Files/App/Value.txt", "old")),
            Layer(File("Files/app/VALUE.txt", "new"), File("Files/app/New.txt", "new")));
        (ExplorerSource source, ExplorerSession session) = await Load(client);
        await using ImageFileSystem files = session.Files;
        ExplorerImage image = ExplorerImage.FromSource(source);
        image.SetSession(session, ExplorerInsightsResult.Empty);
        Assert.Contains("--os-version", image.PlatformArguments);
        Assert.Single(image.WholeTree(1)!);
        Node node = Assert.IsType<Node>(image.Find(image.WholeTree(1)!, "APP/value.TXT"));
        Assert.Contains("Windows attributes", node.Mode);
        Assert.Equal("ACL/owner not interpreted", node.Owner);
        Assert.Equal([(0, Change.Added), (1, Change.Modified)], image.PathHistory("APP/value.TXT"));
        Assert.Equal("old", await Read(files.CreateLayerSnapshot(0, Token), "app/value.txt"));
    }

    private async Task<string> Read(ImageFileSystem files, string path)
    {
        await using (files)
        {
            using MemoryStream output = new();
            await files.CopyFileToAsync(path, output, Token);
            return Encoding.UTF8.GetString(output.ToArray());
        }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void PreviewsBomMarkedUtf16WithBoundedTruncation(bool bigEndian, bool truncated)
    {
        UnicodeEncoding encoding = new(bigEndian, true, true);
        byte[] bytes = [.. encoding.GetPreamble(), .. encoding.GetBytes("Windows\r\ntext")];
        if (truncated)
        {
            bytes = [.. bytes, 0x41];
        }
        (List<string>? lines, string? message, _) = ExplorerHost.Decode(bytes, truncated);
        Assert.Equal(["Windows", "text"], lines);
        Assert.Equal(truncated, message is not null);
    }

    [Fact]
    public void Utf16PreviewRejectsMalformedSurrogatesAndTrimsTruncatedOnes()
    {
        Assert.Null(ExplorerHost.Decode([0xff, 0xfe, 0x00, 0xd8], false).Lines);
        Assert.Equal(["a"], ExplorerHost.Decode([0xff, 0xfe, 0x61, 0x00, 0x00, 0xd8], true).Lines);
        Assert.Null(ExplorerHost.Decode([0xff, 0xfe, 0x00, 0x00], false).Lines);
    }

    [Fact]
    public async Task ExplorerLoadsWindowsLayersAndShowsMetadataLimitations()
    {
        Mock<IDockerRegistryClient> client = Client(Layer(File("Files/App/value.txt", "hello Windows")));
        ExplorerSource source = await ExplorerSource.OpenAsync(client.Object, Mock.Of<IDockerRegistryClientFactory>(),
            Name, new(), null, Token);
        ExplorerOptions options = new(null, null, false, ClipboardMode.Off, "unused", "");
        await using ExplorerApp explorer = new(client.Object, Mock.Of<IDockerRegistryClientFactory>(),
            source, cache.Store, options, Token);
        ExplorerState state = explorer.Start();
        using ExplorerUiHarness ui = new(150, 42, app => explorer.Attach(app, state));
        ui.Until(() => explorer.Image.Complete || explorer.Image.SessionError is not null, "Windows filesystem indexing");
        Assert.Null(explorer.Image.SessionError);
        Assert.True(explorer.Image.Complete);
        Assert.Contains("ACLs", explorer.Image.BaseWarning);
        PreviewContent preview = ui.Wait(() => explorer.Host.PreviewAsync("APP/VALUE.TXT", 0, Token), "Windows content preview");
        Assert.Equal(["hello Windows"], preview.Lines);
        Assert.Single(explorer.Image.WholeTree(0)!);
        state.Expanded.Add("app");
        Assert.Contains("App/value.txt", new ExplorerPresenter(explorer.Image, 150, 42).Flatten(state)
            .Select(static row => row.Path));
        ui.Press(Terminal.Gui.Input.Key.W.WithAlt);
        Assert.Equal(RightView.Warning, state.View);
        ui.Press(Terminal.Gui.Input.Key.W.WithAlt);
        ui.Press(Terminal.Gui.Input.Key.Esc);
        Assert.Equal(RightView.Files, state.View);
        ExplorerPlatform[] choices = [new("windows", "amd64", null, "10.0.20348.2"),
            new("windows", "amd64", null, "10.0.26100.1")];
        ExplorerPlatform? chosen = null;
        Assert.True(ui.AnswerDialog(() => chosen = ui.Wait(
            () => explorer.Host.ChoosePlatformAsync!(choices, Token), "comparison version selection"),
            Terminal.Gui.Input.Key.CursorDown, Terminal.Gui.Input.Key.Enter));
        Assert.Equal(choices[1], chosen);
        Assert.True(ui.AnswerDialog(() => chosen = ui.Wait(
            () => explorer.Host.ChoosePlatformAsync!(choices, Token), "comparison version cancellation"),
            Terminal.Gui.Input.Key.Esc));
        Assert.Null(chosen);
    }

    [Theory]
    [InlineData("10.0.20348.1", "10.0.20348.1")]
    [InlineData("10.0.20348.2", "10.0.20348.2")]
    public async Task WindowsComparisonPrefersExactVersionOrUniqueCompatibleVersion(string available, string expected)
    {
        Mock<IDockerRegistryClient> client = PlatformClient(available);
        (_, _, ExplorerPlatform? platform) = await ExplorerSource.ResolveAsync(client.Object, Name,
            new() { OsVersion = "10.0.20348.1" }, new("windows", "amd64", null, "10.0.20348.1"), Token,
            comparing: true);
        Assert.Equal(expected, platform!.OsVersion);
    }

    [Fact]
    public async Task WindowsComparisonPromptsForAmbiguousVersionsAndHonorsCancellation()
    {
        Mock<IDockerRegistryClient> client = PlatformClient("10.0.20348.2", "10.0.26100.1");
        ExplorerPlatform baseline = new("windows", "amd64", null, "10.0.20348.1");
        IReadOnlyList<ExplorerPlatform>? offered = null;
        (_, _, ExplorerPlatform? platform) = await ExplorerSource.ResolveAsync(client.Object, Name, new(),
            baseline, Token, comparing: true, chooseComparisonPlatform: (choices, _) =>
            {
                offered = choices;
                return Task.FromResult<ExplorerPlatform?>(choices[1]);
            });
        Assert.Equal(2, offered!.Count);
        Assert.Equal("10.0.26100.1", platform!.OsVersion);
        await Assert.ThrowsAsync<OperationCanceledException>(() => ExplorerSource.ResolveAsync(client.Object,
            Name, new(), baseline, Token, comparing: true,
            chooseComparisonPlatform: (_, _) => Task.FromResult<ExplorerPlatform?>(null)));
    }

    [Fact]
    public async Task InitialVersionSelectionDoesNotFallBackToOtherVersions()
    {
        Mock<IDockerRegistryClient> client = PlatformClient("10.0.20348.2");
        await Assert.ThrowsAsync<InvalidOperationException>(() => ExplorerSource.ResolveAsync(client.Object, Name,
            new() { Os = "windows", OsVersion = "10.0.20348.1" }, null, Token,
            settingsStore: new TestAppSettingsStore(new())));
    }

    [Theory]
    [InlineData(false, "10.0.20348.1")]
    [InlineData(true, "10.0.20348.1")]
    [InlineData(false, "10.0.26100.1")]
    [InlineData(true, "10.0.26100.1")]
    public async Task HostComparisonOpensAndRetainsSelectedWindowsVersion(bool describeFirst, string targetVersion)
    {
        Mock<IDockerRegistryClient> client = Client(Layer(File("Files/App/value", "before")));
        AddComparisonTarget(client, [targetVersion]);
        await using ExplorerHost host = await CreateHost(client);
        if (describeFirst)
        {
            await host.DescribeTagAsync(new TagChoice("next"), Token);
        }

        using RetainedExplorerComparison result = await host.CompareRetainedAsync("next", () => { }, Token);

        Assert.Equal(targetVersion, result.Comparison.Target.Platform!.OsVersion);
        Assert.Equal(targetVersion, result.Comparison.Target.Config.OsVersion);
        Assert.Equal(LayerChangeKind.Modified,
            Assert.Single(result.Comparison.Files, static file => file.Path == "App/value").Kind);
        using RetainedExplorerComparison repeated = await host.CompareRetainedAsync("next", () => { }, Token);
        Assert.Same(result.Comparison.Target, repeated.Comparison.Target);
        client.Verify(item => item.Manifests.GetAsync(Name.Repo, "next", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task HostComparisonOffersAmbiguousVersionsAfterNoninteractiveTagDescription()
    {
        Mock<IDockerRegistryClient> client = Client(Layer(File("Files/App/value", "before")));
        string[] targetDigests = AddComparisonTarget(client, ["10.0.20348.2", "10.0.26100.1"]);
        await using ExplorerHost host = await CreateHost(client);
        IReadOnlyList<ExplorerPlatform>? offered = null;
        host.ChoosePlatformAsync = (choices, _) =>
        {
            offered = choices;
            return Task.FromResult<ExplorerPlatform?>(choices[1]);
        };
        await Assert.ThrowsAsync<InvalidOperationException>(() => host.DescribeTagAsync(new TagChoice("next"), Token));
        Assert.Null(offered);

        using RetainedExplorerComparison result = await host.CompareRetainedAsync("next", () => { }, Token);

        Assert.NotNull(offered);
        Assert.Equal(["10.0.20348.2", "10.0.26100.1"], offered.Select(static platform => platform.OsVersion));
        Assert.Equal(offered[1], result.Comparison.Target.Platform);
        Assert.Equal(targetDigests[1], result.Comparison.Target.Resolved.ManifestInfo.DockerContentDigest);
    }

    [Fact]
    public async Task HostComparisonCancellationDoesNotCacheAnUnchosenVersion()
    {
        Mock<IDockerRegistryClient> client = Client(Layer(File("Files/App/value", "before")));
        string[] targetDigests = AddComparisonTarget(client, ["10.0.20348.2", "10.0.26100.1"]);
        await using ExplorerHost host = await CreateHost(client);
        host.ChoosePlatformAsync = (_, _) => Task.FromResult<ExplorerPlatform?>(null);
        await Assert.ThrowsAsync<OperationCanceledException>(() => host.CompareRetainedAsync("next", () => { }, Token));
        client.Verify(item => item.Manifests.GetAsync(Name.Repo,
            It.Is<string>(digest => targetDigests.Contains(digest)), It.IsAny<CancellationToken>()), Times.Never);
        host.ChoosePlatformAsync = (choices, _) => Task.FromResult<ExplorerPlatform?>(choices[0]);

        using RetainedExplorerComparison result = await host.CompareRetainedAsync("next", () => { }, Token);

        Assert.Equal("10.0.20348.2", result.Comparison.Target.Platform!.OsVersion);
        client.Verify(item => item.Manifests.GetAsync(Name.Repo, "next", It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OpenPathAllowsOnlyComparisonToSelectAnAlternateWindowsVersion(bool comparing)
    {
        Mock<IDockerRegistryClient> client = Client();
        AddComparisonTarget(client, ["10.0.26100.1"]);
        ExplorerPlatform baseline = new("windows", "amd64", null, "10.0.20348.1");
        ImageName target = ImageName.Parse("registry.test/windows:next");
        if (comparing)
        {
            await using ExplorerSource source = await ExplorerSource.OpenAsync(client.Object,
                Mock.Of<IDockerRegistryClientFactory>(), target, new(), null, Token,
                exactPlatform: baseline, comparing: true);
            Assert.Equal("10.0.26100.1", source.Platform!.OsVersion);
        }
        else
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => ExplorerSource.OpenAsync(client.Object,
                Mock.Of<IDockerRegistryClientFactory>(), target, new(), null, Token, exactPlatform: baseline));
        }
    }

    [Fact]
    public async Task OpenPathRejectsConfigThatDisagreesWithTheSelectedComparisonDescriptor()
    {
        Mock<IDockerRegistryClient> client = Client();
        AddComparisonTarget(client, ["10.0.26100.1"], configVersion: "10.0.20348.2");
        InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ExplorerSource.OpenAsync(client.Object, Mock.Of<IDockerRegistryClientFactory>(),
                ImageName.Parse("registry.test/windows:next"), new(), null, Token,
                exactPlatform: new("windows", "amd64", null, "10.0.20348.1"), comparing: true));
        Assert.Contains("does not match the selected platform", error.Message);
    }

    [Theory]
    [InlineData("", false, true)]
    [InlineData("", true, true)]
    [InlineData("10.0.20348.1", false, true)]
    [InlineData("10.0.26100.1", false, false)]
    [InlineData("10.0.26100.1", true, true)]
    public async Task CachedManifestUsesSuppliedFallbackWithoutOverridingConfiguration(
        string configVersion, bool comparing, bool compatible)
    {
        Mock<IDockerRegistryClient> client = Client();
        string[] digests = AddComparisonTarget(client, ["10.0.20348.1"], configVersion);
        ManifestInfo info = await client.Object.Manifests.GetAsync(Name.Repo, digests[0], Token);
        ResolvedManifest cached = new(info, Assert.IsAssignableFrom<IImageManifest>(info.Manifest));
        ExplorerPlatform fallback = new("windows", "amd64", "v8", "10.0.20348.1");
        Task<ExplorerSource> Open() => ExplorerSource.OpenAsync(client.Object,
            Mock.Of<IDockerRegistryClientFactory>(), ImageName.Parse("registry.test/windows:next"),
            new(), null, Token, exactPlatform: fallback, resolvedManifest: cached, comparing: comparing);
        if (!compatible)
        {
            await Assert.ThrowsAsync<InvalidOperationException>(Open);
            return;
        }

        await using ExplorerSource source = await Open();

        Assert.Equal(configVersion.Length == 0 ? fallback.OsVersion : configVersion, source.Platform!.OsVersion);
        Assert.Equal(fallback.Variant, source.Platform.Variant);
        Assert.Equal(configVersion, source.Config.OsVersion);
        Assert.Null(source.Resolved.SelectedPlatform);
    }

    [Fact]
    public async Task CachedSelectedDescriptorStillRejectsConflictingConfiguration()
    {
        Mock<IDockerRegistryClient> client = Client();
        string[] digests = AddComparisonTarget(client, ["10.0.26100.1"], configVersion: "10.0.20348.2");
        ManifestInfo info = await client.Object.Manifests.GetAsync(Name.Repo, digests[0], Token);
        ExplorerPlatform selected = new("windows", "amd64", null, "10.0.26100.1");
        ResolvedManifest cached = new(info, Assert.IsAssignableFrom<IImageManifest>(info.Manifest), selected);
        await Assert.ThrowsAsync<InvalidOperationException>(() => ExplorerSource.OpenAsync(client.Object,
            Mock.Of<IDockerRegistryClientFactory>(), ImageName.Parse("registry.test/windows:next"),
            new(), null, Token, exactPlatform: new("windows", "amd64", null, "10.0.20348.1"),
            resolvedManifest: cached, comparing: true));
    }

    [Fact]
    public async Task HostComparisonUsesDescriptorVersionWhenConfigOmitsIt()
    {
        Mock<IDockerRegistryClient> client = Client(Layer(File("Files/App/value", "before")));
        AddComparisonTarget(client, ["10.0.26100.1"], configVersion: "");
        await using ExplorerHost host = await CreateHost(client);
        using RetainedExplorerComparison result = await host.CompareRetainedAsync("next", () => { }, Token);
        Assert.Equal("10.0.26100.1", result.Comparison.Target.Platform!.OsVersion);
        Assert.Equal("10.0.26100.1", result.Comparison.Target.Resolved.SelectedPlatform!.OsVersion);
        Assert.Equal("", result.Comparison.Target.Config.OsVersion);
    }

    [Fact]
    public async Task HostComparisonAcceptsDirectWindowsManifestWithAnotherVersion()
    {
        Mock<IDockerRegistryClient> client = Client(Layer(File("Files/App/value", "before")));
        string[] digests = AddComparisonTarget(client, ["10.0.26100.1"]);
        ManifestInfo direct = await client.Object.Manifests.GetAsync(Name.Repo, digests[0], Token);
        client.Setup(item => item.Manifests.GetAsync(Name.Repo, "next", It.IsAny<CancellationToken>()))
            .ReturnsAsync(direct);
        await using ExplorerHost host = await CreateHost(client);

        using RetainedExplorerComparison result = await host.CompareRetainedAsync("next", () => { }, Token);

        Assert.Equal("10.0.26100.1", result.Comparison.Target.Platform!.OsVersion);
        Assert.Equal(digests[0], result.Comparison.Target.Resolved.ManifestInfo.DockerContentDigest);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DaemonOpenPathStillRejectsAnotherWindowsVersion(bool comparing)
    {
        ExplorerPlatform platform = new("windows", "amd64", null, "10.0.26100.1");
        DockerManifest manifest = new() { Layers = [] };
        ImageSourceReference reference = ImageSourceReference.Parse("docker://target");
        await using ResolvedImage image = new(reference, reference.GetImageName(),
            new(new ManifestInfo("application/manifest", "sha256:target", manifest), manifest),
            null, new() { Os = platform.Os, Architecture = platform.Architecture, OsVersion = platform.OsVersion },
            [], new RegistryImageContentSource(Mock.Of<IDockerRegistryClient>(), Name),
            "sha256:target", platform.ToString(), selectedPlatform: platform);
        FixedImageSourceFactory sources = new(image);
        InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ExplorerSource.OpenAsync(sources, Mock.Of<IDockerRegistryClientFactory>(),
                reference.ToString(), new(), null, Token,
                exactPlatform: new("windows", "amd64", null, "10.0.20348.1"), comparing: comparing));
        Assert.Contains("Docker image platform", error.Message);
    }

    private sealed class FixedImageSourceFactory : IImageSourceFactory
    {
        private readonly ResolvedImage image;

        public FixedImageSourceFactory(ResolvedImage image)
        {
            this.image = image;
        }

        public Task<ResolvedImage> OpenAsync(string reference, PlatformOptionsBase options,
            CancellationToken cancellationToken, ImageSourceRequirements requirements = ImageSourceRequirements.LayerContent) =>
            Task.FromResult(image);
    }

    [Fact]
    public async Task RegistryBaseVerificationDoesNotSelectAnotherWindowsVersion()
    {
        Mock<IDockerRegistryClient> client = Client(Layer(File("Files/App/value", "before")));
        AddComparisonTarget(client, ["10.0.26100.1"]);
        await Assert.ThrowsAsync<InvalidOperationException>(() => ExplorerSource.OpenAsync(client.Object,
            Mock.Of<IDockerRegistryClientFactory>(), Name, new(), ["registry.test/windows:next"], Token));
    }

    private async Task<ExplorerHost> CreateHost(Mock<IDockerRegistryClient> client)
    {
        (ExplorerSource source, ExplorerSession session) = await Load(client);
        ExplorerLayerIndexer indexer = ExplorerLayerIndexer.Create(client.Object, source, cache.Store);
        return new(client.Object, Mock.Of<IDockerRegistryClientFactory>(), source, cache.Store,
            ExplorerImage.FromSource(source), indexer, new(null, null, false, ClipboardMode.Off, "", ""))
        {
            Session = session
        };
    }

    private static string[] AddComparisonTarget(Mock<IDockerRegistryClient> client, string[] versions,
        string? configVersion = null)
    {
        string[] digests = [.. versions.Select((version, index) =>
            LayerCacheTestContext.Digest(Encoding.UTF8.GetBytes($"target-{index}:{version}")))];
        ManifestList list = new()
        {
            Manifests = [.. versions.Select((version, index) => new ManifestReference
            {
                Digest = digests[index],
                Platform = new ManifestPlatform { Os = "windows", Architecture = "amd64", OsVersion = version }
            })]
        };
        client.Setup(item => item.Manifests.GetAsync(Name.Repo, "next", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ManifestInfo("application/index", "sha256:target-index", list));
        for (int index = 0; index < versions.Length; index++)
        {
            byte[] layer = Layer(File("Files/App/value", versions[index]));
            string digest = digests[index];
            string configDigest = $"sha256:target-config{index}";
            string blobDigest = LayerCacheTestContext.Digest(layer);
            DockerManifest manifest = new()
            {
                Config = new ManifestConfig { Digest = configDigest },
                Layers = [new ManifestLayer
                {
                    Digest = blobDigest, Size = layer.Length,
                    MediaType = "application/vnd.docker.image.rootfs.diff.tar.gzip"
                }]
            };
            string version = configVersion ?? versions[index];
            client.Setup(item => item.Manifests.GetAsync(Name.Repo, digest, It.IsAny<CancellationToken>()))
                .ReturnsAsync(new ManifestInfo("application/manifest", digest, manifest));
            client.Setup(item => item.Blobs.GetAsync(Name.Repo, configDigest, It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => new MemoryStream(Encoding.UTF8.GetBytes(
                    $"{{\"os\":\"windows\",\"architecture\":\"amd64\",\"os.version\":\"{version}\"}}")));
            client.Setup(item => item.Blobs.GetRangeAsync(Name.Repo, blobDigest, 0,
                It.IsAny<long?>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => new Valleysoft.DockerRegistryClient.BlobDownloadResult(
                    new MemoryStream(layer), false, null, null, layer.Length));
        }
        return digests;
    }

    private static Mock<IDockerRegistryClient> PlatformClient(params string[] versions)
    {
        Mock<IDockerRegistryClient> client = new() { DefaultValue = DefaultValue.Mock };
        ManifestList list = new()
        {
            Manifests = [.. versions.Select((version, index) => new ManifestReference
            {
                Digest = $"sha256:platform{index}",
                Platform = new ManifestPlatform { Os = "windows", Architecture = "amd64", OsVersion = version }
            })]
        };
        client.Setup(item => item.Manifests.GetAsync(Name.Repo, Name.Tag!, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ManifestInfo("application/index", "sha256:index", list));
        for (int index = 0; index < versions.Length; index++)
        {
            string digest = $"sha256:platform{index}";
            client.Setup(item => item.Manifests.GetAsync(Name.Repo, digest, It.IsAny<CancellationToken>()))
                .ReturnsAsync(new ManifestInfo("application/manifest", digest, new DockerManifest { Layers = [] }));
        }
        return client;
    }
}
