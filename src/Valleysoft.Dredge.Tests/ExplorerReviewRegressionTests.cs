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
