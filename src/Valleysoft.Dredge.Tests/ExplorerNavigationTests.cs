using Terminal.Gui.Input;
using Valleysoft.Dredge.Explorer;
using static Valleysoft.Dredge.Tests.ExplorerSamples;

namespace Valleysoft.Dredge.Tests;

[Collection(ExplorerUiCollection.Name)]
public sealed class ExplorerNavigationTests
{
    [Fact]
    public void HistoryReadFailuresAreVisibleAndReturningFromDiffResumesAnInterruptedPreview()
    {
        TaskCompletionSource<PreviewContent> preview = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource<TextDiffContent> diff = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int reads = 0;
        ExplorerImage image = Custom([Layer([File("file", 1, "old")]), Layer([File("file", 1, "new")])]);
        using ExplorerUiHarness ui = ExplorerWindowTests.Open(image, new() { Layer = 1 },
            session => new FakeExplorerHost
            {
                Baseline = session,
                VersionWork = (_, _, _) => Interlocked.Increment(ref reads) == 1 ? preview.Task
                    : Task.FromException<PreviewContent>(new IOException("preview denied")),
                VersionDiffWork = (_, _, _, _) => diff.Task
            }, out _);
        ui.Press(Key.Enter);
        ui.Press(new Key('h').WithAlt);
        ui.Press(Key.Enter);
        ui.Until(() => reads == 1, "pending preview");
        Task firstRead = ui.Window.HistoryTask;
        ui.Press(new Key('d').WithAlt);
        ui.Press(Key.Esc);
        ui.Until(() => ui.Shows("preview denied"), "resumed preview failure");
        preview.SetResult(new("file", null, ["stale"], null, 1));
        diff.SetException(new IOException("stale diff"));
        ui.Until(() => firstRead.IsCompleted, "old preview completion");
        ui.Pump();
        Assert.True(ui.Shows("preview denied"), ui.Screen());
        Assert.Null(ui.State.HistoryDiff);
        ui.Press(new Key('d').WithAlt);
        ui.Until(() => ui.Shows("Could not read historical diff"), "diff failure");
        ui.Press(Key.Esc);
        Assert.True(ui.Shows("preview denied"), ui.Screen());
        ui.Press(Key.Esc);
        Assert.True(ui.Shows("File history"), ui.Screen());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CanceledHistoricalReadsCannotOverwriteAnotherVersion(bool failOldRequest)
    {
        TaskCompletionSource<PreviewContent> pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken oldToken = default;
        ExplorerImage image = Custom([Layer([File("file", 1, "old")]), Layer([File("file", 1, "new")])]);
        using ExplorerUiHarness ui = ExplorerWindowTests.Open(image, new() { Layer = 1 },
            session => new FakeExplorerHost
            {
                Baseline = session,
                VersionWork = (path, layer, token) =>
                {
                    if (layer == 0)
                    {
                        oldToken = token;
                        return pending.Task;
                    }
                    return Task.FromResult(new PreviewContent(path, null, ["correct version"], null, 1));
                }
            }, out _);
        ui.Press(Key.Enter);
        ui.Until(() => ui.State.Preview is not null, "inspector");
        (int x, int y) = ui.Find("History");
        ui.Click(x, y);
        Assert.Equal(RightView.History, ui.State.View);
        ui.Press(Key.Home);
        ui.Press(Key.Enter);
        ui.Until(() => oldToken.CanBeCanceled, "older request");
        Task oldRead = ui.Window.HistoryTask;
        ui.Press(Key.Esc);
        Assert.True(oldToken.IsCancellationRequested);
        ui.Press(Key.End);
        ui.Press(Key.Enter);
        ui.Until(() => ui.Shows("correct version"), "newer version");
        if (failOldRequest) pending.SetException(new IOException("stale failure"));
        else pending.SetResult(new("file", null, ["stale version"], null, 1));
        ui.Until(() => oldRead.IsCompleted, "canceled read completion");
        ui.Pump();
        Assert.Equal(["correct version"], ui.State.HistoryPreview!.Lines);
        Assert.False(ui.Shows("stale"), ui.Screen());
        Assert.Equal(1, ui.State.HistoryLayer);
    }

    [Theory]
    [InlineData(80, 24)]
    [InlineData(150, 42)]
    public void FileHistoryOpensEveryVersionAndReturnsWithoutMovingTheFileTree(int width, int height)
    {
        ExplorerImage image = Custom([.. Enumerable.Range(0, 40).Select(i => Layer([File("file", 10, $"hash{i}")]))]);
        using ExplorerUiHarness ui = ExplorerWindowTests.Open(image, new() { Layer = 39 },
            session => new FakeExplorerHost { Baseline = session }, out _, width, height);
        ui.Press(Key.Enter);
        ui.Until(() => ui.State.Preview is not null, "initial preview");
        ui.Press(new Key('h').WithAlt);
        Assert.True(ui.Shows("File history"), ui.Screen());
        ui.Press(Key.Home);
        (int firstX, int firstY) = ui.Find("layer 0 ");
        ui.DoubleClick(firstX, firstY);
        ui.Until(() => ui.Shows("Version at layer 0") && ui.Shows("version 0"), "first historical version");
        Assert.Equal(39, ui.State.Layer);
        ui.Press(Key.Esc);
        Assert.True(ui.Shows("File history"), ui.Screen());
        ui.Press(Key.End);
        ui.Press(new Key('d').WithAlt);
        ui.Until(() => ui.Shows("version 38") && ui.Shows("version 39") && ui.Shows("Side-by-side diff"), "historical diff");
        ui.Press(new Key('v').WithAlt);
        Assert.True(ui.Shows("Inline diff"), ui.Screen());
        ui.Press(Key.Esc);
        Assert.True(ui.Shows("File history"), ui.Screen());
        ui.Press(Key.Esc);
        Assert.Equal(RightView.Inspector, ui.State.View);
        ui.Press(Key.Esc);
        Assert.Equal(RightView.Files, ui.State.View);
        Assert.Equal(39, ui.State.Layer);
        Assert.Equal("file", ui.Window.Presenter.Flatten(ui.State)[ui.State.Cursor].Path);
    }

    [Theory]
    [InlineData(80, 24)]
    [InlineData(150, 42)]
    public void ComparisonOverviewKeepsFilesAccessibleWithHundredsOfPackageChanges(int width, int height)
    {
        using ExplorerUiHarness ui = ExplorerWindowTests.Open(Image(), new(),
            session => new FakeExplorerHost
            {
                Baseline = session,
                Target = _ => Session(["sha256:new"], [Layer([File("new-file", 100, "new")])],
                    Enumerable.Range(0, 300).ToDictionary(i => $"pkg{i:D3}", _ => "2.0"), reference: "app:2")
            }, out _, width, height);
        ui.Window.StartCompare("2");
        ui.Until(() => ui.State.Compare is not null, "comparison overview");
        CompareState compare = ui.State.Compare!;
        Assert.True(ui.Shows("Comparison overview"), ui.Screen());
        Assert.True(ui.Shows("Files"), ui.Screen());
        Assert.True(ui.Shows("Packages"), ui.Screen());
        Assert.True(ui.Shows("Hidden payload"), ui.Screen());
        Assert.Equal(2, new CompareView(ui.Window.Presenter, compare).Rows().Count);
        ui.Press(Key.Enter);
        Assert.True(ui.Shows("Final filesystem differences"), ui.Screen());
        ui.Press(Key.Esc);
        Assert.True(ui.Shows("Comparison overview"), ui.Screen());
        ui.Press(Key.CursorDown);
        ui.Press(Key.Enter);
        Assert.Contains(new CompareView(ui.Window.Presenter, compare).Rows(), row => row.Package?.Name == "pkg299");
        ui.Press(Key.Esc);
        Assert.True(ui.Shows("Comparison overview"), ui.Screen());
        ui.Press(new Key('/'));
        ui.Type("pkg299");
        Assert.Contains(new CompareView(ui.Window.Presenter, compare).Rows(), row => row.Package?.Name == "pkg299");
        ui.Press(Key.Esc);
        ui.Press(Key.Esc);
        Assert.True(ui.Shows("Comparison overview"), ui.Screen());
        (int x, int y) = ui.Find("Packages  ");
        ui.Click(x, y);
        Assert.False(compare.Overview);
        Assert.Contains(new CompareView(ui.Window.Presenter, compare).Rows(), row => row.Package?.Name == "pkg299");
        ui.Press(Key.Esc);
        ui.Press(Key.Esc);
        Assert.Null(ui.State.Compare);
    }

    [Fact]
    public void ShortTerminalGivesFilesRoomAndRestoresTheViewportAfterTabbing()
    {
        ExplorerImage image = Custom([Layer([.. Enumerable.Range(0, 50).Select(static i => File($"file{i:D2}", i, $"h{i}"))])]);
        using ExplorerUiHarness ui = ExplorerWindowTests.Open(image, new(),
            static session => new FakeExplorerHost { Baseline = session }, out _, width: 80, height: 24);
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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DefaultFileOrderingCountsSharedFolderContentOnce(bool whole)
    {
        ExplorerImage image = Custom([Layer([
            File("bin/content", 100, "shared"),
            .. Enumerable.Range(0, 3).Select(static i => File($"usr/link{i}", 0, "") with
                { Type = ImageFileType.HardLink, LinkTarget = "bin/content" }),
            File("z/content", 150, "other")])]);
        using ExplorerUiHarness ui = ExplorerWindowTests.Open(image, new() { WholeFilesystem = whole },
            static session => new FakeExplorerHost { Baseline = session }, out _);
        List<FlatRow> rows = ui.Window.Presenter.Flatten(ui.State);
        Assert.Equal(["bin", "usr", "z"], rows.Select(static row => row.Path));
        Assert.Equal([100L, 100L, 150L], rows.Select(static row => row.Node.Size));
        Assert.Equal(250, Node.TotalSize(ui.Window.Presenter.Tree(ui.State)));
    }

    [Fact]
    public void BackFromSearchDrilldownRestoresTheSelectedResultAndViewport()
    {
        ExplorerImage image = Custom([Layer([.. Enumerable.Range(0, 60).Select(static i => File($"app/file{i:D2}", i + 1, $"h{i}"))])]);
        using ExplorerUiHarness ui = ExplorerWindowTests.Open(image, new(),
            static session => new FakeExplorerHost { Baseline = session }, out _);
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
        Assert.Contains(ui.Window.Presenter.Hints(ui.State), static hint => hint.Key == "Esc" && hint.Label == "Back to search");
        ui.Press(Key.Esc);
        Assert.Equal(RightView.Search, ui.State.View);
        Assert.Equal("file", ui.State.SearchQuery);
        Assert.Equal(cursor, ui.State.SearchCursor);
        Assert.Equal(scroll, ui.State.SearchScroll);
        Assert.True(ui.Window.Search.HasFocus);
    }

    [Fact]
    public void FindingDrilldownRevealsFilteredDestinationAndBackRestoresFilters()
    {
        using ExplorerUiHarness ui = ExplorerWindowTests.Open(out _);
        ui.State.Hidden.UnionWith(Enum.GetValues<Change>());
        ui.Press(new Key('i'));
        int index = ui.Window.Presenter.VisibleFindings(ui.State)
            .FindIndex(static finding => finding?.Kind == ExplorerFindingKind.Deleted);
        Assert.True(index >= 0);
        ui.Window.Apply(new SelectFinding(index));
        ExplorerFinding finding = ui.Window.Presenter.SelectedFinding(ui.State)!;
        ui.Press(Key.Enter);

        Assert.Equal(RightView.Files, ui.State.View);
        Assert.Empty(ui.State.Hidden);
        Assert.Equal(finding.Roots[0], ui.Window.Presenter.Flatten(ui.State)[ui.State.Cursor].Path);
        ui.Press(Key.Esc);
        Assert.Equal(RightView.Insights, ui.State.View);
        Assert.Equal(index, ui.State.Finding);
        Assert.Equal(Enum.GetValues<Change>(), ui.State.Hidden.Order());
    }

    [Fact]
    public void BackFromFindingRestoresLayerFiltersAndFindingSelection()
    {
        using ExplorerUiHarness ui = ExplorerWindowTests.Open(out _);
        ui.State.Hidden.Add(Change.Identical);
        ui.Press(new Key('i'));
        ui.Press(Key.End);
        int layer = ui.State.Layer, finding = ui.State.Finding, scroll = ui.State.FindingScroll;
        string[] expanded = [.. ui.State.Expanded.Order()];
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
