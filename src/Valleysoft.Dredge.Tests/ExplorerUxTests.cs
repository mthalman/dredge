using Terminal.Gui.Input;
using Valleysoft.Dredge.Explorer;
using Valleysoft.Dredge.Explorer.Tui;

namespace Valleysoft.Dredge.Tests;

[Collection(ExplorerUiCollection.Name)]
public sealed class ExplorerUxTests
{
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
