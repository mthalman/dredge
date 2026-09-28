using System.Diagnostics;
using Terminal.Gui.Input;
using Valleysoft.Dredge.Explorer;
using static Valleysoft.Dredge.Tests.ExplorerSamples;

namespace Valleysoft.Dredge.Tests;

[Collection(ExplorerUiCollection.Name)]
public sealed class ExplorerDiveScenarioTests
{
    [Theory]
    [Trait("Upstream", "wagoodman/dive#99")]
    [Trait("Upstream", "wagoodman/dive#620")]
    [Trait("Upstream", "wagoodman/dive#706")]
    [InlineData(false)]
    [InlineData(true)]
    public void EmptyFilteredViewsRejectActionsWithoutLeavingTheExplorer(bool search)
    {
        ExplorerImage image = Custom([Layer([File("visible", 1, "one")])]);
        using ExplorerUiHarness ui = ExplorerWindowTests.Open(image, new(),
            session => new FakeExplorerHost { Baseline = session }, out FakeExplorerHost host);
        if (search)
        {
            ui.Press(new Key('/'));
            ui.Type("no-matching-path");
            Assert.Empty(ui.Window.Presenter.SearchResults(ui.State).Hits);
        }
        else
        {
            ui.Window.Apply(new ToggleChange(Change.Added));
            Assert.Empty(ui.Window.Presenter.Flatten(ui.State));
        }

        foreach (Key key in new[] { Key.CursorLeft, Key.CursorRight, Key.CursorDown, Key.PageDown, Key.End, Key.Enter })
        {
            ui.Press(key);
        }
        ui.Window.Apply(new ExtractSelected());
        Assert.Equal("Select a file or folder to extract.", ui.State.Notice);
        Assert.False(ui.Window.ExtractField.Visible);
        ui.Window.Apply(new OpenInViewer());
        Assert.Equal("Select a file to open in the viewer.", ui.State.Notice);
        Assert.Empty(host.Extracted);
        Assert.Empty(host.Previewed);
        Assert.False(ui.Window.StopRequested);
    }

    [Fact]
    [Trait("Upstream", "wagoodman/dive#259")]
    [Trait("Upstream", "wagoodman/dive#543")]
    [Trait("Upstream", "wagoodman/dive#707")]
    public void FilteredAncestorsRemainNavigableAndCursorTargetsTheDisplayedFile()
    {
        ExplorerImage image = Custom(
        [
            Layer([File("app/deep/changed", 1, "old"),
                .. Enumerable.Range(0, 80).Select(i => File($"app/unchanged{i:D3}", 1, $"h{i}"))]),
            Layer([File("app/deep/changed", 2, "new")])
        ]);
        ExplorerState state = new() { Layer = 1, WholeFilesystem = true };
        state.Expanded.UnionWith(["app", "app/deep"]);
        using ExplorerUiHarness ui = ExplorerWindowTests.Open(image, state,
            session => new FakeExplorerHost { Baseline = session }, out FakeExplorerHost host);
        ui.Press(Key.End);
        Assert.True(state.Scroll > 0);
        ui.Window.Apply(new ToggleChange(Change.None));
        ui.Pump();

        Assert.Equal(["app", "app/deep", "app/deep/changed"],
            ui.Window.Presenter.Flatten(state).Select(row => row.Path));
        Assert.InRange(state.Cursor, 0, 2);
        Assert.Equal(0, state.Scroll);
        ui.Press(Key.End);
        for (int i = 0; i < 5; i++)
        {
            ui.Press(Key.CursorDown);
        }
        Assert.Equal(2, state.Cursor);
        ui.Press(Key.CursorLeft);
        Assert.Equal(1, state.Cursor);
        ui.Press(Key.CursorLeft);
        Assert.DoesNotContain("app/deep", state.Expanded);
        ui.Press(Key.CursorRight);
        Assert.Contains("app/deep", state.Expanded);
        ui.Press(Key.End);
        ui.Press(Key.Enter);
        ui.Until(() => state.Preview is not null, "filtered file preview");
        Assert.Equal("app/deep/changed", Assert.Single(host.Previewed));
        Assert.Equal("app/deep/changed", state.InspectPath);
    }

    [Fact]
    [Trait("Upstream", "wagoodman/dive#4")]
    [Trait("Upstream", "wagoodman/dive#55")]
    [Trait("Upstream", "wagoodman/dive#185")]
    public void RepeatedLayerAndFilesystemSwitchesPreserveCollapsedBranchesAndContents()
    {
        ExplorerImage image = Custom(
        [
            Layer([File("app/nested/value", 1, "old"), File("app/root", 3, "root")]),
            Layer([File("app/nested/value", 2, "new"), File("app/root", 3, "root")])
        ]);
        ExplorerState state = new();
        state.Expanded.Add("app");
        using ExplorerUiHarness ui = ExplorerWindowTests.Open(image, state,
            session => new FakeExplorerHost { Baseline = session }, out _);
        List<Node> first = image.WholeTree(0)!;
        for (int i = 0; i < 12; i++)
        {
            int layer = i % 2;
            ui.Window.Apply(new SelectLayer(layer));
            ui.Window.Apply(new SetWhole(i % 3 == 0));
            Assert.Equal(["app", "app/nested", "app/root"],
                ui.Window.Presenter.Flatten(state).Select(row => row.Path));
            Assert.DoesNotContain("app/nested", state.Expanded);
            Assert.Equal(layer + 1, ExplorerImage.Find(ui.Window.Presenter.Tree(state), "app/nested/value")!.Size);
            ui.Press(Key.End);
            Assert.Equal("app/root", ui.Window.Presenter.Flatten(state)[state.Cursor].Path);
        }
        Assert.Same(first, image.WholeTree(0));
        Assert.Equal(1, ExplorerImage.Find(first, "app/nested/value")!.Size);
    }

    [Theory]
    [Trait("Upstream", "wagoodman/dive#72")]
    [Trait("Upstream", "wagoodman/dive#295")]
    [Trait("Upstream", "wagoodman/dive#469")]
    [InlineData(80, 24)]
    [InlineData(150, 42)]
    public void LongLayerListsKeepExactlyOneVisibleSelectionAtBothEnds(int width, int height)
    {
        ExplorerImage image = Custom(Enumerable.Range(0, 80)
            .Select(i => Layer([File($"layer{i:D2}", 1, $"h{i}")])).ToArray());
        using ExplorerUiHarness ui = ExplorerWindowTests.Open(image, new() { Focus = FocusPane.Layers },
            session => new FakeExplorerHost { Baseline = session }, out _, width, height);
        foreach (bool last in new[] { true, false })
        {
            ui.Press(last ? Key.End : Key.Home);
            for (int i = 0; i < 3; i++)
            {
                ui.Press(last ? Key.CursorDown : Key.CursorUp);
            }
            Assert.Equal(last ? 79 : 0, ui.State.Layer);
            PaneContent pane = ui.Window.Presenter.LayersPane(ui.State);
            Line selected = Assert.Single(pane.Lines, line => line.ToString().StartsWith("\u258c", StringComparison.Ordinal));
            Assert.Contains(last ? "step 79" : "step 0", selected.ToString());
            Assert.True(pane.Lines.Count < image.LayerCount);
            var frame = ui.Window.Layers.FrameToScreen();
            Assert.Contains(Enumerable.Range(frame.Y, frame.Height).Select(ui.Row),
                row => row.Contains(last ? "\u258c79 " : "\u258c 0 ", StringComparison.Ordinal)
                    && row.Contains(last ? "step 79" : "step 0", StringComparison.Ordinal));
        }
    }

    [Fact]
    [Trait("Upstream", "wagoodman/dive#12")]
    [Trait("Upstream", "wagoodman/dive#95")]
    [Trait("Upstream", "wagoodman/dive#179")]
    public void ResizeAcrossLayoutBoundariesPreservesTheSelectedFile()
    {
        using ExplorerUiHarness ui = ExplorerWindowTests.Open(out _);
        ui.Window.Apply(new SetCursor(ui.Window.Presenter.IndexOf(ui.State, "app/package.json")));
        foreach ((int width, int height) in new[] { (80, 24), (60, 15), (180, 50), (80, 24) })
        {
            ui.Resize(width, height);
            Assert.Equal(width, ui.Window.Presenter.Width);
            Assert.Equal(height, ui.Window.Presenter.Height);
            Assert.Equal(width < 80, ui.Window.Presenter.TooSmall);
            Assert.Equal("app/package.json", ui.Window.Presenter.Flatten(ui.State)[ui.State.Cursor].Path);
            if (width >= 80)
            {
                Assert.True(ui.Window.Right.Visible);
                Assert.True(ui.Shows("package.json"), ui.Screen());
            }
            else
            {
                Assert.False(ui.Window.Right.Visible);
                Assert.True(ui.Shows("needs at least"), ui.Screen());
            }
        }
        ui.Press(Key.Enter);
        ui.Until(() => ui.State.Preview is not null, "preview after resizing");
        Assert.Equal("app/package.json", ui.State.InspectPath);
    }

    [Fact]
    [Trait("Upstream", "wagoodman/dive#112")]
    [Trait("Upstream", "wagoodman/dive#132")]
    public void FirstLayerContentsAreAddedRatherThanInherited()
    {
        ExplorerImage image = Custom([Layer([File("app/value", 5, "h")])], baseLayerCount: 1);
        ExplorerState state = new();
        state.Hidden.UnionWith([Change.None, Change.Identical]);
        state.Expanded.Add("app");
        using ExplorerUiHarness ui = ExplorerWindowTests.Open(image, state,
            session => new FakeExplorerHost { Baseline = session }, out _);
        foreach (bool whole in new[] { false, true })
        {
            ui.Window.Apply(new SetWhole(whole));
            FlatRow file = Assert.Single(ui.Window.Presenter.Flatten(state), row => row.Path == "app/value");
            Assert.Equal(Change.Added, file.Node.Change);
            Assert.True(ui.Shows("value"), ui.Screen());
        }
    }

    [Fact]
    [Trait("Upstream", "wagoodman/dive#647")]
    [Trait("Upstream", "wagoodman/dive#674")]
    public void LongInsightListsScrollTheirSelectedFindingIntoView()
    {
        ExplorerImage image = Custom(Enumerable.Range(0, 31)
            .Select(i => Layer([File("app/payload", 2_000_000 + i, $"version{i}")])).ToArray());
        using ExplorerUiHarness ui = ExplorerWindowTests.Open(image, new() { View = RightView.Insights },
            session => new FakeExplorerHost { Baseline = session }, out _, width: 100, height: 28);
        Assert.True(image.Findings.Count >= 30);
        ui.Press(Key.End);
        Assert.Equal(image.Findings.Count - 1, ui.State.Finding);
        Assert.True(ui.State.FindingScroll > 0);
        ExplorerFinding finding = ui.Window.Presenter.SelectedFinding(ui.State)!;
        Assert.True(ui.Shows(finding.Where), ui.Screen());
        Assert.True(ui.Shows($"\u258c{image.Findings.Count} "), ui.Screen());
        ui.Press(Key.Home);
        Assert.Equal(0, ui.State.Finding);
        Assert.Equal(0, ui.State.FindingScroll);
        Assert.True(ui.Shows("\u258c1 "), ui.Screen());
    }

    [Fact]
    [Trait("Upstream", "wagoodman/dive#119")]
    [Trait("Upstream", "wagoodman/dive#681")]
    [Trait("Upstream", "wagoodman/dive#683")]
    public void DuplicateHeavyImageNavigationKeepsAllocationAndLatencyBounded()
    {
        const int files = 20_000;
        LayerChanges layer = Layer(Enumerable.Range(0, files)
            .Select(i => File($"app/f{i:D5}", 64, $"h{i}")).ToArray());
        ExplorerImage image = Custom([layer, layer, layer]);
        ExplorerState state = new() { Layer = 2 };
        state.Expanded.Add("app");
        using ExplorerUiHarness ui = ExplorerWindowTests.Open(image, state,
            session => new FakeExplorerHost { Baseline = session }, out _);
        ui.Window.Apply(new Move(1));
        ui.Pump();
        List<Node> tree = image.LayerTree(2);
        long start = GC.GetAllocatedBytesForCurrentThread();
        Stopwatch elapsed = Stopwatch.StartNew();
        for (int i = 0; i < 10; i++)
        {
            ui.Window.Apply(new Move(1));
            ui.Pump();
        }
        long allocated = GC.GetAllocatedBytesForCurrentThread() - start;

        Assert.True(allocated < 64_000_000, $"Ten moves allocated {allocated:N0} bytes.");
        Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(10), $"Ten moves took {elapsed.Elapsed}.");
        Assert.Same(tree, image.LayerTree(2));
        Assert.Equal("app/f00010", ui.Window.Presenter.Flatten(state)[state.Cursor].Path);
        Assert.Equal(2 * files * 64L, image.TotalReclaimable);
    }

    [Fact]
    [Trait("Upstream", "wagoodman/dive#356")]
    [Trait("Upstream", "wagoodman/dive#443")]
    public void MultilineHistoryRendersAsOneRowWithoutLosingShellSyntax()
    {
        const string instruction = "/bin/sh -c printf '%s\\n' \"[ok]\" &&\r\n\t echo ready # buildkit";
        ExplorerImage image = Custom([Layer([File("app/value", 1, "h")])], instructions: [instruction]);
        using ExplorerUiHarness ui = ExplorerWindowTests.Open(image, new(),
            session => new FakeExplorerHost { Baseline = session }, out _);

        Assert.Equal("RUN printf '%s\\n' \"[ok]\" && echo ready", image.Instructions[0]);
        PaneContent pane = ui.Window.Presenter.LayersPane(ui.State);
        Assert.Single(pane.Lines);
        Assert.All(pane.Lines.Select(line => line.ToString()), line =>
            Assert.DoesNotContain(line, char.IsControl));
        Assert.True(ui.Shows("printf"), ui.Screen());
        Assert.True(ui.Shows("[ok]"), ui.Screen());
    }
}
