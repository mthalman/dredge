using Valleysoft.Dredge.Explorer;

namespace Valleysoft.Dredge.Tests;

[Collection(ExplorerUiCollection.Name)]
public sealed class ExplorerComparisonPerformanceTests
{
    [Fact]
    public void NavigationDoesNotRebuildLargeComparisonTrees()
    {
        ExplorerImage image = ExplorerSamples.Custom(
            [ExplorerSamples.Layer([.. Enumerable.Range(0, 20_000)
                .Select(static i => ExplorerSamples.File($"app/f{i:D5}", 64, $"h{i}"))])]);
        ExplorerSession empty = ExplorerSamples.Session(["empty"], [ExplorerSamples.Layer([])], []);
        CompareState compare = new(ExplorerSession.Compare(empty, image.Session!), "before", "after");
        compare.Expanded.Add("file:app");
        ExplorerState state = new() { Compare = compare };
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
        Assert.True(bytes < 20_000_000, $"Ten comparison moves allocated {bytes:N0} bytes.");
        Assert.Equal(11, compare.Cursor);
    }

    [Fact]
    public void RetainedViewUpdatesForStructuralChanges()
    {
        ExplorerImage image = ExplorerSamples.Image();
        ExplorerComparison comparison = ExplorerSession.Compare(image.Session!, ExplorerSamples.Target());
        CompareState state = new(comparison, "before", "after");
        ExplorerPresenter presenter = new(image, 150, 42);
        CompareView view = new(presenter, state);

        Assert.DoesNotContain(view.Rows(), static row => row.Path == "app/package.json");
        state.Expanded.Add("file:app");
        Assert.Contains(view.Rows(), static row => row.Path == "app/package.json");
        state.SearchQuery = "package.json";
        Assert.Equal("app/package.json", Assert.Single(view.Rows()).Path);
        state.SearchQuery = "";
        state.Expanded.Remove("file:app");
        Assert.DoesNotContain(view.Rows(), static row => row.Path == "app/package.json");

        state.Overview = true;
        Assert.Equal(2, view.Rows().Count);
        state.Overview = false;
        state.Searching = true;
        state.SearchQuery = "package.json";
        CompareRow beforeSwap = Assert.Single(view.Rows());
        state.Comparison = ExplorerSession.Compare(comparison.Target, comparison.Baseline);
        CompareRow afterSwap = Assert.Single(view.Rows());
        Assert.Equal(beforeSwap.Before, afterSwap.After);
        Assert.Equal(beforeSwap.After, afterSwap.Before);

        ExplorerPackageDifference package = comparison.Packages[0];
        state.SearchQuery = "";
        state.PackageFiles = new(package, [("one", Change.Added)], null, 1, ["first warning"]);
        Assert.Equal("one", Assert.Single(view.Rows()).Path);
        Assert.Contains("first warning", view.Warnings());
        state.PackageFiles = new(package, [("two", Change.Removed)], null, 1, ["second warning"]);
        Assert.Equal("two", Assert.Single(view.Rows()).Path);
        Assert.DoesNotContain("first warning", view.Warnings());
        Assert.Contains("second warning", view.Warnings());

        presenter.Width = 80;
        presenter.Height = 24;
        Assert.All(view.Header(), static line => Assert.True(line.Length <= 80));
        Assert.All(view.Diff().Lines, line => Assert.True(line.Length <= presenter.RightInner));
    }
}
