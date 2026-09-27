namespace Valleysoft.Dredge.Explorer;

internal sealed partial class ExplorerPresenter
{
    // The whole filesystem needs the analysis of every layer up to this one; until
    // then the pane falls back to this layer's changes and says why.
    public bool WholeAvailable(ExplorerState s) => img.IsAnalyzed(s.Layer);

    public List<Node> Tree(ExplorerState s) =>
        s.WholeFilesystem && img.WholeTree(s.Layer) is List<Node> whole ? whole : img.LayerTree(s.Layer);

    public List<FlatRow> Flatten(ExplorerState s)
    {
        if (flatCache is { } cached && cached.Version == version)
        {
            return cached.Rows;
        }
        List<FlatRow> rows = Flatten(Tree(s), s);
        flatCache = (version, rows);
        return rows;
    }

    public int IndexOf(ExplorerState s, string path) => Flatten(s).FindIndex(r => r.Path == path);

    private static List<FlatRow> Flatten(List<Node> roots, ExplorerState s)
    {
        List<FlatRow> rows = [];
        Dictionary<Node, bool> visibleCache = new(ReferenceEqualityComparer.Instance);
        bool Visible(Node n)
        {
            if (s.Hidden.Count == 0)
            {
                return true;
            }
            if (visibleCache.TryGetValue(n, out bool known))
            {
                return known;
            }
            // Replacements can retain removed descendants even when the replacement itself is filtered out.
            bool result = n.Children.Any(Visible) ||
                ((n.Kind != Kind.Dir || n.Children.Count == 0) && !s.Hidden.Contains(n.Change));
            visibleCache[n] = result;
            return result;
        }

        void Walk(List<Node> nodes, string guide, int depth, bool inFinding)
        {
            List<Node> visible = nodes.Where(n => Visible(n) && (!s.FindingsOnly || inFinding || n.ContainsNote)).ToList();
            for (int i = 0; i < visible.Count; i++)
            {
                Node n = visible[i];
                bool last = i == visible.Count - 1;
                string branch = depth == 0 ? "" : guide + (last ? "└─ " : "├─ ");
                bool expandable = n.Children.Count > 0;
                bool expanded = expandable && s.Expanded.Contains(n.Path);
                rows.Add(new FlatRow(n, n.Path, branch, expanded, expandable));
                if (expanded)
                {
                    Walk(n.Children, depth == 0 ? "" : guide + (last ? "   " : "│  "), depth + 1, inFinding || n.Note is not null);
                }
            }
        }

        Walk(roots, "", 0, false);
        return rows;
    }

    public PaneContent FilesPane(ExplorerState s)
    {
        bool focused = s.Focus == FocusPane.Right;
        int w = RightInner;
        if (img.LayerCount == 0)
        {
            return Pane([Line.Of("This image has an empty filesystem.", Theme.Silt)], "Filesystem", focused);
        }
        if (!img.IsIndexed(s.Layer))
        {
            return NotIndexedPane(s, focused, w);
        }
        bool whole = s.WholeFilesystem && WholeAvailable(s);
        List<Node> tree = Tree(s);
        List<FlatRow> rows = Flatten(s);
        Dictionary<Change, int> counts = Count(img.LayerTree(s.Layer));
        if (whole)
        {
            counts[Change.None] = Math.Max(0, Count(tree).Values.Sum() - counts.Values.Sum());
        }

        List<(int Start, int End, Change Change)> chips = [];
        const int MetadataColumns = 35;
        const int MinimumNameColumns = 32;
        bool compact = Narrow || w - 2 - MetadataColumns < MinimumNameColumns;
        List<Line> lines = [ModeToggle(s, w, tree, counts, whole), Chips(s, counts, chips, whole), ColumnHeader(compact)];
        List<(int Line, int Row)> clickable = [];

        int visible = TreeRows;
        if (s.WholeFilesystem && !whole)
        {
            lines.Add(new Line().Add("◌ ", Theme.Ochre)
                .Add("Whole filesystem appears once layers 0–" + s.Layer + " are indexed; showing this layer.", Theme.Silt)
                .Truncate(w));
            visible--;
        }
        s.Cursor = Math.Clamp(s.Cursor, 0, Math.Max(0, rows.Count - 1));
        int scroll = Math.Clamp(s.Scroll, 0, Math.Max(0, rows.Count - visible));
        if (s.Cursor < scroll)
        {
            scroll = s.Cursor;
        }
        if (s.Cursor >= scroll + visible)
        {
            scroll = s.Cursor - visible + 1;
        }
        s.Scroll = scroll;

        bool scrollbar = rows.Count > visible;
        int thumbSize = scrollbar ? Math.Max(1, visible * visible / rows.Count) : 0;
        int thumbStart = scrollbar ? (int)Math.Round((double)scroll / (rows.Count - visible) * (visible - thumbSize)) : 0;

        for (int i = 0; i < visible; i++)
        {
            int r = scroll + i;
            Line line;
            if (r < rows.Count)
            {
                line = TreeRow(rows[r], r == s.Cursor, focused, w - 2, compact);
                clickable.Add((lines.Count, r));
            }
            else if (rows.Count == 0 && i == 1)
            {
                line = Line.Of(s.Hidden.Count > 0 || s.FindingsOnly
                    ? "   Nothing matches the current filters."
                    : "   This layer changes no files.", Theme.Silt).Pad(w - 2);
            }
            else
            {
                line = new Line().Pad(w - 2);
            }
            line.Add(" ");
            if (scrollbar)
            {
                bool thumb = i >= thumbStart && i < thumbStart + thumbSize;
                line.Add(thumb ? "┃" : "│", thumb ? Theme.Channel : Theme.Shale);
            }
            lines.Add(line);
        }

        lines.Add(Line.Of(new string('─', w), Theme.Shale));
        lines.Add(rows.Count > 0 ? SelectionInfo(rows[s.Cursor], w) : Line.Blank);

        string title = whole ? $"Filesystem at layer {s.Layer}" : $"Changes in layer {s.Layer}";
        string? sub = s.FindingsOnly ? "only paths with findings"
            : !img.IsAnalyzed(s.Layer) ? "change kinds appear once earlier layers are indexed" : null;
        PaneContent pane = Pane(lines, title, focused, sub);
        pane.On(0, new SetWhole(false), 0, 12);
        pane.On(0, new SetWhole(true), 12, 30);
        chips.ForEach(c => pane.On(1, new ToggleChange(c.Change), c.Start, c.End));
        clickable.ForEach(c => pane.On(c.Line, new SetCursor(c.Row)));
        return pane;
    }

    private PaneContent NotIndexedPane(ExplorerState s, bool focused, int w)
    {
        List<Line> lines = [Line.Blank];
        ExplorerLayerState state = img.States[s.Layer];
        HistoryRow row = img.Row(s.Layer);
        if (state == ExplorerLayerState.Failed)
        {
            lines.Add(new Line().Add("  ▲ ", Theme.Garnet).Add($"Layer {s.Layer} could not be indexed.", Theme.S(Theme.Garnet, null, Deco.Bold)));
            lines.Add(Line.Blank);
            lines.AddRange(Syntax.Wrap([("  " + (img.Errors[s.Layer] ?? "Unknown error."), new Sty(Theme.Silt))], w, 4));
            lines.Add(Line.Blank);
            lines.Add(new Line().Add("  Press ", Theme.Silt).Append(Keycap(Keys.Label(KeyAction.Retry)))
                .Add(" to try again.", Theme.Silt));
        }
        else
        {
            double p = Math.Clamp(img.Progress[s.Layer], 0, 1);
            int barWidth = Math.Min(40, w - 4);
            int filled = (int)Math.Round(p * barWidth);
            lines.Add(new Line().Add("  " + SpinnerGlyph(s) + " ", Theme.Channel)
                .Add(state == ExplorerLayerState.Indexing ? $"Indexing layer {s.Layer}…" : $"Layer {s.Layer} is next in line…", Theme.Foam));
            lines.Add(Line.Blank);
            lines.Add(new Line().Add("  ").Add(new string('━', filled), Theme.Channel).Add(new string('─', barWidth - filled), Theme.Shale));
            lines.Add(Line.Of($"  {Fmt.Size((long)(row.Download * p))} of {Fmt.Size(row.Download)} read", Theme.Silt));
            lines.Add(Line.Blank);
            lines.Add(Line.Of("  The selected layer is indexed first. Other layers keep loading.", Theme.Silt));
        }
        return Pane(lines, $"Changes in layer {s.Layer}", focused);
    }

    internal static Line Badges(IEnumerable<(int Layer, Change Change)> history)
    {
        Line line = new();
        foreach (var (layer, change) in history)
        {
            var (glyph, color) = Glyph(change);
            line.Add($"{layer}", Theme.Silt).Add(change == Change.None ? "·" : glyph, Theme.S(color, null, Deco.Bold)).Add(" ");
        }
        return line;
    }

    private Line SelectionInfo(FlatRow r, int w)
    {
        Node n = r.Node;
        string display = "/" + r.Path + (n.Kind == Kind.Dir ? "/" : "");
        List<(int Layer, Change Change)> history = img.PathHistory(r.Path);
        Line left = new Line()
            .Add(display, Theme.S(n.Kind == Kind.Dir ? Theme.DirName : Theme.Foam, null, Deco.Bold));
        if (history.Count > 0)
        {
            left.Add("   layers ", Theme.Silt).Append(Badges(history.Count > 8 ? history.Skip(history.Count - 8) : history));
        }
        Line right = new();
        right.Add(Fmt.Size(n.Change == Change.Removed ? n.ShippedSize : n.Size), Theme.Foam);
        if (n.Kind == Kind.Dir)
        {
            right.Add($" in {Fmt.Count(n.FileCount, "file")}", Theme.Silt);
        }
        right.Add("  ").Append(Keycap("Enter")).Add(n.Kind is Kind.File or Kind.Link ? " inspect" : r.Expanded ? " fold" : " unfold", Theme.Silt);
        return left.Truncate(Math.Max(0, w - right.Length - 1)).PadRight(w, right);
    }

    private static Line ModeToggle(ExplorerState s, int w, List<Node> tree, Dictionary<Change, int> counts, bool whole)
    {
        Sty on = Theme.S(Theme.Foam, Theme.KeycapBg, Deco.Bold);
        Sty off = Theme.S(Theme.Silt, Theme.Graphite);
        Line toggle = new Line()
            .Add(" This layer ", s.WholeFilesystem ? off : on)
            .Add(" Whole filesystem ", s.WholeFilesystem ? on : off);
        long size = tree.Where(n => n.Change != Change.Removed).Sum(n => n.Size);
        int paths = counts.Values.Sum();
        Line right = new Line()
            .Add(Fmt.Size(size), Theme.Foam)
            .Add($" in {Fmt.Count(paths, whole ? "file" : "path")}", Theme.Silt);
        return toggle.PadRight(w, right);
    }

    private static Line Chips(ExplorerState s, Dictionary<Change, int> counts, List<(int Start, int End, Change Change)> ranges, bool whole)
    {
        Line line = new();
        List<(Change, string)> kinds =
        [
            (Change.Added, "added"), (Change.Modified, "modified"), (Change.Identical, "identical"), (Change.Removed, "deleted"),
        ];
        if (whole || counts.GetValueOrDefault(Change.None) > 0)
        {
            kinds.Add((Change.None, whole ? "unchanged" : "not yet compared"));
        }
        foreach (var (change, label) in kinds)
        {
            int n = counts.GetValueOrDefault(change);
            var (glyph, color) = Glyph(change);
            if (change == Change.None)
            {
                glyph = "·";
                color = Theme.Foam;
            }
            if (line.Length > 0)
            {
                line.Add(" ");
            }
            int start = line.Length;
            if (s.Hidden.Contains(change))
            {
                ranges.Add((start, start + label.Length + 4, change));
                line.Add($" {glyph} {label} ", Theme.S(Theme.Silt, null, Deco.Strikethrough));
                continue;
            }
            bool zero = n == 0;
            line.Add($" {glyph}", Theme.S(zero ? Theme.Shale : color, Theme.Graphite, Deco.Bold))
                .Add($" {Fmt.N(n)}", Theme.S(zero ? Theme.Silt : Theme.Foam, Theme.Graphite))
                .Add($" {label} ", Theme.S(Theme.Silt, Theme.Graphite));
            ranges.Add((start, line.Length, change));
        }
        return line;
    }

    private static Line ColumnHeader(bool compact)
    {
        Line line = new Line().Add("   ");
        if (!compact)
        {
            line.Add("mode".PadRight(10), Theme.Silt).Add("  ")
                .Add("uid:gid".PadLeft(9), Theme.Silt).Add("  ");
        }
        return line.Add("size".PadLeft(8), Theme.Silt).Add("  ").Add("name", Theme.Silt);
    }

    private static Line TreeRow(FlatRow r, bool selected, bool focused, int w, bool compact)
    {
        Node n = r.Node;
        var (glyph, color) = Glyph(n.Change);
        Line line = new Line()
            .Add(selected ? "▌" : " ", Theme.Channel)
            .Add(glyph + " ", Theme.S(color, null, Deco.Bold));
        if (!compact)
        {
            line.Add(n.Mode.PadRight(10), Theme.Silt).Add("  ")
                .Add(Fmt.Fit(n.Owner, 9).PadLeft(9), Theme.Silt).Add("  ");
        }

        bool emptyDir = n.Kind == Kind.Dir && n.Size == 0;
        long size = n.Change == Change.Removed ? n.ShippedSize : n.Size;
        line.Add((emptyDir || (n.Kind == Kind.Link && !n.HardLink) ? "" : Fmt.Size(size)).PadLeft(8),
            n.Change == Change.Removed ? Theme.S(Theme.Garnet, null, Deco.Strikethrough) : Theme.S(Theme.Foam));
        line.Add("  ");

        int nameWidth = Math.Max(0, w - line.Length);
        Line name = new Line().Add(r.Branch, Theme.Shale);
        name.Add(r.Expandable ? (r.Expanded ? "▾ " : "▸ ") : "  ", Theme.Silt);
        Deco bold = n.Kind == Kind.Dir ? Deco.Bold : Deco.None;
        Sty style = n.Change switch
        {
            Change.Added => Theme.S(Theme.Kelp, null, bold),
            Change.Modified => Theme.S(Theme.Ochre, null, bold),
            Change.Identical => Theme.S(Theme.Silt, null, bold),
            Change.Removed => Theme.S(Theme.Garnet, null, bold | Deco.Strikethrough),
            _ => Theme.S(n.Kind == Kind.Dir ? Theme.DirName : Theme.Foam, null, bold),
        };
        name.Add(n.Kind == Kind.Dir ? n.Name + "/" : n.Name, style);
        if (n.Kind == Kind.Link && n.Target is not null)
        {
            name.Add(n.HardLink ? " ⇒ " : " → ", Theme.Shale).Add(n.Target, Theme.Silt);
        }
        if (n.Kind == Kind.Dir && !r.Expanded && n.Children.Count > 0)
        {
            name.Add($"  {Fmt.Count(n.FileCount, "file")}", Theme.Silt);
        }
        if (n.Note is not null)
        {
            int noteWidth = nameWidth - name.Length - 1;
            if (noteWidth >= 3)
            {
                name.PadRight(nameWidth, Line.Of("▲ " + n.Note, Theme.Garnet).Truncate(noteWidth));
            }
            else
            {
                name.Add(" ▲", Theme.Garnet);
            }
        }
        name.Truncate(nameWidth).Pad(nameWidth);
        line.Append(name);
        return selected ? line.WithBackground(focused ? Theme.ChannelDeep : Theme.Graphite) : line;
    }
}
