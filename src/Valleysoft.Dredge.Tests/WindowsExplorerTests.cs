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
