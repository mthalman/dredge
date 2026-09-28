using Terminal.Gui.Input;
using Valleysoft.Dredge.Explorer;
using static Valleysoft.Dredge.Tests.ExplorerSamples;

namespace Valleysoft.Dredge.Tests;

[Collection(ExplorerUiCollection.Name)]
public sealed class ExplorerNavigationTests
{
    [Fact]
    public void ShortTerminalGivesFilesRoomAndRestoresTheViewportAfterTabbing()
    {
        ExplorerImage image = Custom([Layer(Enumerable.Range(0, 50)
            .Select(i => File($"file{i:D2}", i, $"h{i}")).ToArray())]);
        using ExplorerUiHarness ui = ExplorerWindowTests.Open(image, new(),
            session => new FakeExplorerHost { Baseline = session }, out _, width: 80, height: 24);
        Assert.True(ui.Window.Presenter.TreeRows >= 10);
        Assert.Equal(1, ui.Window.Layers.Frame.Height);
        Assert.True(ui.Shows("Layer 0"), ui.Screen());
        ui.Window.Apply(new SetCursor(20));
        ui.Pump();
        int scroll = ui.State.Scroll;
        ui.Press(Key.Tab);
        Assert.True(ui.Window.Layers.HasFocus);
        Assert.Equal(9, ui.Window.Layers.Frame.Height);
        ui.Press(Key.Tab);
        Assert.True(ui.Window.Right.HasFocus);
        Assert.Equal(20, ui.State.Cursor);
        Assert.Equal(scroll, ui.State.Scroll);
        Assert.Equal(1, ui.Window.Layers.Frame.Height);
        ui.Resize(100, 40);
        Assert.Equal(9, ui.Window.Layers.Frame.Height);
        ui.Resize(80, 24);
        Assert.Equal(1, ui.Window.Layers.Frame.Height);
        var frame = ui.Window.Layers.FrameToScreen();
        ui.Click(5, frame.Y);
        Assert.True(ui.Window.Layers.HasFocus);
        Assert.Equal(9, ui.Window.Layers.Frame.Height);
    }

    [Fact]
    public void LargestFirstSortsEveryExpandedDirectoryWithoutMovingSelection()
    {
        ExplorerImage image = Custom([Layer([
            File("a/small", 1, "a"), File("z/a", 2, "za"), File("z/big", 100, "zb"),
            File("z/c", 2, "zc"), File("middle", 20, "m")])]);
        ExplorerState state = new();
        state.Expanded.UnionWith(["a", "z"]);
        using ExplorerUiHarness ui = ExplorerWindowTests.Open(image, state,
            session => new FakeExplorerHost { Baseline = session }, out _);
        string[] original = ui.Window.Presenter.Flatten(state).Select(row => row.Path).ToArray();
        ui.Window.Apply(new SetCursor(ui.Window.Presenter.IndexOf(state, "a/small")));
        ui.Press(new Key('s').WithAlt);
        Assert.Equal(["z", "z/big", "z/a", "z/c", "middle", "a", "a/small"],
            ui.Window.Presenter.Flatten(state).Select(row => row.Path));
        Assert.Equal("a/small", ui.Window.Presenter.Flatten(state)[state.Cursor].Path);
        Assert.True(ui.Shows("Largest first"), ui.Screen());
        (int x, int y) = ui.Find("Largest first");
        ui.Click(x + 2, y);
        Assert.Equal(original, ui.Window.Presenter.Flatten(state).Select(row => row.Path));
        Assert.Equal("a/small", ui.Window.Presenter.Flatten(state)[state.Cursor].Path);
        Assert.Equal(["a", "z"], state.Expanded.Order());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SizeSortingCountsSharedFolderContentOnce(bool whole)
    {
        ExplorerImage image = Custom([Layer([
            File("bin/content", 100, "shared"),
            .. Enumerable.Range(0, 3).Select(i => File($"usr/link{i}", 0, "") with
                { Type = ImageFileType.HardLink, LinkTarget = "bin/content" }),
            File("z/content", 150, "other")])]);
        using ExplorerUiHarness ui = ExplorerWindowTests.Open(image, new() { WholeFilesystem = whole },
            session => new FakeExplorerHost { Baseline = session }, out _);
        ui.Press(new Key('s').WithAlt);
        List<FlatRow> rows = ui.Window.Presenter.Flatten(ui.State);
        Assert.Equal(["z", "bin", "usr"], rows.Select(row => row.Path));
        Assert.Equal([150L, 100L, 100L], rows.Select(row => row.Node.Size));
        Assert.Equal(250, Node.TotalSize(ui.Window.Presenter.Tree(ui.State)));
    }

    [Fact]
    public void BackFromSearchDrilldownRestoresTheSelectedResultAndViewport()
    {
        ExplorerImage image = Custom([Layer(Enumerable.Range(0, 60)
            .Select(i => File($"app/file{i:D2}", i + 1, $"h{i}")).ToArray())]);
        using ExplorerUiHarness ui = ExplorerWindowTests.Open(image, new(),
            session => new FakeExplorerHost { Baseline = session }, out _);
        ui.Press(new Key('/'));
        ui.Type("file");
        ui.Window.Apply(new Jump(true));
        ui.Pump();
        int cursor = ui.State.SearchCursor, scroll = ui.State.SearchScroll;
        Assert.True(scroll > 0);
        ui.Press(Key.Enter);
        Assert.Equal(RightView.Files, ui.State.View);
        ui.Press(Key.Enter);
        Assert.Equal(RightView.Inspector, ui.State.View);
        ui.Press(Key.Esc);
        Assert.Equal(RightView.Files, ui.State.View);
        Assert.Contains(ui.Window.Presenter.Hints(ui.State), hint => hint.Key == "Esc" && hint.Label == "Back to search");
        ui.Press(Key.Esc);
        Assert.Equal(RightView.Search, ui.State.View);
        Assert.Equal("file", ui.State.SearchQuery);
        Assert.Equal(cursor, ui.State.SearchCursor);
        Assert.Equal(scroll, ui.State.SearchScroll);
        Assert.True(ui.Window.Search.HasFocus);
    }

    [Fact]
    public void BackFromFindingRestoresLayerFiltersAndFindingSelection()
    {
        using ExplorerUiHarness ui = ExplorerWindowTests.Open(out _);
        ui.State.Hidden.Add(Change.Identical);
        ui.Press(new Key('i'));
        ui.Press(Key.End);
        int layer = ui.State.Layer, finding = ui.State.Finding, scroll = ui.State.FindingScroll;
        string[] expanded = ui.State.Expanded.Order().ToArray();
        ui.Press(Key.Enter);
        Assert.True(ui.State.FindingsOnly);
        ui.Press(Key.Esc);
        Assert.Equal(RightView.Insights, ui.State.View);
        Assert.Equal(layer, ui.State.Layer);
        Assert.Equal(finding, ui.State.Finding);
        Assert.Equal(scroll, ui.State.FindingScroll);
        Assert.False(ui.State.FindingsOnly);
        Assert.Equal([Change.Identical], ui.State.Hidden);
        Assert.Equal(expanded, ui.State.Expanded.Order());
    }
}
