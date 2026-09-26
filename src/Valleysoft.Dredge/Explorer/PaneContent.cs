namespace Valleysoft.Dredge.Explorer;

// What a pane shows. Presenters build it from state; Terminal.Gui views only
// draw it and turn clicks on its hit regions back into commands.
internal sealed record PaneContent(string Title, string? Subtitle, bool Focused, List<Line> Lines)
{
    public List<Hit> Hits { get; } = [];

    public void On(int line, Cmd cmd, int start = 0, int end = int.MaxValue) => Hits.Add(new(line, start, end, cmd));

    public Cmd? HitTest(int line, int col) =>
        Hits.LastOrDefault(h => h.Line == line && col >= h.Start && col < h.End)?.Cmd;

    // Whether the two would paint the same cells; hit regions don't show.
    public bool LooksLike(PaneContent other) =>
        Title == other.Title && Subtitle == other.Subtitle && Focused == other.Focused &&
        Line.SameAs(Lines, other.Lines);
}

internal sealed record Hit(int Line, int Start, int End, Cmd Cmd);

// A footer hint. The same record feeds the footer and the keys screen, so a
// label can't drift between the two.
internal sealed record Hint(string Key, string Label, Cmd? Cmd = null);

// Everything a person can do, as data. Keys, clicks, and footer shortcuts all
// become a Cmd, and ExplorerController.Apply is the only place state changes.
internal abstract record Cmd;
internal sealed record SelectLayer(int Layer) : Cmd;
internal sealed record StepLayer(int Delta) : Cmd;
internal sealed record Move(int Delta) : Cmd;
internal sealed record Jump(bool ToEnd) : Cmd;
internal sealed record SetCursor(int Row) : Cmd;
internal sealed record Activate : Cmd;
internal sealed record Fold(bool Open) : Cmd;
internal sealed record FoldAll : Cmd;
internal sealed record ToggleChange(Change Change) : Cmd;
internal sealed record ToggleFindingsOnly : Cmd;
internal sealed record SetWhole(bool On) : Cmd;
internal sealed record ShowView(RightView View) : Cmd;
internal sealed record Back : Cmd;
internal sealed record SelectFinding(int Index) : Cmd;
internal sealed record SelectSearchHit(int Index) : Cmd;
internal sealed record SetQuery(string Text) : Cmd;
internal sealed record SetSearchScope(bool LayerOnly) : Cmd;
internal sealed record ToggleIncludeDeleted : Cmd;
internal sealed record ToggleExactCase : Cmd;
internal sealed record PickTag : Cmd;
internal sealed record CompareWith(string Tag) : Cmd;
internal sealed record FocusOn(FocusPane Pane) : Cmd;
internal sealed record Quit : Cmd;

internal sealed record Notify(string Text) : Cmd;
internal sealed record RetryLayer(int Layer) : Cmd;
internal sealed record FirstUserLayer : Cmd;
internal sealed record ExtractSelected : Cmd;
internal sealed record CopyCommand : Cmd;
internal sealed record OpenInPager : Cmd;
internal sealed record PickPlatform : Cmd;
internal sealed record SwapSides : Cmd;
internal sealed record StepDifference(int Delta) : Cmd;
internal sealed record ToggleBaseFindings : Cmd;
internal sealed record Redraw : Cmd;