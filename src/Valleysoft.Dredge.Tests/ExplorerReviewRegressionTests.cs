using Terminal.Gui.Input;
using Valleysoft.Dredge.Explorer;
using Valleysoft.Dredge.Explorer.Tui;

namespace Valleysoft.Dredge.Tests;

[Collection(ExplorerUiCollection.Name)]
public sealed class ExplorerReviewRegressionTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DirectoryReplacementFileCanBeInspectedWithoutLosingDeletedChildren(bool whole)
    {
        ExplorerImage image = ExplorerSamples.Custom(
        [
            ExplorerSamples.Layer([ExplorerSamples.File("app/item/old", 1, "old")]),
            ExplorerSamples.Layer([ExplorerSamples.File("app/item", 2, "new")], ["app/item"])
        ]);
        ExplorerState state = new() { Layer = 1, WholeFilesystem = whole, Focus = FocusPane.Right };
        state.Expanded.Add("app");
        using ExplorerUiHarness ui = ExplorerWindowTests.Open(image, state,
            session => new FakeExplorerHost { Baseline = session }, out _);
        Node node = ExplorerImage.Find(ui.Window.Presenter.Tree(state), "app/item")!;
        Assert.Equal(Kind.File, node.Kind);
        Assert.Equal(2, node.Size);
        Assert.Equal(Change.Removed, Assert.Single(node.Children).Change);
        state.Cursor = ui.Window.Presenter.IndexOf(state, "app/item");
        ui.Window.Apply(new Activate());
        Assert.Equal(RightView.Inspector, state.View);
        Assert.Equal("app/item", state.InspectPath);
    }

    [Fact]
    public void EmptyImageRendersAndLayerCommandsRemainSafe()
    {
        ExplorerImage image = ExplorerSamples.Custom([]);
        using ExplorerUiHarness ui = ExplorerWindowTests.Open(image, new ExplorerState(),
            session => new FakeExplorerHost { Baseline = session }, out _);
        Assert.True(ui.Shows("empty filesystem"), ui.Screen());
        foreach (Cmd command in new Cmd[] { new StepLayer(1), new StepLayer(-1), new SelectLayer(0),
            new RetryLayer(0), new Jump(true), new SetWhole(true) })
        {
            ui.Window.Apply(command);
            ui.Pump();
        }
        Assert.DoesNotContain(ui.Window.Presenter.Hints(ui.State), hint => hint.Label == "Step layer");
        ui.Press(new Key('/'));
        ui.Type("nothing");
        ui.Press(Key.Esc);
        ui.Press(new Key('q'));
        Assert.True(ui.Window.StopRequested);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LifetimeCancellationStopsTheScreen(bool canceledBeforeOpen)
    {
        using CancellationTokenSource cts = new();
        if (canceledBeforeOpen)
        {
            cts.Cancel();
        }
        ExplorerImage image = ExplorerSamples.Image();
        using ExplorerUiHarness ui = new(150, 42, _ => new ExplorerWindow(image,
            new ExplorerState { Layer = 2 }, new FakeExplorerHost { Baseline = image.Session }, cts.Token));
        cts.Cancel();
        ui.Until(() => ui.Window.StopRequested, "lifetime cancellation");
        Assert.True(ui.Window.StopRequested);
    }
}
