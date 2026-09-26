namespace Valleysoft.Dredge.Explorer;

internal enum FocusPane { Layers, Right }
internal enum RightView { Files, Insights, Inspector, Search, Keys }

internal sealed class ExplorerState
{
    public int Layer { get; set; }
    public FocusPane Focus { get; set; } = FocusPane.Right;
    public RightView View { get; set; } = RightView.Files;
    public bool WholeFilesystem { get; set; }
    public HashSet<Change> Hidden { get; } = [];
    public HashSet<string> Expanded { get; } = new(StringComparer.Ordinal);
    public int Cursor { get; set; }
    public int Scroll { get; set; }
    public int Finding { get; set; }
    public int FindingScroll { get; set; }
    public bool ShowBaseFindings { get; set; }
    public string SearchQuery { get; set; } = "";
    public int SearchCursor { get; set; }
    public int SearchScroll { get; set; }
    public bool SearchLayerOnly { get; set; }
    public bool SearchIncludeDeleted { get; set; } = true;
    public bool SearchExactCase { get; set; }
    public bool FindingsOnly { get; set; }
    public string? Notice { get; set; }
    public bool NoticeIsError { get; set; }
    public int PreviewScroll { get; set; }
    public string InspectPath { get; set; } = "";
    public PreviewContent? Preview { get; set; }
    public int Spinner { get; set; }
    public CompareState? Compare { get; set; }
}

internal sealed record PreviewContent(string Path, string? Language, List<string>? Lines, string? Message, long Bytes);

internal sealed record FlatRow(Node Node, string Path, string Branch, bool Expanded, bool Expandable);

// Builds every pane from ExplorerImage plus ExplorerState. Presenters never
// touch Terminal.Gui; the Tui layer only draws what they return.
internal sealed partial class ExplorerPresenter
{
    public const int LeftWidth = 60;
    public const int DetailsHeight = 13;
    public const int HeaderHeight = 3;
    public const int NarrowLayersHeight = 9;
    public const int MinimumWidth = 80;
    public const int MinimumHeight = 24;
    private const string SpinnerFrames = "⠋⠙⠹⠸⠼⠴⠦⠧⠇⠏";

    private readonly ExplorerImage img;
    private (int Version, List<FlatRow> Rows)? flatCache;
    private (int Version, List<SearchHit> Hits)? searchCache;
    private int version;

    public ExplorerPresenter(ExplorerImage img, int width, int height, KeyMap? keys = null)
    {
        this.img = img;
        Width = width;
        Height = height;
        Keys = keys ?? KeyMap.Default;
    }

    public ExplorerImage Image => img;
    public KeyMap Keys { get; }
    public int Width { get; set; }
    public int Height { get; set; }

    public bool TooSmall => Width < MinimumWidth || Height < MinimumHeight;
    public bool Narrow => Width < 120;
    private int BodyHeight => Height - HeaderHeight - 1;
    private int RightWidth => Narrow ? Width : Width - LeftWidth;
    public int RightInner => RightWidth - 4;
    private int LeftInner => Narrow ? Width - 4 : LeftWidth - 4;
    public int RightInnerHeight => Narrow ? BodyHeight - NarrowLayersHeight - 2 : BodyHeight - 2;
    private int LayersInnerHeight => Narrow ? NarrowLayersHeight - 2 : BodyHeight - DetailsHeight - 2;
    public int TreeRows => Math.Max(1, RightInnerHeight - 5);
    public int SearchRows => Math.Max(1, RightInnerHeight - 9);
    public int InsightRows => Math.Max(4, RightInnerHeight - 6);

    // Every state change bumps the version so cached rows are rebuilt once.
    public void Invalidate() => version++;

    public PaneContent RightPane(ExplorerState s) => s.View switch
    {
        RightView.Insights => InsightsPane(s),
        RightView.Inspector => InspectorPane(s),
        RightView.Search => SearchPane(s),
        RightView.Keys => KeysPane(s),
        _ => FilesPane(s),
    };

    // ───────────────────────────── chrome ─────────────────────────────

    internal static PaneContent Pane(List<Line> content, string title, bool focused, string? subtitle = null) =>
        new(title, subtitle, focused, content);

    internal static Line Keycap(string key) =>
        Line.Of($" {key} ", Theme.S(Theme.Foam, Theme.KeycapBg, Deco.Bold));

    internal static string ShortDigest(string digest) =>
        digest.Length > 24 ? digest[..19] + "…" + digest[^4..] : digest;

    internal static (string Glyph, Rgb Rgb) Glyph(Change c) => c switch
    {
        Change.Added => ("+", Theme.Kelp),
        Change.Modified => ("~", Theme.Ochre),
        Change.Identical => ("=", Theme.Silt),
        Change.Removed => ("−", Theme.Garnet),
        _ => (" ", Theme.Silt),
    };

    private string SpinnerGlyph(ExplorerState s) => SpinnerFrames[s.Spinner % SpinnerFrames.Length].ToString();

    public List<Line> TooSmallMessage() =>
    [
        Line.Blank,
        new Line().Add(" dredge ", Theme.S(Theme.Channel, null, Deco.Bold)).Add(" needs at least ", Theme.Silt)
            .Add($"{MinimumWidth}×{MinimumHeight}", Theme.Foam).Add($" (this terminal is {Width}×{Height}).", Theme.Silt),
        Line.Blank,
        new Line().Add(" Make the window larger, or list files without the explorer:", Theme.Silt),
        new Line().Add(" $ ", Theme.Shale).Add($"dredge image ls {img.Reference} --recursive", Theme.Foam),
        Line.Blank,
        new Line().Add(" Press ", Theme.Silt).Append(Keycap(Keys.Label(KeyAction.Quit))).Add(" to quit.", Theme.Silt),
    ];

    public List<Line> Header(ExplorerState s)
    {
        Line title = new Line()
            .Add(" dredge ", Theme.S(Theme.Channel, null, Deco.Bold))
            .Add(" ")
            .Add(img.Reference, Theme.S(Theme.Foam, null, Deco.Bold))
            .Add("  " + img.Platform, Theme.Silt);
        if (!Narrow)
        {
            title.Add("  " + ShortDigest(img.Digest), Theme.Silt);
        }
        Line right = new();
        if (!img.Complete)
        {
            // Uncompressed sizes aren't known until a layer is indexed; only claim what the manifest says.
            right.Add(Fmt.SizeShort(img.TotalDownload), Theme.Foam).Add(" download", Theme.Silt).Add("   ");
            if (img.ReadyCount < img.LayerCount)
            {
                right.Add(SpinnerGlyph(s) + " ", Theme.Channel)
                    .Add($"{img.ReadyCount} of {img.LayerCount} layers indexed", Theme.Silt);
            }
            else if (img.SessionError is not null)
            {
                right.Add("▲ ", Theme.Garnet).Add("insights unavailable", Theme.Silt);
            }
            else
            {
                right.Add(SpinnerGlyph(s) + " ", Theme.Channel).Add("reading packages", Theme.Silt);
            }
        }
        else
        {
            right.Add(Fmt.SizeShort(img.TotalSize), Theme.Foam).Add(" on disk", Theme.Silt).Add("   ");
            if (!Narrow)
            {
                right.Add(Fmt.SizeShort(img.TotalDownload), Theme.Foam).Add(" download", Theme.Silt).Add("   ");
            }
            int eff = (int)Math.Floor(img.Efficiency * 100);
            Rgb color = eff >= 90 ? Theme.Kelp : eff >= 75 ? Theme.Ochre : Theme.Garnet;
            right.Add($"{eff}%", Theme.S(color, null, Deco.Bold)).Add(" efficient", Theme.Silt);
        }
        right.Add(" ");
        title.PadRight(Width, right);

        (Line bar, Line brackets) = CoreBar(s, Width - 2);
        return [title, new Line().Add(" ").Append(bar), new Line().Add(" ").Append(brackets)];
    }

    private long Weight(int layer) => img.Complete ? img.LayerSize(layer) : img.LayerDownloads[layer];

    // Maps a click on the core bar (header row 1 or 2) back to the stratum under it.
    public int? LayerAtColumn(int col)
    {
        int[] cells = Allocate(img.LayerIndexes.Select(Weight).ToArray(), Width - 2);
        int x = col - 1;
        for (int i = 0, start = 0; i < cells.Length; start += cells[i], i++)
        {
            if (x >= start && x < start + cells[i])
            {
                return i;
            }
        }
        return null;
    }

    // The "core sample": one bar for the whole image, one stratum per layer.
    private (Line Bar, Line Brackets) CoreBar(ExplorerState s, int width)
    {
        long[] sizes = img.LayerIndexes.Select(Weight).ToArray();
        int[] cells = Allocate(sizes, width);

        Line bar = new();
        int baseOrdinal = 0, appOrdinal = 0;
        int baseCells = 0, selStart = -1, selLen = 0, cursor = 0;
        for (int layer = 0; layer < sizes.Length; layer++)
        {
            int n = cells[layer];
            bool isBase = img.IsBase(layer);
            if (isBase)
            {
                baseCells += n;
            }
            if (layer == s.Layer)
            {
                selStart = cursor;
                selLen = n;
            }
            cursor += n;
            if (n == 0)
            {
                continue;
            }
            Rgb color = isBase
                ? (baseOrdinal++ % 2 == 0 ? Theme.Bedrock1 : Theme.Bedrock2)
                : (appOrdinal++ % 2 == 0 ? Theme.Sand1 : Theme.Sand2);
            if (layer == s.Layer)
            {
                color = Theme.Channel;
            }
            if (img.States[layer] != ExplorerLayerState.Ready)
            {
                int done = (int)Math.Round(n * Math.Clamp(img.Progress[layer], 0, 1));
                Rgb pending = img.States[layer] == ExplorerLayerState.Failed ? Theme.Garnet : Theme.Shale;
                bar.Add(new string('▓', done), Theme.Silt).Add(new string('░', n - done), pending);
                continue;
            }
            long reclaimable = img.IsAnalyzed(layer) ? img.Analysis!.Layers[layer].HiddenBytes : 0;
            long size = img.LayerSize(layer);
            int waste = size == 0 || !img.Complete ? 0 : (int)Math.Round(n * Math.Min(1, (double)reclaimable / size));
            bar.Add(new string('█', n - waste), color).Add(new string('█', waste), Theme.Garnet);
        }

        char[] chars = new char[width];
        Rgb[] colors = new Rgb[width];
        Array.Fill(chars, ' ');
        Array.Fill(colors, Theme.Shale);
        void Bracket(int start, int len, string label)
        {
            if (len <= 0 || start + len > width)
            {
                return;
            }
            for (int i = 0; i < len; i++)
            {
                chars[start + i] = '─';
            }
            chars[start] = '╰';
            chars[start + len - 1] = '╯';
            string text = $" {Fmt.Fit(label, Math.Max(0, len - 6))} ";
            if (len >= 8)
            {
                int at = start + (len - text.Length) / 2;
                for (int i = 0; i < text.Length; i++)
                {
                    chars[at + i] = text[i];
                    colors[at + i] = Theme.Silt;
                }
            }
        }
        if (baseCells > 0)
        {
            Bracket(0, baseCells, img.BaseName ?? "base image");
        }
        Bracket(baseCells, width - baseCells, img.RepoName);
        for (int i = selStart; i >= 0 && i < selStart + selLen && i < width; i++)
        {
            if (chars[i] == '─')
            {
                chars[i] = '━';
            }
            colors[i] = Theme.Channel;
        }

        Line brackets = new();
        for (int i = 0; i < width;)
        {
            int j = i;
            while (j < width && colors[j] == colors[i])
            {
                j++;
            }
            brackets.Add(new string(chars, i, j - i), colors[i]);
            i = j;
        }
        return (bar, brackets);
    }

    internal static int[] Allocate(long[] sizes, int width)
    {
        int nonzero = sizes.Count(size => size > 0);
        long total = sizes.Sum();
        if (total <= 0 || width <= 0)
        {
            return new int[sizes.Length];
        }
        if (nonzero > width)
        {
            // More strata than columns: give the largest layers one column each.
            int[] one = new int[sizes.Length];
            foreach (int i in Enumerable.Range(0, sizes.Length).OrderByDescending(i => sizes[i]).Take(width))
            {
                one[i] = 1;
            }
            return one;
        }
        int remaining = width - nonzero;
        double[] exact = sizes.Select(size => size > 0 ? (double)size / total * remaining : 0).ToArray();
        int[] cells = sizes.Select((size, i) => size > 0 ? 1 + (int)exact[i] : 0).ToArray();
        int left = width - cells.Sum();
        foreach (int i in Enumerable.Range(0, sizes.Length)
            .Where(i => sizes[i] > 0)
            .OrderByDescending(i => exact[i] - Math.Floor(exact[i]))
            .Take(left))
        {
            cells[i]++;
        }
        return cells;
    }

    // ───────────────────────────── footer ─────────────────────────────

    public List<Hint> Hints(ExplorerState s)
    {
        string K(KeyAction action) => Keys.Label(action);
        Hint whole = new(K(KeyAction.WholeFilesystem), s.WholeFilesystem ? "This layer" : "Whole filesystem",
            new SetWhole(!s.WholeFilesystem));
        Hint keys = new(K(KeyAction.Help), "Keys", new ShowView(RightView.Keys));
        Hint quit = new(K(KeyAction.Quit), "Quit", new Quit());
        Hint back = new("Esc", "Back", new Back());
        Hint compare = new(K(KeyAction.Compare), "Compare…", new PickTag());
        Hint search = new(K(KeyAction.Search), "Search", new ShowView(RightView.Search));
        Hint insights = new(K(KeyAction.Insights), "Insights", new ShowView(RightView.Insights));
        Hint step = new($"{K(KeyAction.PreviousLayer)} {K(KeyAction.NextLayer)}", "Step layer");
        List<Hint> retry = img.States[s.Layer] == ExplorerLayerState.Failed
            ? [new(K(KeyAction.Retry), "Retry layer", new RetryLayer(s.Layer))] : [];
        return s.View switch
        {
            RightView.Search =>
            [
                new("↑↓", "Select"), new("Enter", "Open", new Activate()),
                new("Alt+L", s.SearchLayerOnly ? "Whole image" : $"Layer {s.Layer} only", new SetSearchScope(!s.SearchLayerOnly)),
                new("Alt+D", s.SearchIncludeDeleted ? "Hide deleted" : "Include deleted", new ToggleIncludeDeleted()),
                new("Alt+C", s.SearchExactCase ? "Ignore case" : "Exact case", new ToggleExactCase()),
                new("Esc", "Close", new Back()),
            ],
            RightView.Insights =>
            [
                new("↑↓", "Finding"), new("Enter", "Show files", new Activate()), new("Tab", "Layers", new FocusOn(FocusPane.Layers)), back, keys, quit,
            ],
            RightView.Inspector =>
            [
                new("↑↓", "Scroll"), step, new(K(KeyAction.Extract), "Extract…", new ExtractSelected()),
                new(K(KeyAction.CopyCommand), "Show command", new CopyCommand()),
                new(K(KeyAction.Pager), "Open in pager", new OpenInPager()),
                back, keys, quit,
            ],
            RightView.Keys => [back, quit],
            _ when s.Focus == FocusPane.Layers =>
            [
                .. retry, new("Tab", "Files", new FocusOn(FocusPane.Right)), new("↑↓", "Layer"), whole, compare, search, insights, keys, quit,
            ],
            _ =>
            [
                .. retry, new("Tab", "Layers", new FocusOn(FocusPane.Layers)), new("↑↓", "Move"), step, whole,
                new($"{K(KeyAction.ToggleAdded)} {K(KeyAction.ToggleModified)} {K(KeyAction.ToggleIdentical)} {K(KeyAction.ToggleDeleted)}", "Filter"),
                new("Enter", "Inspect", new Activate()), search, insights,
                new(K(KeyAction.Extract), "Extract…", new ExtractSelected()), compare, new("←→", "Fold"), keys, quit,
            ],
        };
    }

    public string? Status(ExplorerState s)
    {
        if (s.Notice is not null)
        {
            return s.Notice;
        }
        if (img.Complete || img.ReadyCount == img.LayerCount)
        {
            return null;
        }
        long left = img.LayerIndexes
            .Where(layer => img.States[layer] != ExplorerLayerState.Ready)
            .Sum(layer => (long)(img.LayerDownloads[layer] * (1 - Math.Clamp(img.Progress[layer], 0, 1))));
        int failed = img.States.Count(state => state == ExplorerLayerState.Failed);
        return failed > 0
            ? $"{failed} {(failed == 1 ? "layer" : "layers")} failed · {Fmt.Size(left)} left"
            : $"{Fmt.Size(left)} left to download";
    }

    // Keeps as many hints as fit; help and quit stay so they are always discoverable.
    internal List<Hint> Fit(List<Hint> hints, int room, Func<Hint, int> cost)
    {
        List<Hint> pinned = hints.Where(h => h.Cmd is Quit || (h.Cmd is ShowView { View: RightView.Keys })).ToList();
        int used = pinned.Sum(cost);
        List<Hint> kept = [];
        foreach (Hint h in hints.Except(pinned))
        {
            if (used + cost(h) > room)
            {
                break;
            }
            kept.Add(h);
            used += cost(h);
        }
        return [.. kept, .. pinned];
    }

    // ───────────────────────────── layers ─────────────────────────────

    public PaneContent LayersPane(ExplorerState s)
    {
        bool focused = s.Focus == FocusPane.Layers;
        int w = LeftInner;
        long max = Math.Max(1, img.LayerIndexes.Select(img.LayerSize).DefaultIfEmpty(0).Max());
        ExplorerFinding? finding = s.View == RightView.Insights ? SelectedFinding(s) : null;
        List<(Line Line, int? Layer, bool Selected)> rows = [];
        void AddRow(HistoryRow row) =>
            rows.Add((LayerRow(row, s, w, max, focused, finding), row.Layer, row.Layer == s.Layer));

        string sub = $"{img.LayerCount} with files";
        if (Narrow)
        {
            // Only layers with files; details move to the pane title.
            foreach (HistoryRow row in img.History.Where(h => h.Layer is not null))
            {
                AddRow(row);
            }
            List<ExplorerFinding> related = RelatedFindings(s.Layer);
            if (related.Count > 0)
            {
                sub = $"▲ {related.Count} {(related.Count == 1 ? "finding involves" : "findings involve")} layer {s.Layer}, {Fmt.Size(related.Sum(f => f.Bytes))} · press {Keys.Label(KeyAction.Insights)}";
            }
        }
        else
        {
            bool? previousBase = null;
            foreach (HistoryRow row in img.History)
            {
                if (img.BaseLayerCount is not null && previousBase != row.IsBase)
                {
                    string label = row.IsBase ? img.BaseName ?? "base image" : img.RepoName;
                    rows.Add((new Line().Add("── ", Theme.Shale).Add(label, Theme.Silt).Add(" ")
                        .Add(new string('─', Math.Max(0, w - label.Length - 4)), Theme.Shale), null, false));
                    previousBase = row.IsBase;
                }
                AddRow(row);
            }
        }

        int visible = LayersInnerHeight;
        int at = Math.Max(0, rows.FindIndex(row => row.Selected));
        int start = rows.Count <= visible ? 0 : Math.Clamp(at - visible / 2, 0, rows.Count - visible);
        List<Line> lines = [];
        PaneContent pane = Pane(lines, "Layers", focused, sub);
        foreach ((Line line, int? layer, _) in rows.Skip(start).Take(visible))
        {
            if (layer is int value)
            {
                pane.On(lines.Count, new SelectLayer(value));
                if (img.States[value] == ExplorerLayerState.Failed)
                {
                    pane.On(lines.Count, new RetryLayer(value), 3, 3 + 11);
                }
            }
            lines.Add(line);
        }
        return pane;
    }

    private Line LayerRow(HistoryRow row, ExplorerState s, int w, long max, bool focused, ExplorerFinding? finding)
    {
        bool selected = row.Layer == s.Layer;
        bool implicated = row.Layer is int l && finding?.Layers.Contains(l) == true;
        Line line = new Line().Add(selected ? "▌" : " ", Theme.Channel);
        const int barWidth = 11;
        if (row.Layer is int layer)
        {
            Sty idx = implicated ? Theme.S(Theme.Garnet, null, Deco.Bold)
                : selected ? Theme.S(Theme.Foam, null, Deco.Bold) : Theme.S(Theme.Silt);
            line.Add($"{layer,2} ", idx);
            ExplorerLayerState state = img.States[layer];
            if (state == ExplorerLayerState.Indexing)
            {
                double p = Math.Clamp(img.Progress[layer], 0, 1);
                int filled = (int)Math.Round(p * barWidth);
                line.Add(new string('━', filled), Theme.Channel).Add(new string('─', barWidth - filled), Theme.Shale)
                    .Add(" ").Add($"{(int)(p * 100)}%".PadLeft(8), Theme.Silt);
            }
            else if (state == ExplorerLayerState.Waiting)
            {
                line.Add("waiting".PadRight(barWidth), Theme.Shale).Add(" ")
                    .Add(Fmt.Size(row.Download).PadLeft(8), Theme.Shale);
            }
            else if (state == ExplorerLayerState.Failed)
            {
                line.Add(" ↻ retry ".PadRight(barWidth), Theme.S(Theme.Garnet, Theme.GarnetDeep, Deco.Bold))
                    .Add(" ").Add("failed".PadLeft(8), Theme.Garnet);
            }
            else
            {
                long size = img.LayerSize(layer);
                string bar = Fmt.Bar((double)size / max, barWidth);
                int waste = size > 0 && img.Complete
                    ? (int)Math.Round(bar.Length * Math.Min(1, (double)row.Reclaimable / size)) : 0;
                Rgb color = selected ? Theme.Channel : row.IsBase ? Theme.Bedrock2 : Theme.Sand2;
                line.Add(bar[..(bar.Length - waste)], color)
                    .Add(bar[(bar.Length - waste)..], Theme.Garnet)
                    .Add(new string(' ', barWidth - bar.Length))
                    .Add(" ")
                    .Add(Fmt.Size(size).PadLeft(8), selected ? Theme.S(Theme.Foam, null, Deco.Bold) : Theme.S(row.IsBase ? Theme.Silt : Theme.Foam));
            }
            line.Add("  ");
            foreach (var (text, style) in Syntax.Dockerfile(row.Instruction, row.IsBase && !selected ? Theme.Silt : Theme.Foam))
            {
                line.Add(text, style);
            }
        }
        else
        {
            line.Add(new string(' ', 3 + barWidth + 1 + 8 + 2)).Add(row.Instruction, Theme.Silt);
        }
        line.Truncate(w).Pad(w);
        if (implicated)
        {
            return line.WithBackground(Theme.GarnetDeep);
        }
        return selected ? line.WithBackground(focused ? Theme.ChannelDeep : Theme.Graphite) : line;
    }

    public List<ExplorerFinding> RelatedFindings(int layer) =>
        img.Complete ? img.Findings.Where(f => f.Layers.Contains(layer) && !f.FromBase).ToList() : [];

    public PaneContent DetailsPane(ExplorerState s)
    {
        if (s.View == RightView.Insights && SelectedFinding(s) is ExplorerFinding selected)
        {
            return FindingDetails(selected);
        }
        HistoryRow row = img.Row(s.Layer);
        int w = LeftInner;
        List<Line> lines = [];
        lines.AddRange(Syntax.Wrap(Syntax.Dockerfile(row.Instruction, Theme.Foam), w, 3, indent: 4));
        lines.Add(Line.Blank);
        switch (img.States[s.Layer])
        {
            case ExplorerLayerState.Waiting:
                lines.Add(new Line().Add(Fmt.Size(row.Download), Theme.Foam).Add(" download · waiting to index", Theme.Silt));
                lines.Add(Line.Of(ShortDigest(img.LayerDigests[s.Layer]), Theme.Silt));
                return Pane(lines, $"Layer {s.Layer}", false);
            case ExplorerLayerState.Indexing:
                lines.Add(new Line().Add(SpinnerGlyph(s) + " ", Theme.Channel)
                    .Add($"Indexing this layer… {(int)(img.Progress[s.Layer] * 100)}% of {Fmt.Size(row.Download)}", Theme.Silt));
                lines.Add(Line.Of(ShortDigest(img.LayerDigests[s.Layer]), Theme.Silt));
                return Pane(lines, $"Layer {s.Layer}", false);
            case ExplorerLayerState.Failed:
                lines.Add(new Line().Add("▲ ", Theme.Garnet).Add("This layer failed to index.", Theme.Garnet));
                lines.AddRange(Syntax.Wrap([(img.Errors[s.Layer] ?? "", new Sty(Theme.Silt))], w, 3));
                lines.Add(Line.Blank);
                lines.Add(new Line().Add("Press ", Theme.Silt).Append(Keycap(Keys.Label(KeyAction.Retry)))
                    .Add(" to retry. Other layers keep loading.", Theme.Silt));
                return Pane(lines, $"Layer {s.Layer}", false);
        }

        Line sizes = new Line()
            .Add(Fmt.Size(img.LayerSize(s.Layer)), Theme.Foam).Add(" on disk", Theme.Silt).Add("   ")
            .Add(Fmt.Size(row.Download), Theme.Foam).Add(" download", Theme.Silt);
        if (row.Created.Length > 0)
        {
            sizes.Add("   ").Add(row.Created, Theme.Silt);
        }
        lines.Add(sizes);
        lines.Add(ChangeSummary(Count(img.LayerTree(s.Layer))));
        lines.Add(Line.Of(ShortDigest(img.LayerDigests[s.Layer]), Theme.Silt));

        List<ExplorerFinding> related = RelatedFindings(s.Layer);
        if (related.Count > 0)
        {
            lines.Add(Line.Blank);
            string what = related.Count == 1 ? "1 finding involves this layer" : $"{related.Count} findings involve this layer";
            lines.Add(new Line().Add("▲ ", Theme.Garnet).Add($"{what}, {Fmt.Size(related.Sum(f => f.Bytes))}", Theme.Garnet));
            foreach (ExplorerFinding finding in related.Take(2))
            {
                lines.Add(Line.Of($"  {finding.Title}, {Fmt.Size(finding.Bytes)}", Theme.Silt));
            }
            lines.Add(new Line().Add("  Press ", Theme.Silt).Append(Keycap(Keys.Label(KeyAction.Insights))).Add(" to see insights", Theme.Silt));
        }
        else if (!img.Complete)
        {
            lines.Add(Line.Blank);
            lines.Add(img.SessionError is null
                ? Line.Of("Insights appear after all layers are indexed.", Theme.Silt)
                : new Line().Add("▲ ", Theme.Garnet).Add("Insights are unavailable; press ", Theme.Silt)
                    .Append(Keycap(Keys.Label(KeyAction.Insights))).Add(" for details.", Theme.Silt));
        }
        else if (img.IsBase(s.Layer) && img.BaseName is not null)
        {
            lines.Add(Line.Blank);
            lines.Add(new Line().Add("From the base image ", Theme.Silt).Add(img.BaseName, Theme.Foam));
        }
        return Pane(lines, $"Layer {s.Layer}", false);
    }

    private PaneContent FindingDetails(ExplorerFinding f)
    {
        int w = LeftInner;
        List<Line> lines = [];
        foreach (string text in f.Explain)
        {
            lines.AddRange(text.Length == 0 ? [Line.Blank] : Syntax.Wrap([(text, new Sty(Theme.Foam))], w, 2));
        }
        lines.Add(Line.Blank);
        lines.Add(new Line().Add("Layers  ", Theme.Silt).Add(string.Join(", ", f.Layers), Theme.Foam));
        lines.Add(new Line().Add("Paths   ", Theme.Silt).Add(f.Where, Theme.DirName).Truncate(w));
        lines.Add(new Line().Add("Files   ", Theme.Silt).Add(Fmt.N(f.FileCount), Theme.Foam));
        return Pane(lines, f.Certain ? "Why this is reclaimable" : "Why this may be reclaimable", false);
    }

    internal static Dictionary<Change, int> Count(IEnumerable<Node> nodes)
    {
        Dictionary<Change, int> counts = [];
        foreach (Node n in nodes)
        {
            foreach ((Change change, int count) in n.Counts)
            {
                counts[change] = counts.GetValueOrDefault(change) + count;
            }
        }
        return counts;
    }

    private static Line ChangeSummary(Dictionary<Change, int> counts)
    {
        Line line = new();
        foreach (var (change, label) in new[] { (Change.Added, "added"), (Change.Modified, "modified"), (Change.Identical, "identical"), (Change.Removed, "deleted") })
        {
            int n = counts.GetValueOrDefault(change);
            if (n == 0)
            {
                continue;
            }
            var (glyph, color) = Glyph(change);
            if (line.Length > 0)
            {
                line.Add("   ");
            }
            line.Add(glyph, Theme.S(color, null, Deco.Bold)).Add(" " + Fmt.N(n), Theme.Foam).Add(" " + label, Theme.Silt);
        }
        if (line.Length == 0)
        {
            line.Add("no file changes", Theme.Silt);
        }
        return line;
    }
}
