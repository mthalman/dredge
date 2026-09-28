namespace Valleysoft.Dredge.Explorer;

internal sealed record PackageInventoryRow(
    InstalledPackageEcosystem Ecosystem, string Name, string? Versions, bool Expanded = false)
{
    public bool IsGroup => Versions is null;
}

internal sealed partial class ExplorerPresenter
{
    private (int Version, List<PackageInventoryRow> Rows)? packageCache;

    public List<PackageInventoryRow> PackageRows(ExplorerState s)
    {
        if (packageCache is { } cached && cached.Version == version)
        {
            return cached.Rows;
        }
        List<PackageInventoryRow> rows = [];
        if (s.Packages is null)
        {
            packageCache = (version, rows);
            return rows;
        }
        foreach (var (ecosystem, metadata) in s.Packages.Ecosystems.OrderBy(pair => pair.Key))
        {
            string label = CompareView.EcosystemName(ecosystem);
            bool Match(string text) => text.Contains(s.PackageQuery, StringComparison.OrdinalIgnoreCase);
            List<PackageInventoryRow> packages = metadata.Packages.OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .Select(pair => new PackageInventoryRow(ecosystem, pair.Key, string.Join(", ", pair.Value)))
                .Where(row => Match(label) || Match(row.Name) || Match(row.Versions!)).ToList();
            if (s.PackageQuery.Length > 0 && packages.Count == 0 && !Match(label))
            {
                continue;
            }
            bool open = s.PackageQuery.Length > 0 || !s.CollapsedPackages.Contains(ecosystem);
            string summary = metadata.Availability == InstalledPackageMetadataAvailability.Unavailable
                ? "metadata unavailable" : Fmt.Count(packages.Count, "package");
            rows.Add(new(ecosystem, $"{label}  {summary}", null, open));
            if (open)
            {
                rows.AddRange(packages);
            }
        }
        packageCache = (version, rows);
        return rows;
    }

    private PaneContent PackagesPane(ExplorerState s)
    {
        int w = RightInner;
        string layer = img.LayerCount == 0 ? "empty image" : $"through layer {s.Layer}";
        if (s.Packages is null)
        {
            string message = s.PackagesError ?? (!img.Complete
                ? img.SessionError ?? "Package inventory is available once every layer is indexed."
                : "Reading packages at this layer…");
            return Pane(Syntax.Wrap([(message, Theme.S(s.PackagesError is null ? Theme.Silt : Theme.Ochre))], w, RightInnerHeight),
                "Packages", s.Focus == FocusPane.Right, layer);
        }

        List<PackageInventoryRow> rows = PackageRows(s);
        int count = s.Packages.Ecosystems.Values.Sum(metadata => metadata.Packages.Count);
        List<Line> lines =
        [
            Line.Of($"{Fmt.Count(count, "package")} detected {layer}", Theme.Foam).Truncate(w),
            Line.Of(s.Packages.Diagnostics.Count > 0
                ? $"{Fmt.Count(s.Packages.Diagnostics.Count, "metadata warning")} - Alt+W for details"
                : s.PackageQuery.Length > 0 ? $"Filter: {s.PackageQuery}" : "Cumulative inventory; inherited packages included.",
                s.Packages.Diagnostics.Count > 0 ? Theme.Ochre : Theme.Silt).Truncate(w),
            Line.Blank,
        ];
        PaneContent pane = Pane(lines, "Packages", s.Focus == FocusPane.Right, layer);
        s.PackageCursor = Math.Clamp(s.PackageCursor, 0, Math.Max(0, rows.Count - 1));
        int visible = TreeRows;
        s.PackageScroll = Math.Clamp(s.PackageScroll, 0, Math.Max(0, rows.Count - visible));
        if (s.PackageCursor < s.PackageScroll)
        {
            s.PackageScroll = s.PackageCursor;
        }
        if (s.PackageCursor >= s.PackageScroll + visible)
        {
            s.PackageScroll = s.PackageCursor - visible + 1;
        }
        int versionWidth = Math.Min(rows.Select(row => row.Versions is null ? 0 : DisplayText.Width(row.Versions))
            .DefaultIfEmpty().Max(), Math.Max(1, (w - 3) / 3));
        int nameWidth = Math.Max(1, w - 3 - versionWidth - 2);
        for (int i = s.PackageScroll; i < Math.Min(rows.Count, s.PackageScroll + visible); i++)
        {
            PackageInventoryRow row = rows[i];
            Line line = new Line().Add(i == s.PackageCursor ? "▌" : " ", Theme.Channel);
            if (row.IsGroup)
            {
                line.Add(row.Expanded ? "▾ " : "▸ ", Theme.Silt).Add(row.Name, Theme.S(Theme.Foam, null, Deco.Bold));
            }
            else
            {
                line.Add("  ").Add(Fmt.Fit(row.Name, nameWidth).PadRight(nameWidth), Theme.Foam)
                    .Add("  ").Add(row.Versions!, Theme.Silt);
            }
            line.Truncate(w).Pad(w);
            pane.On(lines.Count, new SetCursor(i));
            lines.Add(i == s.PackageCursor ? line.WithBackground(Theme.ChannelDeep) : line);
        }
        if (rows.Count == 0)
        {
            lines.Add(Line.Of("No matching packages. Esc clears the filter.", Theme.Silt).Truncate(w));
        }
        while (lines.Count < 3 + visible)
        {
            lines.Add(Line.Blank);
        }
        lines.Add(Line.Blank);
        lines.Add(Line.Of("Enter: fold type or show full package details", Theme.Silt).Truncate(w));
        return pane;
    }

    private List<Hint> PackageHints(ExplorerState s)
    {
        if (s.PackageSearching)
        {
            return [new("↑↓", "Select"), new("PgUp PgDn", "Page", ShowInFooter: false),
                new("Enter", "Browse", new Activate()), new("Esc", "Close filter", new Back())];
        }
        return
        [
            .. s.Packages?.Diagnostics.Count > 0 ? new Hint[] { new("Alt+W", "Warnings", new ShowView(RightView.Warning)) } : [],
            .. s.PackagesError is not null ? new Hint[] { new(Keys.Label(KeyAction.Retry), "Retry packages", new RetryLayer(s.Layer)) } : [],
            .. img.LayerCount > 0 ? (Hint[])
            [
                new("Tab", s.Focus == FocusPane.Layers ? "Packages" : "Layers", new FocusOn(s.Focus == FocusPane.Layers ? FocusPane.Right : FocusPane.Layers)),
                new($"{Keys.Label(KeyAction.PreviousLayer)} {Keys.Label(KeyAction.NextLayer)}", "Step layer"),
                .. img.States[s.Layer] == ExplorerLayerState.Failed
                    ? new Hint[] { new(Keys.Label(KeyAction.Retry), "Retry layer", new RetryLayer(s.Layer)) } : [],
            ] : [],
            new(Keys.Label(KeyAction.Search), "Filter", new ShowView(RightView.Search)),
            new("Enter", "Details / fold", new Activate()),
            new("↑↓", "Move", ShowInFooter: false), new("←→", "Fold", ShowInFooter: false),
            new("PgUp PgDn", "Page", ShowInFooter: false), new("Home End", "First or last", ShowInFooter: false),
            new("Esc", "Back", new Back()), new(Keys.Label(KeyAction.Help), "Keys", new ShowView(RightView.Keys)),
            new(Keys.Label(KeyAction.Quit), "Quit", new Quit()),
        ];
    }
}
