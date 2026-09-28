namespace Valleysoft.Dredge.Explorer;

internal enum FocusPane { Layers, Right }
internal enum RightView { Files, Insights, Inspector, Search, Keys, Command, Warning, Packages }

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
    public int KeysScroll { get; set; }
    public int WarningScroll { get; set; }
    public string? WarningText { get; set; }
    public string? WarningTitle { get; set; }
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
    public string? ComparisonStatus { get; set; }
    public int PreviewScroll { get; set; }
    public int PreviewColumn { get; set; }
    public string InspectPath { get; set; } = "";
    public PreviewContent? Preview { get; set; }
    public int Spinner { get; set; }
    public CompareState? Compare { get; set; }
    public int? PackagesLayer { get; set; }
    public InstalledPackageMetadata? Packages { get; set; }
    public string? PackagesError { get; set; }
    public int PackageCursor { get; set; }
    public int PackageScroll { get; set; }
    public string PackageQuery { get; set; } = "";
    public bool PackageSearching { get; set; }
    public HashSet<InstalledPackageEcosystem> CollapsedPackages { get; } = [];
    public InvestigationContext? Investigation { get; set; }
}

internal sealed record InvestigationContext(
    RightView View, int Layer, bool WholeFilesystem, bool FindingsOnly,
    Change[] Hidden, string[] Expanded, int Cursor, int Scroll,
    int SearchCursor, int SearchScroll, int Finding, int FindingScroll)
{
    public static InvestigationContext Capture(ExplorerState s) =>
        new(s.View, s.Layer, s.WholeFilesystem, s.FindingsOnly, [.. s.Hidden], [.. s.Expanded],
            s.Cursor, s.Scroll, s.SearchCursor, s.SearchScroll, s.Finding, s.FindingScroll);

    public void Restore(ExplorerState s)
    {
        s.View = View;
        s.Layer = Layer;
        s.WholeFilesystem = WholeFilesystem;
        s.FindingsOnly = FindingsOnly;
        s.Hidden.Clear();
        s.Hidden.UnionWith(Hidden);
        s.Expanded.Clear();
        s.Expanded.UnionWith(Expanded);
        s.Cursor = Cursor;
        s.Scroll = Scroll;
        s.SearchCursor = SearchCursor;
        s.SearchScroll = SearchScroll;
        s.Finding = Finding;
        s.FindingScroll = FindingScroll;
        s.Focus = FocusPane.Right;
    }
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
    // Whether copying is available, so hints can say "Copy" or "Show".
    public bool Copies { get; init; }
    internal string CopyVerb => Copies ? "Copy" : "Show";
    public int Width { get; set; }
    public int Height { get; set; }
    public bool FullWidthContent { get; set; }

    public bool TooSmall => Width < MinimumWidth || Height < MinimumHeight;
    public bool Narrow => Width < 120;
    private int BodyHeight => Height - HeaderHeight - 1;
    private int RightWidth => FullWidthContent || Narrow ? Width : Width - LeftWidth;
    public int RightInner => RightWidth - 4;
    private int LeftInner => Narrow ? Width - 4 : LeftWidth - 4;
    public int RightInnerHeight => Narrow && !FullWidthContent ? BodyHeight - NarrowLayersHeight - 2 : BodyHeight - 2;
    private int LayersInnerHeight => Narrow ? NarrowLayersHeight - 2 : BodyHeight - DetailsHeight - 2;
    public int TreeRows => Math.Max(1, RightInnerHeight - 5);
    public int SearchRows => Math.Max(1, RightInnerHeight - 9);

    // Every state change bumps the version so cached rows are rebuilt once.
    public void Invalidate() => version++;

    public PaneContent RightPane(ExplorerState s) => s.View switch
    {
        RightView.Insights => InsightsPane(s),
        RightView.Inspector => InspectorPane(s),
        RightView.Search => SearchPane(s),
        RightView.Keys => KeysPane(s),
        RightView.Warning => WarningPane(s),
        RightView.Packages => PackagesPane(s),
        RightView.Command => Pane([Line.Blank, Line.Blank, Line.Blank,
            Line.Of("Use Left/Right or Home/End to read the complete command.", Theme.Silt),
            Line.Of("Ctrl+A selects all. Esc returns to the explorer.", Theme.Silt)],
            "Dredge command", true, "Read-only · scroll horizontally"),
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
            title.Add("  " + img.Digest, Theme.Silt);
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
                right.Add(SpinnerGlyph(s) + " ", Theme.Channel).Add("preparing insights", Theme.Silt);
            }
        }
        else
        {
            right.Add(Fmt.SizeShort(img.TotalSize), Theme.Foam).Add(" file payload", Theme.Silt).Add("   ");
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

    // Each layer's colour in the core bar. Base and app layers alternate between
    // two shades, counting only the layers wide enough to be drawn, so that
    // neighbouring strata always differ.
    private Rgb[] StrataColors(int[] cells)
    {
        Rgb[] colors = new Rgb[cells.Length];
        int baseOrdinal = 0, appOrdinal = 0;
        for (int layer = 0; layer < cells.Length; layer++)
        {
            bool isBase = img.IsBase(layer);
            int ordinal = isBase ? baseOrdinal : appOrdinal;
            colors[layer] = isBase
                ? (ordinal % 2 == 0 ? Theme.Bedrock1 : Theme.Bedrock2)
                : (ordinal % 2 == 0 ? Theme.Sand1 : Theme.Sand2);
            if (cells[layer] > 0)
            {
                _ = isBase ? baseOrdinal++ : appOrdinal++;
            }
        }
        return colors;
    }

    internal Rgb[] StrataColors() => StrataColors(Allocate(img.LayerIndexes.Select(Weight).ToArray(), Width - 2));

    // The "core sample": one bar for the whole image, one stratum per layer.
    private (Line Bar, Line Brackets) CoreBar(ExplorerState s, int width)
    {
        long[] sizes = img.LayerIndexes.Select(Weight).ToArray();
        int[] cells = Allocate(sizes, width);

        Rgb[] strata = StrataColors(cells);
        Line bar = new();
        int selStart = -1, selLen = 0, cursor = 0;
        for (int layer = 0; layer < sizes.Length; layer++)
        {
            int n = cells[layer];
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
            Rgb color = strata[layer];
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
            bar.Add(new string('█', n - waste), color).Add(new string('█', waste), Theme.StratumWaste);
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
        int groupStart = 0, previousLayer = 0;
        foreach (ExplorerBaseImage baseImage in img.BaseImages)
        {
            int end = cells.Take(baseImage.LayerCount).Sum();
            Bracket(groupStart, end - groupStart, img.BaseLabel(baseImage.Name));
            groupStart = end;
            previousLayer = baseImage.LayerCount;
        }
        Bracket(groupStart, cells.Skip(previousLayer).Sum(), img.RepoName);
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
        Hint keys = new(K(KeyAction.Help), "Help", new ShowView(RightView.Keys));
        Hint quit = new(K(KeyAction.Quit), "Quit", new Quit());
        Hint back = new("Esc", "Back", new Back());
        Hint compare = new(K(KeyAction.Compare), "Compare…", new PickTag());
        Hint search = new(K(KeyAction.Search), "Search", new ShowView(RightView.Search));
        Hint insights = new(K(KeyAction.Insights), "Insights", new ShowView(RightView.Insights));
        Hint packages = new(K(KeyAction.Packages), "Packages", new ShowView(RightView.Packages));
        Hint step = new($"{K(KeyAction.PreviousLayer)} {K(KeyAction.NextLayer)}", "Step layer");
        List<Hint> retry = img.LayerCount > 0 && img.States[s.Layer] == ExplorerLayerState.Failed
            ? [new(K(KeyAction.Retry), "Retry layer", new RetryLayer(s.Layer))] : [];
        Hint page = new("PgUp PgDn", "Page", ShowInFooter: false);
        Hint ends = new("Home End", "First or last", ShowInFooter: false);
        Hint findingsOnly = new(K(KeyAction.FindingsOnly), s.FindingsOnly ? "All paths" : "Findings only", new ToggleFindingsOnly());
        List<Hint> clear = s.Investigation is { } investigation
            ? [new("Esc", investigation.View == RightView.Search ? "Back to search" : "Back to insights", new Back())]
            : s.FindingsOnly || s.Hidden.Count > 0 ? [new("Esc", "Clear filters", new Back())] : [];
        return s.View switch
        {
            RightView.Packages => PackageHints(s),
            RightView.Search =>
            [
                new("↑↓", "Select"), new("Enter", "Open", new Activate()), page,
                .. img.LayerCount > 0 ? new Hint[] { new("Alt+L", s.SearchLayerOnly ? "Whole image" : $"Layer {s.Layer} only", new SetSearchScope(!s.SearchLayerOnly)) } : [],
                new("Alt+D", s.SearchIncludeDeleted ? "Hide deleted" : "Include deleted", new ToggleIncludeDeleted()),
                new("Alt+C", s.SearchExactCase ? "Ignore case" : "Exact case", new ToggleExactCase()),
                new("Esc", "Close", new Back()),
            ],
            RightView.Insights =>
            [
                .. img.BaseWarning is not null ? new Hint[] { new("Alt+W", "Base warning", new ShowView(RightView.Warning)) } : [],
                new("↑↓", "Finding"), new("Enter", "Show files", new Activate()),
                .. img.LayerCount > 0 ? new Hint[] { new("Tab", "Layers", new FocusOn(FocusPane.Layers)) } : [], back,
                packages, ends, search, keys, quit,
            ],
            RightView.Inspector =>
            [
                new("↑↓", "Scroll", ShowInFooter: false), new("←→", "Pan text", ShowInFooter: false), new(K(KeyAction.Extract), "Extract…", new ExtractSelected()),
                new(K(KeyAction.CopyCommand), $"{CopyVerb} command", new CopyCommand()),
                new(K(KeyAction.Viewer), "Open file in text viewer", new OpenInViewer()),
                back, page, ends, keys, quit,
            ],
            RightView.Keys or RightView.Warning => [new("↑↓", "Scroll"), page, ends, back, quit],
            RightView.Command => [new("←→", "Scroll"), new("Ctrl+A", "Select all"), ends, back, keys, quit],
            _ when img.LayerCount == 0 => [packages, compare, insights, search, keys, back, quit],
            _ when s.Focus == FocusPane.Layers =>
            [
                .. retry, new("Tab", "Files", new FocusOn(FocusPane.Right)), packages, new("↑↓", "Layer", ShowInFooter: false), whole, compare, search, insights,
                ends, .. clear, keys, quit,
            ],
            _ =>
            [
                .. retry, new("Tab", "Layers", new FocusOn(FocusPane.Layers)), packages, new("↑↓", "Move", ShowInFooter: false), step, whole,
                new($"{K(KeyAction.ToggleAdded)} {K(KeyAction.ToggleModified)} {K(KeyAction.ToggleIdentical)} {K(KeyAction.ToggleDeleted)}", "Filter"),
                new("Enter", "Inspect", new Activate()), search, insights,
                new(K(KeyAction.Extract), "Extract…", new ExtractSelected()), compare, new("←→", "Fold", ShowInFooter: false), .. clear, findingsOnly,
                page, ends, keys, quit,
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
        List<Hint> pinned = hints.Where(h => h.Cmd is Quit or Back || (h.Cmd is ShowView { View: RightView.Keys })).ToList();
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
        if (img.LayerCount == 0)
        {
            return Pane([Line.Of("No filesystem layers.", Theme.Silt)], "Layers", focused);
        }
        // The selection marker takes the pane's left padding, leaving more room for instructions.
        int w = LeftInner + 1;
        long max = Math.Max(1, img.LayerIndexes.Select(img.LayerSize).DefaultIfEmpty(0).Max());
        Rgb[] strata = StrataColors();
        ExplorerFinding? finding = s.View == RightView.Insights ? SelectedFinding(s) : null;
        List<(Line Line, int? Layer, bool Selected)> rows = [];
        void AddRow(HistoryRow row) =>
            rows.Add((LayerRow(row, s, w, max, strata, focused, finding), row.Layer, row.Layer == s.Layer));

        string sub = $"{img.LayerCount} with files";
        if (Narrow)
        {
            // Only layers with files; details move to the pane title.
            foreach (HistoryRow row in img.History.Where(h => h.Layer is not null))
            {
                AddRow(row);
            }
            if (img.BaseLayerCount is not null)
            {
                sub = img.GroupAt(s.Layer);
            }
            List<ExplorerFinding> related = RelatedFindings(s.Layer);
            if (related.Count > 0)
            {
                sub = $"▲ {related.Count} {(related.Count == 1 ? "finding involves" : "findings involve")} layer {s.Layer}, {Fmt.Size(related.Sum(f => f.Bytes))} · press {Keys.Label(KeyAction.Insights)}";
            }
        }
        else
        {
            string? previousGroup = null;
            for (int index = 0; index < img.History.Count; index++)
            {
                HistoryRow row = img.History[index];
                int? layer = row.Layer ?? img.History.Skip(index + 1).FirstOrDefault(next => next.Layer is not null)?.Layer;
                string group = layer is int value ? img.GroupAt(value) : img.Reference;
                if (img.BaseLayerCount is not null && previousGroup != group)
                {
                    string label = group;
                    rows.Add((new Line().Add(" ── ", Theme.Shale).Add(label, Theme.Silt).Add(" ")
                        .Add(new string('─', Math.Max(0, w - label.Length - 5)), Theme.Shale), null, false));
                    previousGroup = group;
                }
                AddRow(row);
            }
        }

        int visible = LayersInnerHeight;
        int at = Math.Max(0, rows.FindIndex(row => row.Selected));
        int start = rows.Count <= visible ? 0 : Math.Clamp(at - visible / 2, 0, rows.Count - visible);
        List<Line> lines = [];
        PaneContent pane = Pane(lines, "Layers", focused, sub) with { Inset = 0 };
        foreach ((Line line, int? layer, _) in rows.Skip(start).Take(visible))
        {
            if (layer is int value)
            {
                pane.On(lines.Count, new SelectLayer(value));
                if (img.States[value] == ExplorerLayerState.Failed)
                {
                    pane.On(lines.Count, new RetryLayer(value), 1 + IndexWidth + 1, 1 + IndexWidth + 1 + GaugeWidth);
                }
            }
            lines.Add(line);
        }
        return pane;
    }

    private Line LayerRow(HistoryRow row, ExplorerState s, int w, long max, Rgb[] strata, bool focused, ExplorerFinding? finding)
    {
        bool selected = row.Layer == s.Layer;
        bool implicated = row.Layer is int l && finding?.Layers.Contains(l) == true;
        Line line = new Line().Add(selected ? "▌" : " ", Theme.Channel);
        Rgb under = implicated ? Theme.GarnetDeep : selected ? focused ? Theme.ChannelDeep : Theme.Graphite : Theme.Ground;
        if (row.Layer is int layer)
        {
            Sty idx = implicated ? Theme.S(Theme.Garnet, null, Deco.Bold)
                : selected ? Theme.S(Theme.Foam, null, Deco.Bold) : Theme.S(Theme.Silt);
            line.Add(layer.ToString(System.Globalization.CultureInfo.InvariantCulture).PadLeft(IndexWidth) + " ", idx);
            ExplorerLayerState state = img.States[layer];
            if (state == ExplorerLayerState.Indexing)
            {
                double p = Math.Clamp(img.Progress[layer], 0, 1);
                Gauge(line, $"{(int)(p * 100)}%", p * GaugeWidth, 0, Theme.S(Theme.Silt), Theme.Silt, under);
            }
            else if (state == ExplorerLayerState.Waiting)
            {
                line.Add("waiting".PadLeft(GaugeWidth), Theme.Silt);
            }
            else if (state == ExplorerLayerState.Failed)
            {
                line.Add("↻ retry".PadLeft(GaugeWidth), Theme.S(Theme.Garnet, Theme.GarnetDeep, Deco.Bold));
            }
            else
            {
                long size = img.LayerSize(layer);
                double waste = size > 0 && img.Complete ? Math.Min(1, (double)row.Reclaimable / size) : 0;
                Rgb fill = strata[layer];
                Sty text = Theme.S(Theme.Foam, null, selected ? Deco.Bold : Deco.None);
                Gauge(line, Fmt.Size(size), GaugeReach(max <= 0 ? 0 : (double)size / max), waste, text, fill,
                    implicated ? Theme.GarnetDeep : Theme.Ground);
            }
            line.Add(new string(' ', GaugeGap));
            foreach (var (text, style) in Syntax.Dockerfile(row.Instruction, row.IsBase && !selected ? Theme.Silt : Theme.Foam))
            {
                line.Add(text, style);
            }
        }
        else
        {
            line.Add(new string(' ', IndexWidth + 1 + GaugeWidth + GaugeGap)).Add(row.Instruction, Theme.Silt);
        }
        line.Truncate(w).Pad(w);
        if (implicated)
        {
            return line.UnderBackground(Theme.GarnetDeep);
        }
        return selected ? line.UnderBackground(focused ? Theme.ChannelDeep : Theme.Graphite) : line;
    }

    // Wide enough for any size, such as "530.5 MB", plus a gap after the index.
    // Wide enough for the longest size, 999.9 MB.
    internal const int GaugeWidth = 8;
    private const int GaugeGap = 1;

    // Only as many digits as the highest layer index needs.
    private int IndexWidth => Math.Max(1, (img.LayerCount - 1).ToString(System.Globalization.CultureInfo.InvariantCulture).Length);

    // How far across the gauge a size reaches. One large layer (often the
    // runtime or SDK) would squash every other layer into the first cell on a
    // linear scale, so the square root spreads the rest out while keeping order.
    // Any nonzero size shows at least a sliver.
    internal static double GaugeReach(double fraction)
    {
        fraction = Math.Clamp(fraction, 0, 1);
        return fraction <= 0 ? 0 : Math.Max(Math.Sqrt(fraction) * GaugeWidth, MinReach);
    }

    private const double MinReach = 0.35;

    // Right-aligns the text and shades reach cells from the left, so the number
    // doubles as the bar. The cell where the shading ends is blended with the
    // row's background by how much of it is covered, giving sub-cell steps. The
    // wasted share of the shading, at its end, is red.
    private static void Gauge(Line line, string text, double reach, double waste, Sty style, Rgb fill, Rgb under)
    {
        string padded = text.PadLeft(GaugeWidth);
        double kept = reach * (1 - Math.Clamp(waste, 0, 1));
        for (int i = 0; i < GaugeWidth; i++)
        {
            double cover = Math.Clamp(reach - i, 0, 1);
            string cell = padded[i].ToString();
            if (cover <= 0)
            {
                line.Add(cell, style);
            }
            else if (Theme.NoColor)
            {
                line.Add(cell, style with { Deco = style.Deco | Deco.Underline });
            }
            else
            {
                Rgb bg = Blend(under, i + cover / 2 < kept ? fill : Theme.StratumWaste, cover);
                line.Add(cell, style with { Background = bg });
            }
        }
    }

    private static Rgb Blend(Rgb from, Rgb to, double amount) => new(
        (int)Math.Round(from.R + (to.R - from.R) * amount),
        (int)Math.Round(from.G + (to.G - from.G) * amount),
        (int)Math.Round(from.B + (to.B - from.B) * amount));

    public List<ExplorerFinding> RelatedFindings(int layer) =>
        img.Complete ? img.Findings.Where(f => f.Layers.Contains(layer) && !f.FromBase).ToList() : [];

    public PaneContent DetailsPane(ExplorerState s)
    {
        if (img.LayerCount == 0)
        {
            return Pane([Line.Of("This image has no layers.", Theme.Silt)], "Image", false);
        }
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
            .Add(Fmt.Size(img.LayerSize(s.Layer)), Theme.Foam).Add(" file payload", Theme.Silt).Add("   ")
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
        else if (img.BaseImageAt(s.Layer) is string baseName)
        {
            lines.Add(Line.Blank);
            lines.Add(new Line().Add("From the base image ", Theme.Silt).Add(baseName, Theme.Foam));
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
