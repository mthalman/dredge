using Terminal.Gui.Input;
using Valleysoft.Dredge.Explorer;
using static Valleysoft.Dredge.Tests.ExplorerSamples;

namespace Valleysoft.Dredge.Tests;

[Collection(ExplorerUiCollection.Name)]
public sealed class ExplorerNavigationTests
{
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
