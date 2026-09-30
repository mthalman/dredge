using Valleysoft.Dredge.Explorer;
using static Valleysoft.Dredge.Tests.ExplorerSamples;

namespace Valleysoft.Dredge.Tests;

[Collection(ExplorerUiCollection.Name)]
public sealed class ExplorerNavigationPerformanceTests
{
    [Theory]
    [InlineData("f", 100_000, false)]
    [InlineData("f099", 1_000, false)]
    [InlineData("f", 100_000, true)]
    public void SearchNavigationDoesNotRescanUnchangedImages(string query, int total, bool layerOnly)
    {
        ExplorerImage image = Custom([Layer([.. Enumerable.Range(0, 100_000)
            .Select(static i => File($"app/f{i:D6}", 64, $"h{i}"))])]);
        ExplorerState state = new() { View = RightView.Search, SearchQuery = query, SearchLayerOnly = layerOnly };
        using ExplorerUiHarness ui = ExplorerWindowTests.Open(image, state,
            static session => new FakeExplorerHost { Baseline = session }, out _);
        ui.Window.Apply(new Move(1));
        ui.Pump();
        long start = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 10; i++)
        {
            ui.Window.Apply(new Move(1));
            ui.Pump();
        }
        long bytes = GC.GetAllocatedBytesForCurrentThread() - start;
        Assert.True(bytes < 20_000_000, $"Ten search moves allocated {bytes:N0} bytes.");
        Assert.Equal(11, state.SearchCursor);
        Assert.Equal(total, ui.Window.Presenter.SearchResults(state).Total);
    }

    [Fact]
    public void PackageNavigationDoesNotRebuildUnchangedInventories()
    {
        ExplorerImage image = Custom([Layer([])]);
        ExplorerSession session = Session(["layer"], [Layer([])],
            Enumerable.Range(0, 20_000).ToDictionary(static i => $"package-{i:D6}", static _ => "1.0.0"));
        ExplorerState state = new() { View = RightView.Packages, Packages = session.Packages, PackagesLayer = 0 };
        using ExplorerUiHarness ui = ExplorerWindowTests.Open(image, state,
            static baseline => new FakeExplorerHost { Baseline = baseline }, out _);
        ui.Window.Apply(new Move(1));
        ui.Pump();
        long start = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 10; i++)
        {
            ui.Window.Apply(new Move(1));
            ui.Pump();
        }
        long bytes = GC.GetAllocatedBytesForCurrentThread() - start;
        Assert.True(bytes < 20_000_000, $"Ten package moves allocated {bytes:N0} bytes.");
        Assert.Equal(11, state.PackageCursor);
        Assert.Equal(20_001, ui.Window.Presenter.PackageRows(state).Count);
    }

    [Fact]
    public void SearchCacheFollowsQueriesFiltersLayersIndexesAndAnalysis()
    {
        LayerChanges first = Layer([File("app/Alpha", 1, "a"), File("app/gone", 2, "gone")]);
        LayerChanges second = Layer([File("app/beta", 3, "b")], whiteouts: ["app/gone"]);
        ExplorerImage image = Custom([first, second, Layer([])]);
        image.SetAnalysis(ImageAnalysis.Analyze([first, second]), ExplorerInsightsResult.Empty);
        ExplorerPresenter presenter = new(image, 150, 42);
        ExplorerState state = new() { SearchQuery = "app/" };
        string[] Paths() => [.. presenter.SearchResults(state).Hits.Select(static hit => hit.Path)];

        Assert.Equal(["app/Alpha", "app/beta", "app/gone"], Paths());
        state.SearchIncludeDeleted = false;
        Assert.Equal(["app/Alpha", "app/beta"], Paths());
        state.SearchLayerOnly = true;
        Assert.Equal(["app/Alpha"], Paths());
        Assert.Contains("in 2 paths", presenter.SearchPane(state).Lines[0].ToString());
        state.Layer = 2;
        Assert.Contains("in 0 paths", presenter.SearchPane(state).Lines[0].ToString());
        state.Layer = 1;
        Assert.Equal(["app/beta"], Paths());
        state.SearchIncludeDeleted = true;
        Assert.Equal(["app/beta", "app/gone"], Paths());
        state.SearchLayerOnly = false;
        state.SearchQuery = "ALPHA";
        Assert.Equal(["app/Alpha"], Paths());
        state.SearchExactCase = true;
        Assert.Empty(Paths());
        state.SearchQuery = "";
        Assert.Empty(Paths());
        state.SearchQuery = "app/";
        Assert.Equal(3, Paths().Length);

        image.SetIndexed(2, Layer([File("app/raw", 4, "raw")]));
        Assert.Equal(["app/Alpha", "app/beta", "app/gone", "app/raw"], Paths());
        state.SearchLayerOnly = true;
        state.Layer = 2;
        Assert.Contains("in 1 path", presenter.SearchPane(state).Lines[0].ToString());
        image.SetAnalysis(ImageAnalysis.Analyze([Layer([File("app/replacement", 5, "replacement")]),
            Layer([]), Layer([])]), ExplorerInsightsResult.Empty);
        Assert.Contains("in 0 paths", presenter.SearchPane(state).Lines[0].ToString());
        state.SearchLayerOnly = false;
        SearchHit replacement = Assert.Single(presenter.SearchResults(state).Hits);
        Assert.Equal("app/replacement", replacement.Path);
        Assert.Equal(5, replacement.Size);
    }

    [Fact]
    public void PackageCacheFollowsMetadataFiltersAndExpansion()
    {
        ExplorerPresenter presenter = new(Custom([Layer([])]), 150, 42);
        ExplorerState state = new();
        string[] Names() => [.. presenter.PackageRows(state).Where(static row => !row.IsGroup).Select(static row => row.Name)];
        Assert.Empty(Names());
        state.Packages = Session([], [], new() { ["one"] = "1.0", ["two"] = "2.0" }).Packages;
        Assert.Equal(["one", "two"], Names());
        state.CollapsedPackages.Add(InstalledPackageEcosystem.Npm);
        Assert.True(Assert.Single(presenter.PackageRows(state)).IsGroup);
        state.PackageQuery = "ONE";
        Assert.Equal(["one"], Names());
        state.PackageQuery = "2.0";
        Assert.Equal(["two"], Names());
        state.PackageQuery = "npm";
        Assert.Equal(["one", "two"], Names());
        state.PackageQuery = "";
        Assert.Empty(Names());
        state.CollapsedPackages.Clear();
        Assert.Equal(["one", "two"], Names());
        state.Packages = Session([], [], new() { ["three"] = "3.0" }).Packages;
        Assert.Equal("3.0", Assert.Single(presenter.PackageRows(state), static row => !row.IsGroup).Versions);
        Assert.Equal(["three"], Names());
        state.Packages = null;
        Assert.Empty(presenter.PackageRows(state));
    }
}
