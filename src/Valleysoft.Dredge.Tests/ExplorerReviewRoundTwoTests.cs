using System.Collections;
using Terminal.Gui.Input;
using Valleysoft.Dredge.Explorer;
using Valleysoft.Dredge.Explorer.Tui;
using static Valleysoft.Dredge.Tests.WindowsImageTestData;

namespace Valleysoft.Dredge.Tests;

[Collection(ExplorerUiCollection.Name)]
public sealed class ExplorerReviewRoundTwoTests : IAsyncDisposable
{
    private readonly string root = Path.Combine(Directory.GetCurrentDirectory(), "explorer-review-two-" + Guid.NewGuid().ToString("N"));
    private readonly List<LayerStore> stores = [];
    private CancellationToken Token => TestContext.Current.CancellationToken;
    public async ValueTask DisposeAsync()
    {
        foreach (LayerStore store in stores) await store.DisposeAsync();
        if (Directory.Exists(root)) Directory.Delete(root, true);
    }

    private async Task<(ExplorerImage Image, ExplorerHost Host)> Load(params byte[][] layers)
    {
        Mock<IDockerRegistryClient> client = Client(layers);
        LayerStore store = new(Path.Combine(root, stores.Count.ToString()));
        stores.Add(store);
        ExplorerSource source = await ExplorerSource.OpenAsync(client.Object,
            Mock.Of<IDockerRegistryClientFactory>(), Name, new(), null, Token);
        ExplorerSession session = await ExplorerSession.CreateAsync(client.Object, source, store, null, Token);
        await session.EnsurePackagesAsync(Token);
        ExplorerImage image = ExplorerImage.FromSource(source);
        image.SetSession(session, ExplorerInsightsResult.Empty);
        ExplorerHost host = new(client.Object, Mock.Of<IDockerRegistryClientFactory>(), source, store,
            image, new ExplorerLayerIndexer(0, static (_, _, _) => throw new InvalidOperationException()),
            new(null, null, false, ClipboardMode.Off, "", ""), root) { Session = session };
        return (image, host);
    }

    [Fact]
    public async Task WindowsHistoricalPreviewShowsWindowsMetadataInsteadOfUnixOwnership()
    {
        var (image, host) = await Load(Layer(File("Files/value", "same")));
        await using (host)
        {
            PreviewContent preview = await host.PreviewVersionAsync("value", 0, Token);
            ExplorerState state = new()
            {
                View = RightView.History, InspectPath = "value", HistoryLayer = 0, HistoryPreview = preview
            };
            string text = string.Join("\n", new ExplorerPresenter(image, 150, 42).RightPane(state).Lines);
            Assert.Contains("attributes 0x20", text);
            Assert.DoesNotContain("mode ", text);
            Assert.DoesNotContain("0:0", text);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WindowsHistoricalDiffReportsAttributeAndFingerprintOnlyChanges(bool fingerprintOnly)
    {
        var (image, host) = await Load(
            Layer(File("Files/value", "same", new() { ["MSWINDOWS.fileattr"] = "32" })),
            Layer(File("Files/value", "same", fingerprintOnly
                ? new() { ["MSWINDOWS.fileattr"] = "32", ["MSWINDOWS.rawsd"] = "changed" }
                : new() { ["MSWINDOWS.fileattr"] = "33" })));
        await using (host)
        {
            TextDiffContent diff = await host.DiffVersionAsync("value", 0, 1, Token);
            Assert.Equal(DiffOp.Same, Assert.Single(diff.Lines!).Op);
            Assert.Contains("Metadata:", diff.Message);
            Assert.Contains(fingerprintOnly ? "fingerprint" : "attributes 0x21", diff.Message);
            Assert.DoesNotContain("mode ", diff.Message);
            Assert.DoesNotContain("0:0", diff.Message);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WindowsComparisonReportsUnavailablePackagesWithoutInventedCounts(bool overview)
    {
        var (before, beforeHost) = await Load(Layer(File("Files/value", "before")));
        var (after, afterHost) = await Load(Layer(File("Files/value", "after")));
        await using (beforeHost)
        await using (afterHost)
        {
            CompareState state = new(ExplorerSession.Compare(before.Session!, after.Session!), "before", "after")
            {
                Overview = overview
            };
            CompareView view = new(new ExplorerPresenter(before, 150, 42), state);
            string text = string.Join("\n", view.Diff().Lines);
            Assert.Contains("unavailable", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("No package database found", text);
            Assert.DoesNotContain("0 changed packages", text);
            Assert.DoesNotContain("0 updated", text);
            Assert.Null(view.Rows().Single(row => row.Key == "section:packages").Files);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AdvertisedWindowsWarningsOpenFromFocusedSearchAndCommandFields(bool command)
    {
        var (image, host) = await Load(Layer(File("Files/value", "same")));
        await using (host)
        using (ExplorerUiHarness ui = ExplorerWindowTests.Open(image, new() { Layer = 0 },
            session => new FakeExplorerHost { Baseline = session }, out _))
        {
            ui.Window.Apply(command ? new CopyCommand() : new ShowView(RightView.Search));
            Assert.Equal(command ? RightView.Command : RightView.Search, ui.State.View);
            Assert.True(command ? ui.Window.CommandText.HasFocus : ui.Window.Search.HasFocus);
            Assert.Contains(ui.Window.Presenter.Hints(ui.State), hint => hint.Key == "Alt+W");

            ui.Press(Key.W.WithAlt);

            Assert.Equal(RightView.Warning, ui.State.View);
            Assert.Contains("Windows content-only", ui.State.WarningText);
            ui.Press(Key.Esc);
            Assert.Equal(command ? RightView.Command : RightView.Search, ui.State.View);
        }
    }

    [Fact]
    public async Task ComparisonWarningCountsDoNotMaterializeFilesystemDiagnostics()
    {
        var (image, host) = await Load(Layer(File("Files/value", "same")));
        await using (host)
        {
            CompareView view = new(new ExplorerPresenter(image, 150, 42),
                new(ExplorerSession.Compare(image.Session!, image.Session!), "before", "after"));
            int expected = view.WarningCount;
            Assert.True(expected > 0);
            for (int i = 0; i < 10; i++) Assert.Equal(expected, view.WarningCount);
            long start = GC.GetAllocatedBytesForCurrentThread();
            int total = 0;
            for (int i = 0; i < 100; i++) total += view.WarningCount;
            long allocated = GC.GetAllocatedBytesForCurrentThread() - start;
            Assert.Equal(expected * 100, total);
            Assert.Equal(0, allocated);
        }
    }

    [Fact]
    public void WarningPresenceChecksDoNotEnumerateOrFormatLayerDiagnostics()
    {
        CountingDiagnostics diagnostics = new();
        ExplorerImage image = new(Name.ToString(), "windows/amd64", "digest", ["layer"], [1], null);
        image.SetIndexed(0, new([], [], []) { Diagnostics = diagnostics });
        using ExplorerUiHarness ui = ExplorerWindowTests.Open(image, new() { View = RightView.Insights },
            _ => new FakeExplorerHost(), out _);
        for (int i = 0; i < 3; i++)
        {
            ui.Window.Presenter.Hints(ui.State);
            ui.Window.Presenter.RightPane(ui.State);
        }
        Assert.Equal(0, diagnostics.Enumerations);
        ui.Window.Apply(new ShowView(RightView.Warning));
        Assert.Contains("Layer 0: diagnostic value", ui.State.WarningText);
        Assert.Equal(1, diagnostics.Enumerations);
    }

    [Fact]
    public void WarningCountsReflectReplacedLayerIndexesWithoutCachedState()
    {
        ExplorerImage image = new(Name.ToString(), "linux/amd64", "digest", ["layer"], [1], null);
        Assert.False(image.HasWarnings);
        Assert.Equal(0, image.WarningCount);
        image.SetIndexed(0, new([], [], []) { Diagnostics = [new("value", "diagnostic")] });
        Assert.True(image.HasWarnings);
        Assert.Equal(1, image.WarningCount);
        image.SetIndexed(0, new([], [], []));
        Assert.False(image.HasWarnings);
        Assert.Equal(0, image.WarningCount);
    }

    private sealed class CountingDiagnostics : IReadOnlyList<LayerDiagnostic>
    {
        public int Enumerations { get; private set; }
        public int Count => 1;
        public LayerDiagnostic this[int index] => new("value", "diagnostic");
        public IEnumerator<LayerDiagnostic> GetEnumerator()
        {
            Enumerations++;
            yield return this[0];
        }
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
