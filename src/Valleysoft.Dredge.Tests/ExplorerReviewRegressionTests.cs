using Terminal.Gui.Input;
using Valleysoft.Dredge.Explorer;
using Valleysoft.Dredge.Explorer.Tui;

namespace Valleysoft.Dredge.Tests;

[Collection(ExplorerUiCollection.Name)]
public sealed class ExplorerReviewRegressionTests
{
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
