using Terminal.Gui.Input;
using Valleysoft.Dredge.Explorer;

namespace Valleysoft.Dredge.Tests;

[Collection(ExplorerUiCollection.Name)]
public sealed class ExplorerFileDiffTests
{
    private static CompareState State(string[] before, string[] after, bool unified = false)
    {
        ExplorerSession baseline = ExplorerSamples.Image().Session!;
        return new(ExplorerSession.Compare(baseline, ExplorerSamples.Target()), "old", "new")
        {
            UnifiedDiff = unified,
            Diff = new("app/config.json", TextDiff.Diff(before, after), null),
        };
    }

    [Fact]
    public void WordHighlightsPreserveSourceAndIdentifyOnlyChangedTokens()
    {
        CompareState state = State(["same", "var value = \"before\"; // 👩‍💻 e\u0301"],
            ["same", "var value = \"after\"; // 👩‍💻 e\u0301"]);
        FileDiffDocument doc = state.Diff!.Document;
        Assert.Equal(2, doc.Split.Count);
        VisualDiffPair pair = doc.Split[1];
        Assert.Equal(["before"], pair.Left!.Spans.Where(static span => span.Changed).Select(static span => span.Text));
        Assert.Equal(["after"], pair.Right!.Spans.Where(static span => span.Changed).Select(static span => span.Text));
        foreach (VisualDiffLine line in doc.Unified)
        {
            Assert.Equal(line.Source.Text, string.Concat(line.Spans.Select(static span => span.Text)));
        }
        Assert.Equal([DiffOp.Same, DiffOp.Delete, DiffOp.Insert], doc.Unified.Select(static line => line.Source.Op));
    }

    [Theory]
    [InlineData(80, false)]
    [InlineData(80, true)]
    [InlineData(150, false)]
    [InlineData(150, true)]
    public void BothLayoutsHaveNumberGuttersChangeMarkersAndWordBackgrounds(int width, bool unified)
    {
        Theme.Apply(ThemeKind.Dark);
        CompareState state = State(["same", "before = 1;", "deleted"], ["same", "after = 1;"], unified);
        ExplorerPresenter presenter = new(ExplorerSamples.Image(), width, 30) { FullWidthContent = true };
        PaneContent pane = new CompareView(presenter, state).Diff();
        Assert.Contains(pane.Lines, line => line.ToString().Contains(unified ? "Inline diff" : "Side-by-side diff"));
        Assert.Contains(pane.Lines, line => line.ToString().Contains("+1 -2"));
        Assert.Contains(pane.Lines, line => line.ToString().Contains(unified ? "  2     - before" : "  2 - before"));
        Assert.Contains(pane.Lines, line => line.ToString().Contains("+ after"));
        Assert.Contains(pane.Lines.SelectMany(line => line.Parts),
            part => part.Text == "before" && part.Sty.Background == Theme.DiffRemovedWord);
        Assert.Contains(pane.Lines.SelectMany(line => line.Parts),
            part => part.Text == "after" && part.Sty.Background == Theme.DiffAddedWord);
        Assert.Contains(pane.Lines.SelectMany(line => line.Parts), part => part.Sty.Background == Theme.GarnetDeep);
        Assert.Contains(pane.Lines.SelectMany(line => line.Parts), part => part.Sty.Background == Theme.KelpDeep);
        Assert.All(pane.Lines, line => Assert.True(line.Length <= presenter.RightInner));
        if (unified)
        {
            List<string> text = [.. pane.Lines.Select(line => line.ToString())];
            Assert.True(text.FindIndex(line => line.Contains("deleted")) < text.FindIndex(line => line.Contains("after")));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NoColorRetainsMarkersAndIntralineEmphasis(bool unified)
    {
        try
        {
            Theme.Apply(ThemeKind.NoColor);
            CompareState state = State(["before"], ["after"], unified);
            PaneContent pane = new CompareView(new(ExplorerSamples.Image(), 80, 24) { FullWidthContent = true }, state).Diff();
            Assert.Contains(pane.Lines, static line => line.ToString().Contains("- before"));
            Assert.Contains(pane.Lines, static line => line.ToString().Contains("+ after"));
            Assert.Contains(pane.Lines.SelectMany(static line => line.Parts), static part =>
                part.Text == "after" && part.Sty.Deco.HasFlag(Deco.Underline));
        }
        finally
        {
            Theme.Apply(ThemeKind.Dark);
        }
    }

    [Fact]
    public void LayoutSwitchPreservesTheTopSourceLineAndWorksThroughKeyboard()
    {
        using var ui = ExplorerWindowTests.Open(out _, width: 100, height: 30);
        ui.Window.StartCompare("2.0");
        ui.Until(() => ui.State.Compare is not null, "comparison");
        CompareState state = ui.State.Compare!;
        string[] before = [.. Enumerable.Range(0, 100).Select(i => $"before value {i}")];
        string[] after = [.. Enumerable.Range(0, 100).Select(i => $"after value {i}")];
        state.Diff = new("file", TextDiff.Diff(before, after), null);
        state.DiffScroll = 20;
        ui.Window.ImageChanged();
        ui.Pump();
        Assert.False(state.UnifiedDiff);
        Assert.Contains("Alt+V", ui.Row(ui.Height - 1));
        Assert.Contains("Inline diff", ui.Row(ui.Height - 1));
        ui.Press(new Key('v').WithAlt);
        Assert.True(state.UnifiedDiff);
        Assert.Equal(20, state.DiffScroll);
        Assert.True(ui.Shows("Inline diff"), ui.Screen());
        Assert.Contains("Side-by-side diff", ui.Row(ui.Height - 1));
        ui.Press(new Key('v').WithAlt);
        Assert.False(state.UnifiedDiff);
        Assert.Equal(20, state.DiffScroll);
        Assert.True(ui.Shows("Side-by-side diff"), ui.Screen());
        ui.Press(new Key('?'));
        ui.Press(Key.Esc);
        Assert.NotNull(state.Diff);
        ui.Press(Key.Esc);
        Assert.Null(state.Diff);
        ui.Press(new Key('v').WithAlt);
        Assert.False(state.UnifiedDiff);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LongUnicodeAndTabbedLinesCanBePannedInBothLayouts(bool unified)
    {
        CompareState state = State(["\t" + new string('界', 100) + "old-tail"], ["\t" + new string('界', 100) + "new-tail"], unified);
        state.DiffColumn = int.MaxValue;
        PaneContent pane = new CompareView(new(ExplorerSamples.Image(), 80, 24) { FullWidthContent = true }, state).Diff();
        Assert.Contains(pane.Lines, static line => line.ToString().Contains("old-tail"));
        Assert.Contains(pane.Lines, static line => line.ToString().Contains("new-tail"));
        Assert.All(pane.Lines, static line => Assert.True(line.Length <= 76));
        int column = 0;
        Assert.Equal("a   b   c", FileDiffDocument.ExpandTabs("a\tb\tc", ref column));
    }

    [Fact]
    public void LargeChangesKeepAllTextWithoutUnboundedWordDiffs()
    {
        string before = new('a', 100_000);
        string after = new('b', 100_000);
        FileDiffDocument document = new(TextDiff.Diff([before], [after])!);
        Assert.Equal(before, Assert.Single(document.Split[0].Left!.Spans).Text);
        Assert.Equal(after, Assert.Single(document.Split[0].Right!.Spans).Text);
        Assert.Equal(100_000, document.TextWidth);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EmptyInsertedDeletedAndPartialFilesRemainReadable(bool unified)
    {
        ExplorerPresenter presenter = new(ExplorerSamples.Image(), 80, 24) { FullWidthContent = true };
        foreach (var (before, after) in new (string[], string[])[] { ([], []), ([], ["added"]), (["removed"], []) })
        {
            CompareState state = State(before, after, unified);
            PaneContent pane = new CompareView(presenter, state).Diff();
            Assert.All(pane.Lines, line => Assert.True(line.Length <= 76));
            Assert.Contains(pane.Lines, line => line.ToString().Contains(before.Length + after.Length == 0
                ? "No text differences." : before.Length == 0 ? "+ added" : "- removed"));
            state.Diff = new("file", state.Diff!.Lines, "Showing the first 256 KB.");
            Assert.Contains(new CompareView(presenter, state).Diff().Lines,
                line => line.ToString().Contains("Showing the first 256 KB."));
        }
        CompareState unavailable = State([], [], unified);
        unavailable.Diff = new("file", null, "Binary file; no preview.");
        PaneContent binary = new CompareView(presenter, unavailable).Diff();
        Assert.Contains(binary.Lines, line => line.ToString().Contains("Binary file; no preview."));
        Assert.DoesNotContain(binary.Lines, line => line.ToString().Contains("+0"));
        Assert.DoesNotContain("changed lines", binary.Subtitle!);
    }
}
