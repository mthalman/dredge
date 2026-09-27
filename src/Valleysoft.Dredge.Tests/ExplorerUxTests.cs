using Terminal.Gui.Input;
using Valleysoft.Dredge.Explorer;
using Valleysoft.Dredge.Explorer.Tui;

namespace Valleysoft.Dredge.Tests;

[Collection(ExplorerUiCollection.Name)]
public sealed class ExplorerUxTests
{
    [Fact]
    public void PreviewAndDiffExposeTheEndsOfLongLines()
    {
        using ExplorerUiHarness ui = ExplorerWindowTests.Open(ExplorerSamples.Image(), new ExplorerState
        {
            Layer = 2, View = RightView.Inspector, InspectPath = "app/package.json", Focus = FocusPane.Right,
        }, s => new FakeExplorerHost
        {
            Baseline = s,
            Preview = path => new PreviewContent(path, null, [new string('x', 150) + "preview-tail"], null, 162),
        }, out _, width: 80, height: 24);
        ui.Until(() => ui.State.Preview is not null, "preview");
        Assert.False(ui.Shows("preview-tail"));
        for (int i = 0; i < 30; i++) ui.Press(Key.CursorRight);
        Assert.True(ui.Shows("preview-tail"));
        ui.Press(Key.Esc);
        ui.Window.StartCompare("2.0");
        ui.Until(() => ui.State.Compare is not null, "comparison");
        ui.State.Compare!.Diff = new TextDiffContent("app/config.json",
            TextDiff.Diff([new string('x', 100) + "old-tail"], [new string('x', 100) + "new-tail"]), null);
        ui.Window.ImageChanged();
        ui.Pump();
        Assert.False(ui.Window.Layers.Visible);
        Assert.Equal(80, ui.Window.Right.Frame.Width);
        for (int i = 0; i < 30; i++) ui.Press(Key.CursorRight);
        Assert.True(ui.Shows("old-tail"));
        Assert.True(ui.Shows("new-tail"));
    }

    [Fact]
    public void ComparisonSearchFindsCollapsedFilesAndPackages()
    {
        using ExplorerUiHarness ui = ExplorerWindowTests.Open(out FakeExplorerHost host);
        ui.Window.StartCompare("2.0");
        ui.Until(() => ui.State.Compare is not null, "comparison");
        ui.Press(new Key('/'));
        ui.Type("package.json");
        Assert.Single(new CompareView(ui.Window.Presenter, ui.State.Compare!).Rows());
        ui.Press(Key.Enter);
        ui.Until(() => ui.State.Compare!.Diff is not null, "diff");
        Assert.Equal(["app/package.json"], host.Diffed);
        ui.Press(Key.Esc);
        ui.Press(Key.Esc);
        ui.Press(new Key('/'));
        ui.Type("left-pad");
        ui.Press(Key.Enter);
        ui.Until(() => ui.State.Compare!.PackageFiles?.Files is not null, "package files");
        Assert.True(ui.Shows("app/node_modules/left-pad/index.js"));
        ui.Press(Key.Enter);
        ui.Until(() => ui.State.Compare!.Diff is not null, "package file diff");
        Assert.Equal("app/node_modules/left-pad/index.js", host.Diffed[^1]);
    }

    [Fact]
    public void AllPackageFilesCanBeNavigated()
    {
        using ExplorerUiHarness ui = ExplorerWindowTests.Open(out FakeExplorerHost host);
        ui.Window.StartCompare("2.0");
        ui.Until(() => ui.State.Compare is not null, "comparison");
        CompareState c = ui.State.Compare!;
        c.PackageFiles = new PackageFilesContent(c.Comparison.Packages[0],
            Enumerable.Range(0, 50).Select(i => ($"app/file-{i:D2}", Change.Modified)).ToArray(), null, 50);
        ui.Window.ImageChanged();
        ui.Press(Key.End);
        Assert.True(ui.Shows("app/file-49"));
        ui.Press(Key.Enter);
        ui.Until(() => c.Diff is not null, "last file diff");
        Assert.Equal(["app/file-49"], host.Diffed);
    }

    [Theory]
    [InlineData(119)]
    [InlineData(120)]
    [InlineData(150)]
    public void FilenameTakesPriorityOverMetadataAndFindingAnnotation(int width)
    {
        using ExplorerUiHarness ui = ExplorerWindowTests.Open(out _, width: width, height: 30);
        Assert.True(ui.Shows("package.json"), ui.Screen());
        if (width == 120)
        {
            Assert.False(ui.Shows("uid:gid"));
        }
    }

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
