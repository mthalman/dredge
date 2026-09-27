using Terminal.Gui.Input;
using Valleysoft.Dredge.Explorer;
using Valleysoft.Dredge.Explorer.Tui;

namespace Valleysoft.Dredge.Tests;

[Collection(ExplorerUiCollection.Name)]
public sealed class ExplorerUxTests
{
    [Fact]
    public void PartialDiffShowsBothNoticeAndAvailableLines()
    {
        using ExplorerUiHarness ui = ExplorerWindowTests.Open(out _);
        ui.Window.StartCompare("2.0");
        ui.Until(() => ui.State.Compare is not null, "comparison");
        ui.State.Compare!.Diff = new TextDiffContent("app/config.json",
            TextDiff.Diff(["old-value"], ["new-value"]), "Showing the first 256 KB.");
        ui.Window.ImageChanged();
        ui.Pump();
        Assert.True(ui.Shows("Showing the first 256 KB."));
        Assert.True(ui.Shows("old-value"));
        Assert.True(ui.Shows("new-value"));
    }

    [Fact]
    public void CompactAuxiliaryViewsUseTheBodyAndHelpScrolls()
    {
        using ExplorerUiHarness ui = ExplorerWindowTests.Open(out _, width: 80, height: 24);
        ui.Press(new Key('?'));
        Assert.False(ui.Window.Layers.Visible);
        ui.Press(Key.End);
        Assert.True(ui.Shows("NO_COLOR=1"));
        ui.Press(Key.Home);
        Assert.True(ui.Shows("Move"));
        ui.Press(Key.Esc);
        ui.Press(new Key('i'));
        Assert.False(ui.Window.Layers.Visible);
        Assert.True(ui.Shows("COPY --from=build"));
        ui.Press(Key.Esc);
        ui.Press(new Key('/'));
        ui.Type("app");
        Assert.False(ui.Window.Layers.Visible);
        Assert.True(ui.Shows("Esc  Close"));
        Assert.True(ui.Shows("/app/package.json"));
    }

    [Fact]
    public void ComparisonProgressSurvivesNavigationAndCanBeCanceled()
    {
        TaskCompletionSource<ExplorerComparison> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using ExplorerUiHarness ui = ExplorerWindowTests.Open(ExplorerSamples.Image(), new ExplorerState { Layer = 2 },
            s => new FakeExplorerHost { Baseline = s, CompareWork = () => completion.Task }, out FakeExplorerHost host);
        ui.Window.StartCompare("2.0");
        ui.Until(() => ui.State.ComparisonStatus?.EndsWith("reading packages") == true, "packages");
        ui.Press(Key.CursorDown);
        Assert.True(ui.Shows("reading packages"));
        ui.Press(Key.Esc);
        Assert.Null(ui.State.ComparisonStatus);
        Assert.Equal("Comparison canceled.", ui.State.Notice);
        completion.SetResult(ExplorerSession.Compare(host.Baseline!, ExplorerSamples.Target()));
        ui.Pump();
        Assert.Null(ui.State.Compare);
    }

    [Fact]
    public void ExactTagWinsOverSubstringMatches()
    {
        using ExplorerUiHarness ui = ExplorerWindowTests.Open(ExplorerSamples.Image(), new ExplorerState { Layer = 2 },
            s => new FakeExplorerHost { Baseline = s, Tags = ["1.2.0", "2.0", "2.0-rc1"] }, out FakeExplorerHost host);
        ui.InDialog(() => ui.Press(new Key('c')),
            DialogStep.When("tags", () => ui.Shows("3 tags."), () => ui.Send("2.0")),
            DialogStep.When("exact match", () => ui.Shows("▌2.0 "), () => ui.Send(Key.Enter)));
        ui.Until(() => ui.State.Compare is not null, "comparison");
        Assert.Equal(["2.0"], host.Compared);
    }
}
