namespace Valleysoft.Dredge.Explorer;

internal enum CompareRowKind { Section, Package, Scope, Unchanged, Dir, File }

internal sealed record CompareRow(
    string Key, CompareRowKind Kind, string Branch, string Name, Change Change,
    long? Before = null, long? After = null, string? Versions = null,
    bool Expandable = false, bool Expanded = false, int? Files = null,
    ExplorerPackageDifference? Package = null, string? Path = null);

internal sealed record TextDiffContent(
    string Path, IReadOnlyList<DiffLine>? Lines, string? Message);

internal sealed record PackageFilesContent(
    ExplorerPackageDifference Package, IReadOnlyList<(string Path, Change Change)>? Files, string? Message, int Total);

internal sealed class CompareState
{
    public CompareState(ExplorerComparison comparison, string baselineLabel, string targetLabel)
    {
        Comparison = comparison;
        BaselineLabel = baselineLabel;
        TargetLabel = targetLabel;
        Layer = Differences().DefaultIfEmpty(0).First();
    }

    public ExplorerComparison Comparison { get; set; }
    public string BaselineLabel { get; set; }
    public string TargetLabel { get; set; }
    public int Layer { get; set; }
    public bool FocusLayers { get; set; }
    public int Cursor { get; set; }
    public int Scroll { get; set; }
    public HashSet<string> Expanded { get; } = new(StringComparer.Ordinal) { "section:packages", "section:files" };
    public TextDiffContent? Diff { get; set; }
    public int DiffScroll { get; set; }
    public PackageFilesContent? PackageFiles { get; set; }
    public bool Busy { get; set; }

    public int LayerCount => Math.Max(Comparison.Baseline.Resolved.Manifest.Layers.Length, Comparison.Target.Resolved.Manifest.Layers.Length);

    public string? BaselineDigest(int layer) => Digest(Comparison.Baseline, layer);
    public string? TargetDigest(int layer) => Digest(Comparison.Target, layer);
    public long? BaselineSize(int layer) => Size(Comparison.Baseline, layer);
    public long? TargetSize(int layer) => Size(Comparison.Target, layer);
    public bool Shared(int layer) => BaselineDigest(layer) is string digest && digest == TargetDigest(layer);

    public IEnumerable<int> Differences() => Enumerable.Range(0, LayerCount).Where(layer => !Shared(layer));

    public static int? FirstDifference(ExplorerComparison comparison) =>
        new CompareState(comparison) is var probe ? probe.Differences().Cast<int?>().FirstOrDefault() : null;

    private CompareState(ExplorerComparison comparison)
    {
        Comparison = comparison;
        BaselineLabel = TargetLabel = "";
    }

    private static string? Digest(ExplorerSession session, int layer) =>
        layer < session.Resolved.Manifest.Layers.Length ? session.Resolved.Manifest.Layers[layer].Digest : null;

    private static long? Size(ExplorerSession session, int layer) =>
        layer < session.Resolved.Manifest.Layers.Length ? session.Resolved.Manifest.Layers[layer].Size : null;
}

// `c` in the explorer: this image (the baseline) against another tag, layer by layer.
internal sealed class CompareView
{
    private readonly ExplorerPresenter presenter;
    private readonly CompareState c;
    private readonly int width;
    private readonly int height;
    private List<CompareRow>? rows;

    public CompareView(ExplorerPresenter presenter, CompareState state)
    {
        this.presenter = presenter;
        c = state;
        width = presenter.Width;
        height = presenter.Height;
    }

    private bool Narrow => presenter.Narrow;
    private int RightInner => presenter.RightInner;
    private int RightInnerHeight => presenter.RightInnerHeight;
    private int LeftInner => Narrow ? width - 4 : ExplorerPresenter.LeftWidth - 4;
    public int DiffRows => Math.Max(1, RightInnerHeight - (c.PackageFiles is null ? 5 : 12));

    public List<Line> Header()
    {
        int shared = Enumerable.Range(0, c.LayerCount).Count(c.Shared);
        long delta = c.Comparison.Target.Analysis.FileBytes - c.Comparison.Baseline.Analysis.FileBytes;
        Line title = new Line()
            .Add(" dredge ", Theme.S(Theme.Channel, null, Deco.Bold))
            .Add(" ")
            .Add(c.BaselineLabel, Theme.S(Theme.Silt, null, Deco.Bold))
            .Add(" → ", Theme.Shale)
            .Add(c.TargetLabel, Theme.S(Theme.Foam, null, Deco.Bold));
        if (!Narrow)
        {
            title.Add("  " + presenter.Image.Platform, Theme.Silt);
        }
        Line right = new Line()
            .Add($"{shared} of {c.LayerCount}", Theme.Foam).Add(" layers shared", Theme.Silt).Add("   ")
            .Add(Signed(delta), delta > 0 ? Theme.Ochre : delta < 0 ? Theme.Kelp : Theme.Silt).Add(" on disk", Theme.Silt).Add("   ")
            .Add(Fmt.SizeShort(c.Comparison.AdditionalDownloadBytes), Theme.Foam).Add(" to download ", Theme.Silt);
        title.PadRight(width, right);
        int labelWidth = Math.Min(16, Math.Max(c.BaselineLabel.Length, c.TargetLabel.Length)) + 3;
        int barWidth = width - labelWidth - 2;
        long max = Math.Max(1, Math.Max(Total(c.BaselineSize), Total(c.TargetSize)));
        return
        [
            title,
            Bar(c.BaselineLabel, c.BaselineSize, barWidth, max, labelWidth, false),
            Bar(c.TargetLabel, c.TargetSize, barWidth, max, labelWidth, true),
        ];
    }

    private long Total(Func<int, long?> size) => Enumerable.Range(0, c.LayerCount).Sum(layer => size(layer) ?? 0);

    internal static string Signed(long delta) =>
        delta == 0 ? "±0" : (delta > 0 ? "+" : "−") + Fmt.Size(Math.Abs(delta));

    private Line Bar(string label, Func<int, long?> sizes, int barWidth, long max, int labelWidth, bool target)
    {
        long sum = Total(sizes);
        int total = (int)Math.Round((double)barWidth * sum / max);
        Line line = new Line().Add(" " + Fmt.Fit(label, labelWidth - 3).PadRight(labelWidth - 1), target ? Theme.Foam : Theme.Silt);
        int used = 0;
        long running = 0;
        int ordinal = 0;
        for (int layer = 0; layer < c.LayerCount; layer++)
        {
            long size = sizes(layer) ?? 0;
            running += size;
            int end = sum == 0 ? 0 : (int)Math.Round((double)running / sum * total);
            int n = Math.Max(size > 0 ? 1 : 0, end - used);
            used += n;
            if (n == 0)
            {
                continue;
            }
            Rgb color = layer == c.Layer ? Theme.Channel
                : c.Shared(layer) ? (ordinal % 2 == 0 ? Theme.Shale : Theme.Shared2)
                : (ordinal % 2 == 0 ? Theme.Ochre : Theme.OchreDeep);
            ordinal++;
            // Three-quarter blocks leave a sliver between the two stacked bars.
            line.Add(new string('▆', n), color);
        }
        return line.Truncate(width);
    }

    private string[] Instructions(ExplorerSession session) =>
        new ExplorerImage(session.Image.ToString(), "", "",
            session.Resolved.Manifest.Layers.Select(l => l.Digest ?? "").ToArray(),
            session.Resolved.Manifest.Layers.Select(l => l.Size).ToArray(),
            session.Config.History, null, null, null).Instructions.ToArray();

    private string[]? baselineInstructions, targetInstructions;
    private string Instruction(int layer)
    {
        targetInstructions ??= Instructions(c.Comparison.Target);
        baselineInstructions ??= Instructions(c.Comparison.Baseline);
        return layer < targetInstructions.Length ? targetInstructions[layer]
            : layer < baselineInstructions.Length ? baselineInstructions[layer] : "";
    }

    public PaneContent Layers()
    {
        int w = LeftInner;
        List<Line> lines =
        [
            new Line().Add("    ").Add(Fmt.Fit(c.TargetLabel, 8).PadLeft(8), Theme.Shale).Add("   ")
                .Add(("vs " + Fmt.Fit(c.BaselineLabel, 6)).PadLeft(9), Theme.Shale),
        ];
        int visible = Narrow ? ExplorerPresenter.NarrowLayersHeight - 3 : height - ExplorerPresenter.HeaderHeight - 1 - ExplorerPresenter.DetailsHeight - 3;
        int start = c.LayerCount <= visible ? 0 : Math.Clamp(c.Layer - visible / 2, 0, c.LayerCount - visible);
        PaneContent pane = ExplorerPresenter.Pane(lines, "Layers", c.FocusLayers, $"{c.TargetLabel} vs {c.BaselineLabel}");
        for (int layer = start; layer < Math.Min(c.LayerCount, start + visible); layer++)
        {
            bool selected = layer == c.Layer;
            bool same = c.Shared(layer);
            Line line = new Line().Add(selected ? "▌" : " ", Theme.Channel);
            line.Add($"{layer,2} ", selected ? Theme.S(Theme.Foam, null, Deco.Bold) : Theme.S(Theme.Silt))
                .Add((c.TargetSize(layer) is long t ? Fmt.Size(t) : "—").PadLeft(8), same ? Theme.Silt : Theme.Foam)
                .Add(same ? " = " : " ≠ ", same ? Theme.S(Theme.Shale) : Theme.S(Theme.Ochre, null, Deco.Bold));
            if (same)
            {
                line.Add("same".PadLeft(9), Theme.Shale);
            }
            else if (c.BaselineSize(layer) is null)
            {
                line.Add("new".PadLeft(9), Theme.Ochre);
            }
            else if (c.TargetSize(layer) is null)
            {
                line.Add("gone".PadLeft(9), Theme.Kelp);
            }
            else
            {
                long delta = c.TargetSize(layer)!.Value - c.BaselineSize(layer)!.Value;
                line.Add(Signed(delta).PadLeft(9), delta > 0 ? Theme.Ochre : delta < 0 ? Theme.Kelp : Theme.Silt);
            }
            line.Add("  ");
            foreach (var (text, style) in Syntax.Dockerfile(Instruction(layer), same ? Theme.Silt : Theme.Foam))
            {
                line.Add(text, same ? new Sty(Theme.Silt) : style);
            }
            line.Truncate(w).Pad(w);
            pane.On(lines.Count, new SelectLayer(layer));
            lines.Add(selected ? line.WithBackground(c.FocusLayers ? Theme.ChannelDeep : Theme.Graphite) : line);
        }
        return pane;
    }

    public PaneContent Details()
    {
        int w = LeftInner;
        int layer = c.Layer;
        List<Line> lines = [];
        lines.AddRange(Syntax.Wrap(Syntax.Dockerfile(Instruction(layer), Theme.Foam), w, 2, indent: 4));
        lines.Add(Line.Blank);
        int labelWidth = Math.Min(12, Math.Max(c.BaselineLabel.Length, c.TargetLabel.Length)) + 2;
        void Side(string label, string? digest, long? size)
        {
            Line line = new Line().Add(Fmt.Fit(label, labelWidth - 2).PadRight(labelWidth), Theme.Silt);
            if (digest is null)
            {
                line.Add("no layer at this position", Theme.Shale);
            }
            else
            {
                line.Add(ExplorerPresenter.ShortDigest(digest), Theme.Silt).Add("  " + Fmt.Size(size ?? 0), Theme.Foam);
            }
            lines.Add(line.Truncate(w));
        }
        Side(c.TargetLabel, c.TargetDigest(layer), c.TargetSize(layer));
        Side(c.BaselineLabel, c.BaselineDigest(layer), c.BaselineSize(layer));
        lines.Add(Line.Blank);
        if (c.Shared(layer))
        {
            lines.Add(Line.Of("Same digest in both images, so this layer is shared.", Theme.Silt));
        }
        else if (c.TargetDigest(layer) is string digest)
        {
            bool elsewhere = c.Comparison.Baseline.Resolved.Manifest.Layers.Any(l => l.Digest == digest);
            lines.Add(elsewhere
                ? Line.Of("This layer exists elsewhere in the baseline, so it is not downloaded again.", Theme.Silt)
                : new Line().Add("Additional download  ", Theme.Silt).Add(Fmt.Size(c.TargetSize(layer) ?? 0), Theme.Foam).Add(" for this layer", Theme.Silt));
        }
        else
        {
            lines.Add(Line.Of($"{c.TargetLabel} has fewer layers than {c.BaselineLabel}.", Theme.Silt));
        }
        lines.Add(Line.Blank);
        int firstDiff = c.Differences().DefaultIfEmpty(c.LayerCount).First();
        lines.Add(Line.Of(firstDiff == 0 ? "No layers are shared from the start."
            : firstDiff >= c.LayerCount ? "Every layer is shared."
            : $"Layers 0 to {firstDiff - 1} are shared.", Theme.Silt));
        lines.Add(Line.Of($"Pulling {c.TargetLabel} where {c.BaselineLabel} exists downloads", Theme.Silt).Truncate(w));
        lines.Add(new Line().Add(Fmt.Size(c.Comparison.AdditionalDownloadBytes), Theme.Foam).Add(" of unshared layers.", Theme.Silt));
        return ExplorerPresenter.Pane(lines, $"Layer {layer}", false);
    }

    // ───────────────────────────── right pane ─────────────────────────────

    public List<CompareRow> Rows() => rows ??= BuildRows();

    private List<CompareRow> BuildRows()
    {
        List<CompareRow> list = [];
        IReadOnlyList<ExplorerPackageDifference> packages = c.Comparison.Packages;
        bool packagesOpen = c.Expanded.Contains("section:packages");
        list.Add(new("section:packages", CompareRowKind.Section, "", "Installed packages", Change.None,
            Expandable: true, Expanded: packagesOpen, Files: packages.Count));
        if (packagesOpen)
        {
            foreach (InstalledPackageEcosystem ecosystem in Enum.GetValues<InstalledPackageEcosystem>())
            {
                List<ExplorerPackageDifference> changes = packages.Where(p => p.Ecosystem == ecosystem).ToList();
                if (!Available(ecosystem))
                {
                    continue;
                }
                int unchanged = Math.Max(0, c.Comparison.Target.Packages.Ecosystems[ecosystem].Packages.Count
                    - changes.Count(p => p.TargetVersion is not null));
                if (changes.Count == 0 && unchanged == 0)
                {
                    continue;
                }
                List<(string Key, List<ExplorerPackageDifference> Items)> groups = changes
                    .GroupBy(p => ecosystem == InstalledPackageEcosystem.Npm && p.Name.StartsWith('@') && p.Name.Contains('/')
                        ? p.Name[..p.Name.IndexOf('/')] : p.Name)
                    .Select(g => (g.Key, g.ToList())).ToList();
                int count = groups.Count + (unchanged > 0 ? 1 : 0);
                int index = 0;
                list.Add(new($"eco:{ecosystem}", CompareRowKind.Section, "   ", EcosystemName(ecosystem), Change.None));
                foreach ((string key, List<ExplorerPackageDifference> items) in groups)
                {
                    string branch = "   " + (++index == count ? "└─ " : "├─ ");
                    if (items.Count > 1 || (items.Count == 1 && items[0].Name != key))
                    {
                        string scopeKey = $"scope:{ecosystem}:{key}";
                        bool open = c.Expanded.Contains(scopeKey);
                        Change change = items.All(i => i.BaselineVersion is null) ? Change.Added
                            : items.All(i => i.TargetVersion is null) ? Change.Removed : Change.Modified;
                        list.Add(new(scopeKey, CompareRowKind.Scope, branch, key + "/", change,
                            Versions: $"{items.Count} packages changed", Expandable: true, Expanded: open));
                        if (open)
                        {
                            string guide = "   " + (index == count ? "   " : "│  ");
                            for (int i = 0; i < items.Count; i++)
                            {
                                list.Add(PackageRow(items[i], guide + (i == items.Count - 1 ? "└─ " : "├─ ")));
                            }
                        }
                    }
                    else
                    {
                        list.Add(PackageRow(items[0], branch));
                    }
                }
                if (unchanged > 0)
                {
                    list.Add(new($"same:{ecosystem}", CompareRowKind.Unchanged, "   └─ ", $"{Fmt.N(unchanged)} unchanged", Change.Identical));
                }
            }
            if (packages.Count == 0 && !Enum.GetValues<InstalledPackageEcosystem>().Any(Available))
            {
                list.Add(new("pkg:none", CompareRowKind.Unchanged, "   ", "No package database found in either image", Change.None));
            }
        }

        bool filesOpen = c.Expanded.Contains("section:files");
        list.Add(new("section:files", CompareRowKind.Section, "", "Files", Change.None,
            Expandable: true, Expanded: filesOpen, Files: c.Comparison.Files.Count));
        if (filesOpen)
        {
            FileTree tree = FileTree.Build(c.Comparison.Files);
            void Walk(List<FileTree> nodes, string guide)
            {
                for (int i = 0; i < nodes.Count; i++)
                {
                    FileTree n = nodes[i];
                    bool last = i == nodes.Count - 1;
                    string key = "file:" + n.Path;
                    bool expandable = n.Children.Count > 0;
                    bool open = expandable && c.Expanded.Contains(key);
                    list.Add(new(key, expandable || n.Dir ? CompareRowKind.Dir : CompareRowKind.File,
                        guide + (last ? "└─ " : "├─ "), n.Name, n.Change, n.Before, n.After,
                        Expandable: expandable, Expanded: open, Files: expandable ? n.FileCount : null, Path: n.Path));
                    if (open)
                    {
                        Walk(n.Children, guide + (last ? "   " : "│  "));
                    }
                }
            }
            Walk(tree.Children, "");
            if (c.Comparison.Files.Count == 0)
            {
                list.Add(new("file:none", CompareRowKind.Unchanged, "   ", "No file differences", Change.None));
            }
        }
        return list;
    }

    private bool Available(InstalledPackageEcosystem ecosystem) =>
        c.Comparison.Baseline.Packages.Ecosystems[ecosystem].Availability == InstalledPackageMetadataAvailability.Available &&
        c.Comparison.Target.Packages.Ecosystems[ecosystem].Availability == InstalledPackageMetadataAvailability.Available;

    private static string EcosystemName(InstalledPackageEcosystem ecosystem) => ecosystem switch
    {
        InstalledPackageEcosystem.Npm => "npm",
        InstalledPackageEcosystem.Dpkg => "dpkg",
        InstalledPackageEcosystem.Apk => "apk",
        _ => "pip",
    };

    private static CompareRow PackageRow(ExplorerPackageDifference p, string branch)
    {
        Change change = p.BaselineVersion is null ? Change.Added : p.TargetVersion is null ? Change.Removed : Change.Modified;
        string versions = change switch
        {
            Change.Added => p.TargetVersion!,
            Change.Removed => p.BaselineVersion!,
            _ => $"{p.BaselineVersion} → {p.TargetVersion}",
        };
        return new($"pkg:{p.Ecosystem}:{p.Name}", CompareRowKind.Package, branch, p.Name, change, Versions: versions, Package: p);
    }

    public PaneContent Diff()
    {
        if (c.Diff is not null)
        {
            return TextDiffPane(c.Diff);
        }
        int w = RightInner;
        List<CompareRow> list = Rows();
        c.Cursor = Math.Clamp(c.Cursor, 0, Math.Max(0, list.Count - 1));

        List<Line> lines = [Chips(w), Line.Blank,
            new Line().Add("   ").Add(Fmt.Fit(c.BaselineLabel, 8).PadLeft(8), Theme.Shale).Add("    ").Add(Fmt.Fit(c.TargetLabel, 8).PadLeft(8), Theme.Shale).Add("  ")
                .Add("change".PadLeft(9), Theme.Shale).Add("  ").Add("name".PadRight(28), Theme.Shale).Add("version", Theme.Shale).Truncate(w)];
        PaneContent pane = ExplorerPresenter.Pane(lines, "Differences", !c.FocusLayers, $"{c.BaselineLabel} → {c.TargetLabel}");

        int visible = DiffRows;
        int scroll = Math.Clamp(c.Scroll, 0, Math.Max(0, list.Count - visible));
        if (c.Cursor < scroll)
        {
            scroll = c.Cursor;
        }
        if (c.Cursor >= scroll + visible)
        {
            scroll = c.Cursor - visible + 1;
        }
        c.Scroll = scroll;
        for (int i = scroll; i < Math.Min(list.Count, scroll + visible); i++)
        {
            pane.On(lines.Count, new SetCursor(i));
            lines.Add(Row(list[i], i == c.Cursor, w));
        }
        while (lines.Count < 3 + visible)
        {
            lines.Add(Line.Blank);
        }

        if (c.PackageFiles is PackageFilesContent pf)
        {
            lines.Add(Line.Of($"── {pf.Package.Name} ", Theme.Shale).Add(new string('─', Math.Max(0, w - pf.Package.Name.Length - 4)), Theme.Shale));
            Line summary = new();
            if (pf.Package.BaselineVersion is not null && pf.Package.TargetVersion is not null)
            {
                summary.Add(pf.Package.BaselineVersion, Theme.Silt).Add(" → ", Theme.Shale).Add(pf.Package.TargetVersion, Theme.Foam);
            }
            else
            {
                summary.Add(pf.Package.TargetVersion ?? pf.Package.BaselineVersion ?? "", Theme.Foam);
            }
            if (pf.Files is not null)
            {
                summary.Add($"   {Fmt.Count(pf.Total, "file")}, {Fmt.N(pf.Files.Count)} changed", Theme.Silt);
            }
            lines.Add(summary.Truncate(w));
            if (pf.Message is not null)
            {
                lines.Add(Line.Of("  " + pf.Message, Theme.Silt).Truncate(w));
            }
            foreach ((string path, Change change) in (pf.Files ?? []).Take(4))
            {
                var (glyph, color) = ExplorerPresenter.Glyph(change);
                lines.Add(new Line().Add($"  {glyph} ", Theme.S(color, null, Deco.Bold)).Add("/" + path, Theme.Foam).Truncate(w));
            }
            if (pf.Files is { Count: > 4 })
            {
                lines.Add(Line.Of($"  … {Fmt.N(pf.Files.Count - 4)} more", Theme.Shale));
            }
        }

        lines.Add(Line.Blank);
        lines.Add(new Line().Append(ExplorerPresenter.Keycap("Enter")).Add(" Diff a file   ", Theme.Silt)
            .Append(ExplorerPresenter.Keycap(presenter.Keys.Label(KeyAction.CopyCommand))).Add($" {presenter.CopyVerb} ", Theme.Silt)
            .Add("dredge image compare files", Theme.Foam).Truncate(w));
        return pane;
    }

    private Line Chips(int w)
    {
        IReadOnlyList<ExplorerPackageDifference> p = c.Comparison.Packages;
        int updated = p.Count(x => x.BaselineVersion is not null && x.TargetVersion is not null);
        int added = p.Count(x => x.BaselineVersion is null);
        int removed = p.Count(x => x.TargetVersion is null);
        Line line = new Line()
            .Add(" ~", Theme.S(Theme.Ochre, Theme.Graphite, Deco.Bold)).Add($" {Fmt.N(updated)} updated ", Theme.S(Theme.Foam, Theme.Graphite)).Add(" ")
            .Add(" +", Theme.S(Theme.Kelp, Theme.Graphite, Deco.Bold)).Add($" {Fmt.N(added)} added ", Theme.S(Theme.Foam, Theme.Graphite)).Add(" ")
            .Add(" −", Theme.S(Theme.Garnet, Theme.Graphite, Deco.Bold)).Add($" {Fmt.N(removed)} removed ", Theme.S(Theme.Foam, Theme.Graphite));
        // An ecosystem missing from both images has nothing to compare; one missing
        // from only one image means its package changes can't be shown.
        List<string> unavailable = Enum.GetValues<InstalledPackageEcosystem>()
            .Where(e => (c.Comparison.Baseline.Packages.Ecosystems[e].Availability == InstalledPackageMetadataAvailability.Unavailable)
                != (c.Comparison.Target.Packages.Ecosystems[e].Availability == InstalledPackageMetadataAvailability.Unavailable))
            .Select(EcosystemName).ToList();
        Line right = unavailable.Count > 0
            ? Line.Of($"▲ {string.Join(", ", unavailable)} metadata unavailable", Theme.Ochre)
            : Line.Of($"{Fmt.Count(c.Comparison.Files.Count, "path")} {(c.Comparison.Files.Count == 1 ? "differs" : "differ")}", Theme.Shale);
        return line.PadRight(w, right);
    }

    private static Line Row(CompareRow r, bool sel, int w)
    {
        var (glyph, color) = ExplorerPresenter.Glyph(r.Change);
        Line line = new Line().Add(sel ? "▌" : " ", Theme.Channel);
        if (r.Kind == CompareRowKind.Section)
        {
            bool top = r.Branch.Length == 0;
            line.Add(r.Branch, Theme.Shale);
            if (top)
            {
                line.Add(r.Expanded ? "▾ " : "▸ ", Theme.Silt);
            }
            line.Add(r.Name, Theme.S(top ? Theme.Foam : Theme.Silt, null, Deco.Bold));
            if (r.Files is int n)
            {
                line.Add($"  {Fmt.Count(n, r.Key == "section:files" ? "path" : "package")} {(n == 1 ? "differs" : "differ")}", Theme.Shale);
            }
            line.Truncate(w).Pad(w);
            return sel ? line.WithBackground(Theme.ChannelDeep) : line;
        }
        line.Add(glyph + " ", Theme.S(color, null, Deco.Bold));
        bool sizes = r.Kind is CompareRowKind.Dir or CompareRowKind.File;
        // Package rows have no sizes, so their names start at the size columns and
        // the version change gets the room.
        if (sizes)
        {
            line.Add((r.Before is long a ? Fmt.Size(a) : r.After is not null ? "—" : "").PadLeft(8), Theme.Silt)
                .Add(" → ", Theme.Shale).Add(" ")
                .Add((r.After is long b ? Fmt.Size(b) : r.Before is not null ? "—" : "").PadLeft(8), Theme.Foam)
                .Add("  ");
            long delta = (r.After ?? 0) - (r.Before ?? 0);
            line.Add(delta != 0 ? Signed(delta).PadLeft(9) : new string(' ', 9), delta > 0 ? Theme.Ochre : Theme.Kelp)
                .Add("  ");
        }
        else
        {
            line.Add(" ");
        }

        Line name = new Line().Add(r.Branch, Theme.Shale)
            .Add(r.Expandable ? (r.Expanded ? "▾ " : "▸ ") : r.Kind == CompareRowKind.Unchanged ? "… " : "  ", Theme.Silt);
        Sty style = r.Change switch
        {
            Change.Added => Theme.S(Theme.Kelp, null, Deco.Bold),
            Change.Modified => Theme.S(Theme.Ochre, null, Deco.Bold),
            Change.Removed => Theme.S(Theme.Garnet, null, Deco.Bold | Deco.Strikethrough),
            _ => Theme.S(Theme.Silt),
        };
        name.Add(r.Kind == CompareRowKind.Dir ? r.Name + "/" : r.Name, style);
        if (r.Files is int files && !r.Expanded)
        {
            name.Add($"  {Fmt.N(files)}", Theme.Shale);
        }
        name.Truncate(28).Pad(28);
        if (r.Versions is not null)
        {
            string[] parts = r.Versions.Split(" → ");
            if (parts.Length == 2)
            {
                name.Add(parts[0], Theme.Silt).Add(" → ", Theme.Shale).Add(parts[1], Theme.Foam);
            }
            else
            {
                name.Add(r.Versions, r.Change == Change.Removed ? Theme.S(Theme.Garnet, null, Deco.Strikethrough) : Theme.S(Theme.Foam));
            }
        }
        int room = Math.Max(0, w - line.Length);
        name.Truncate(room).Pad(room);
        line.Append(name);
        return sel ? line.WithBackground(Theme.ChannelDeep) : line;
    }

    // Side-by-side text diff of one file, opened with Enter on a changed file.
    private PaneContent TextDiffPane(TextDiffContent diff)
    {
        int w = RightInner;
        List<Line> lines = [];
        int half = (w - 3) / 2;
        lines.Add(new Line().Add(Fmt.Fit(c.BaselineLabel, half).PadRight(half), Theme.S(Theme.Silt, null, Deco.Bold)).Add(" │ ", Theme.Shale)
            .Add(Fmt.Fit(c.TargetLabel, half), Theme.S(Theme.Foam, null, Deco.Bold)));
        lines.Add(Line.Of(new string('─', half) + "─┼─" + new string('─', Math.Max(0, w - half - 3)), Theme.Shale));
        if (diff.Message is not null)
        {
            lines.AddRange(Syntax.Wrap([("  " + diff.Message, new Sty(Theme.Silt))], w, 4));
            if (diff.Lines is null)
            {
                return ExplorerPresenter.Pane(lines, diff.Path.Split('/')[^1], true, "/" + diff.Path);
            }
        }
        List<(DiffLine? Left, DiffLine? Right)> pairs = Pair(diff.Lines ?? []);
        int room = Math.Max(1, RightInnerHeight - lines.Count);
        c.DiffScroll = Math.Clamp(c.DiffScroll, 0, Math.Max(0, pairs.Count - room));
        foreach ((DiffLine? left, DiffLine? right) in pairs.Skip(c.DiffScroll).Take(room))
        {
            Line line = Half(left, half, left?.Op == DiffOp.Delete ? Theme.Garnet : Theme.Silt, left?.OldLine, left?.Op == DiffOp.Delete ? Theme.GarnetDeep : null);
            line.Add(" │ ", Theme.Shale);
            line.Append(Half(right, Math.Max(0, w - half - 3), right?.Op == DiffOp.Insert ? Theme.Kelp : Theme.Foam, right?.NewLine, right?.Op == DiffOp.Insert ? Theme.KelpDeep : null));
            lines.Add(line);
        }
        int changes = (diff.Lines ?? []).Count(l => l.Op != DiffOp.Same);
        return ExplorerPresenter.Pane(lines, diff.Path.Split('/')[^1], true, $"/{diff.Path} · {Fmt.N(changes)} changed lines");
    }

    private static Line Half(DiffLine? line, int width, Rgb fg, int? number, Rgb? bg)
    {
        Line result = new();
        if (line is null)
        {
            return result.Pad(width);
        }
        result.Add($"{number,4} ", Theme.Shale).Add(line.Text.Replace('\t', ' '), new Sty(fg, bg));
        result.Truncate(width).Pad(width, bg is null ? null : new Sty(fg, bg));
        return result;
    }

    internal static List<(DiffLine? Left, DiffLine? Right)> Pair(IReadOnlyList<DiffLine> lines)
    {
        List<(DiffLine?, DiffLine?)> pairs = [];
        for (int i = 0; i < lines.Count;)
        {
            if (lines[i].Op == DiffOp.Same)
            {
                pairs.Add((lines[i], lines[i]));
                i++;
                continue;
            }
            List<DiffLine> deletes = [], inserts = [];
            while (i < lines.Count && lines[i].Op != DiffOp.Same)
            {
                (lines[i].Op == DiffOp.Delete ? deletes : inserts).Add(lines[i]);
                i++;
            }
            for (int j = 0; j < Math.Max(deletes.Count, inserts.Count); j++)
            {
                pairs.Add((j < deletes.Count ? deletes[j] : null, j < inserts.Count ? inserts[j] : null));
            }
        }
        return pairs;
    }

    public List<Hint> Hints()
    {
        KeyMap k = presenter.Keys;
        if (c.Diff is not null)
        {
            return [new("↑↓", "Scroll"), new("PgUp PgDn", "Page", ShowInFooter: false), new("Home End", "Top or bottom", ShowInFooter: false), new("Esc", "Back to differences", new Back()),
                new(k.Label(KeyAction.Help), "Keys", new ShowView(RightView.Keys)), new(k.Label(KeyAction.Quit), "Quit", new Quit())];
        }
        return
        [
            new("Tab", c.FocusLayers ? "Differences" : "Layers", new FocusOn(c.FocusLayers ? FocusPane.Right : FocusPane.Layers)), new("↑↓", "Move", ShowInFooter: false),
            new($"{k.Label(KeyAction.PreviousLayer)} {k.Label(KeyAction.NextLayer)}", "Next difference"),
            new(k.Label(KeyAction.SwapSides), "Swap sides", new SwapSides()),
            new("Enter", "Diff a file", new Activate()),
            new(k.Label(KeyAction.Compare), "Change tag…", new PickTag()), new("Esc", "Leave compare", new Back()), new("←→", "Fold", ShowInFooter: false),
            new(k.Label(KeyAction.CopyCommand), $"{presenter.CopyVerb} command", new CopyCommand()),
            new("PgUp PgDn", "Page", ShowInFooter: false), new("Home End", "First or last", ShowInFooter: false),
            new(k.Label(KeyAction.Help), "Keys", new ShowView(RightView.Keys)), new(k.Label(KeyAction.Quit), "Quit", new Quit()),
        ];
    }

    private sealed class FileTree
    {
        public string Name = "";
        public string Path = "";
        public bool Dir;
        public Change Change;
        public long? Before;
        public long? After;
        public int FileCount;
        public List<FileTree> Children = [];

        public static FileTree Build(IReadOnlyList<ExplorerFileDifference> files)
        {
            FileTree root = new() { Dir = true };
            Dictionary<string, FileTree> nodes = new(StringComparer.Ordinal) { [""] = root };
            FileTree Get(string path)
            {
                if (nodes.TryGetValue(path, out FileTree? node))
                {
                    return node;
                }
                int slash = path.LastIndexOf('/');
                FileTree parent = Get(slash < 0 ? "" : path[..slash]);
                node = new FileTree { Name = path[(slash + 1)..], Path = path, Dir = true, Change = Change.Modified };
                parent.Children.Add(node);
                nodes[path] = node;
                return node;
            }
            foreach (ExplorerFileDifference file in files)
            {
                FileTree node = Get(file.Path);
                node.Change = ExplorerImage.ToChange(file.Kind);
                ImageFileSystemEntry? entry = file.Target ?? file.Baseline;
                node.Dir = entry?.Type == ImageFileType.Directory && node.Children.Count == 0 ? true : node.Children.Count > 0;
                if (entry?.Type != ImageFileType.Directory)
                {
                    node.Before = file.Baseline?.Size;
                    node.After = file.Target?.Size;
                }
            }
            Finish(root);
            return root;
        }

        private static void Finish(FileTree node)
        {
            node.Children.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
            if (node.Children.Count == 0)
            {
                node.FileCount = node.Dir ? 0 : 1;
                return;
            }
            node.Dir = true;
            long before = 0, after = 0;
            bool anyBefore = false, anyAfter = false;
            foreach (FileTree child in node.Children)
            {
                Finish(child);
                node.FileCount += child.FileCount;
                if (child.Before is long b) { before += b; anyBefore = true; }
                if (child.After is long a) { after += a; anyAfter = true; }
            }
            node.Before = anyBefore ? before : null;
            node.After = anyAfter ? after : null;
            if (node.Children.All(child => child.Change == Change.Added))
            {
                node.Change = Change.Added;
            }
            else if (node.Children.All(child => child.Change == Change.Removed))
            {
                node.Change = Change.Removed;
            }
        }
    }
}
