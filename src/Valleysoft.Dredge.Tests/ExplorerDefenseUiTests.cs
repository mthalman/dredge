using Terminal.Gui.Input;
using Valleysoft.Dredge.Explorer;

namespace Valleysoft.Dredge.Tests;

[Collection(ExplorerUiCollection.Name)]
public sealed class ExplorerDefenseUiTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FilteredReplacementRetainsVisibleRemovedDescendants(bool whole)
    {
        ExplorerImage image = ReplacementImage();
        ExplorerState state = new() { Layer = 1, WholeFilesystem = whole };
        state.Expanded.UnionWith(["app", "app/item"]);
        state.Hidden.UnionWith([Change.Added, Change.Modified]);
        ExplorerPresenter presenter = new(image, 100, 30);

        List<FlatRow> rows = presenter.Flatten(state);

        Assert.Contains(rows, row => row.Path == "app/item/old" && row.Node.Change == Change.Removed);
        Node replacement = Assert.Single(rows, row => row.Path == "app/item").Node;
        Assert.Equal(Kind.File, replacement.Kind);
        Assert.Equal(2, replacement.Size);
    }

    [Fact]
    public void ComparisonReplacementKeepsOwnIdentitySearchAndDiffAlongsideHistoricalChildren()
    {
        ExplorerImage baseline = ExplorerSamples.Custom(
            [ExplorerSamples.Layer([ExplorerSamples.File("app/item/old", 7, "old")])]);
        ExplorerImage target = ReplacementImage();
        ExplorerComparison comparison = ExplorerSession.Compare(baseline.Session!, target.Session!);
        ExplorerState state = new() { Compare = new CompareState(comparison, "before", "after") };
        CompareState compare = state.Compare;
        compare.Expanded.Add("file:app");
        using ExplorerUiHarness ui = ExplorerWindowTests.Open(target, state,
            session => new FakeExplorerHost { Baseline = session }, out FakeExplorerHost host);
        CompareView view = new(ui.Window.Presenter, compare);
        List<CompareRow> rows = view.Rows();
        CompareRow replacement = Assert.Single(rows, row => row.Path == "app/item");
        ExplorerFileDifference difference = Assert.Single(comparison.Files, file => file.Path == "app/item");
        Assert.Equal(CompareRowKind.File, replacement.Kind);
        Assert.Equal(ExplorerImage.ToChange(difference.Kind), replacement.Change);
        Assert.Equal(difference.Baseline?.Size, replacement.Before);
        Assert.Equal(2, replacement.After);
        Assert.True(replacement.Expandable);
        compare.Cursor = rows.IndexOf(replacement);

        ui.Press(Key.CursorRight);
        Assert.Contains("file:app/item", compare.Expanded);
        rows = new CompareView(ui.Window.Presenter, compare).Rows();
        Assert.Contains(rows, row => row.Path == "app/item/old" && row.Change == Change.Removed);
        CompareRow parent = Assert.Single(rows, row => row.Path == "app");
        Assert.Equal(7, parent.Before);
        Assert.Equal(2, parent.After);
        ui.Press(Key.Enter);
        ui.Until(() => compare.Diff is not null, "replacement file diff");
        Assert.Equal("app/item", Assert.Single(host.Diffed));
        ui.Press(Key.Esc);
        compare.SearchQuery = "app/item";
        rows = new CompareView(ui.Window.Presenter, compare).Rows();
        Assert.Contains(rows, row => row.Path == "app/item" && row.Kind == CompareRowKind.File);
        Assert.Contains(rows, row => row.Path == "app/item/old");
    }

    private static ExplorerImage ReplacementImage() => ExplorerSamples.Custom(
    [
        ExplorerSamples.Layer([ExplorerSamples.File("app/item/old", 7, "old")]),
        ExplorerSamples.Layer([ExplorerSamples.File("app/item", 2, "new")], ["app/item"])
    ]);
}
