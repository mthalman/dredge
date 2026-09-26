namespace Valleysoft.Dredge.Explorer;

internal sealed partial class ExplorerPresenter
{
    public const int MaxSearchHits = 1000;

    // ───────────────────────────── insights ─────────────────────────────

    // Base-image findings stay collapsed into one row: the user can't fix them here.
    public List<ExplorerFinding?> VisibleFindings(ExplorerState s)
    {
        if (!img.Complete)
        {
            return [];
        }
        List<ExplorerFinding?> list = img.Findings.Where(f => !f.FromBase).Cast<ExplorerFinding?>().ToList();
        List<ExplorerFinding> fromBase = img.Findings.Where(f => f.FromBase).ToList();
        if (fromBase.Count > 0)
        {
            list.Add(null);
            if (s.ShowBaseFindings)
            {
                list.AddRange(fromBase);
            }
        }
        return list;
    }

    public ExplorerFinding? SelectedFinding(ExplorerState s)
    {
        List<ExplorerFinding?> list = VisibleFindings(s);
        return list.Count == 0 ? null : list[Math.Clamp(s.Finding, 0, list.Count - 1)];
    }

    public PaneContent InsightsPane(ExplorerState s)
    {
        int w = RightInner;
        bool focused = s.Focus == FocusPane.Right;
        List<Line> items = [];
        if (!img.Complete)
        {
            items.Add(Line.Blank);
            if (img.SessionError is not null)
            {
                items.Add(new Line().Add("  ▲ ", Theme.Garnet).Add("Insights are unavailable.", Theme.Foam));
                items.AddRange(Syntax.Wrap([("  " + img.SessionError, new Sty(Theme.Silt))], w, 4));
            }
            else
            {
                items.Add(new Line().Add("  " + SpinnerGlyph(s) + " ", Theme.Channel)
                    .Add($"Insights appear once every layer is indexed ({img.ReadyCount} of {img.LayerCount}).", Theme.Foam));
                items.Add(Line.Blank);
                items.Add(Line.Of("  Efficiency counts only bytes a later layer hides. Nothing is guessed", Theme.Silt));
                items.Add(Line.Of("  from partial data, so this view waits for the whole image.", Theme.Silt));
            }
            return Pane(items, "Insights", focused);
        }

        long total = img.TotalReclaimable;
        int eff = (int)Math.Floor(img.Efficiency * 100);
        items.Add(new Line()
            .Add(Fmt.Size(total), Theme.S(Theme.Garnet, null, Deco.Bold))
            .Add($" hidden bytes · {eff}% efficient", Theme.Foam));
        items.Add(img.PotentialSavings > 0
            ? new Line().Add(Fmt.Size(img.PotentialSavings), Theme.Ochre)
                .Add(" more potential savings · verify your app does not need these files", Theme.Silt)
            : Line.Of("No potential savings detected by heuristics.", Theme.Silt));
        items.Add(Line.Blank);

        Rgb[] palette = [Theme.Garnet, Theme.Rose, Theme.Mauve, Theme.Ochre];
        var categories = img.Findings.Where(f => f.Certain && !f.FromBase)
            .GroupBy(f => f.Category).Select(g => (g.Key, Bytes: g.Sum(f => f.Bytes))).ToList();
        long fromBase = img.Findings.Where(f => f.FromBase).Sum(f => f.Bytes) + img.Insights.BaseChurnBytes;
        List<(string, long, Rgb)> parts = [("Other shipped bytes", Math.Max(0, img.TotalSize - total), Theme.Sand1)];
        parts.AddRange(categories.Select((c, i) => (c.Key, c.Bytes, palette[i % palette.Length])));
        if (fromBase > 0)
        {
            parts.Add(("From the base image", fromBase, Theme.Silt));
        }
        items.AddRange(Breakdown.Render(parts, w));
        items.Add(Line.Blank);
        int headerLines = items.Count;

        List<ExplorerFinding?> findings = VisibleFindings(s);
        if (findings.Count == 0)
        {
            items.Add(new Line().Add("✓ ", Theme.Kelp).Add("No hidden bytes. Every shipped file is visible in the final image.", Theme.Foam));
            return Pane(items, "Insights", focused, "0 findings");
        }
        s.Finding = Math.Clamp(s.Finding, 0, findings.Count - 1);

        List<(int Start, int Count)> blocks = [];
        List<Line> list = [];
        for (int i = 0; i < findings.Count; i++)
        {
            int start = list.Count;
            bool selected = i == s.Finding;
            if (findings[i] is not ExplorerFinding f)
            {
                int count = img.Findings.Count(x => x.FromBase);
                Line group = new Line()
                    .Add(selected ? "▌" : " ", Theme.Channel)
                    .Add(s.ShowBaseFindings ? "▾ " : "▸ ", Theme.Silt)
                    .Add($"{count} {(count == 1 ? "finding" : "findings")} inside the base image", Theme.S(Theme.Foam, null, Deco.Bold))
                    .Add(" · not fixable here", Theme.Silt);
                group.PadRight(w, Line.Of(Fmt.Size(fromBase), Theme.Silt));
                list.Add(selected ? group.WithBackground(focused ? Theme.ChannelDeep : Theme.Graphite) : group);
                list.Add(Line.Of("   Rebuild or update the base image to reclaim these bytes.", Theme.Silt));
                blocks.Add((start, list.Count - start));
                list.Add(Line.Blank);
                continue;
            }
            string layers = f.Layers.Count == 1 ? $"layer {f.Layers[0]}" : $"layers {string.Join(" → ", f.Layers)}";
            Line title = new Line()
                .Add(selected ? "▌" : " ", Theme.Channel)
                .Add($"{i + 1} ", Theme.Silt)
                .Add(f.Title, Theme.S(Theme.Foam, null, Deco.Bold))
                .Add(f.Certain ? " · hidden" : " · potential", f.Certain ? Theme.Garnet : Theme.Ochre);
            title.PadRight(w, new Line()
                .Add(Fmt.Size(f.Bytes), Theme.S(f.Certain ? Theme.Garnet : Theme.Ochre, null, Deco.Bold))
                .Add("  " + Fmt.Fit(layers, 15).PadRight(15), Theme.Silt));
            list.Add(selected ? title.WithBackground(focused ? Theme.ChannelDeep : Theme.Graphite) : title);
            list.Add(new Line().Add("   ").Add(f.Where, Theme.DirName).Truncate(w));
            list.Add(new Line().Add("   ").Add(f.Why, Theme.Silt).Truncate(w));
            if (f.Fix.Length > 0)
            {
                Line fix = new Line().Add("   ").Add(f.FixLabel + "  ", Theme.Kelp);
                fix.Add(" ", Theme.S(Theme.Foam, Theme.Graphite));
                if (f.FixIsDockerfile)
                {
                    foreach (var (text, style) in Syntax.Dockerfile(f.Fix, Theme.Foam))
                    {
                        fix.Add(text, new Sty(style.Foreground, Theme.Graphite, style.Deco));
                    }
                }
                else
                {
                    fix.Add(f.Fix, Theme.S(Theme.Foam, Theme.Graphite));
                }
                fix.Add(" ", Theme.S(Theme.Foam, Theme.Graphite));
                list.Add(fix.Truncate(w));
            }
            blocks.Add((start, list.Count - start));
            list.Add(Line.Blank);
        }

        // Scroll the list so the selected finding is fully visible.
        int room = Math.Max(4, RightInnerHeight - headerLines);
        (int selStart, int selCount) = blocks[s.Finding];
        int scroll = Math.Clamp(s.FindingScroll, 0, Math.Max(0, list.Count - room));
        if (selStart < scroll)
        {
            scroll = selStart;
        }
        if (selStart + selCount > scroll + room)
        {
            scroll = selStart + selCount - room;
        }
        s.FindingScroll = scroll;

        PaneContent pane = Pane(items, "Insights", focused, $"{img.Findings.Count} {(img.Findings.Count == 1 ? "finding" : "findings")}");
        for (int i = scroll; i < Math.Min(list.Count, scroll + room); i++)
        {
            int finding = blocks.FindIndex(b => i >= b.Start && i < b.Start + b.Count);
            if (finding >= 0)
            {
                pane.On(items.Count, new SelectFinding(finding));
            }
            items.Add(list[i]);
        }
        return pane;
    }

    // ───────────────────────────── inspector ─────────────────────────────

    public Node? InspectedNode(ExplorerState s) =>
        ExplorerImage.Find(Tree(s), s.InspectPath) ?? ExplorerImage.Find(img.LayerTree(s.Layer), s.InspectPath);

    private PaneContent InspectorPane(ExplorerState s)
    {
        int w = RightInner;
        string path = s.InspectPath;
        Node? node = InspectedNode(s);
        List<Line> items = [];
        if (node is null)
        {
            return Pane([Line.Of($"/{path} is not in layer {s.Layer}.", Theme.Silt)], path.Split('/')[^1], true, "/" + path);
        }

        Line meta = new Line()
            .Add(node.Mode, Theme.Silt).Add("   ")
            .Add(node.Owner, Theme.Silt).Add("   ")
            .Add(Fmt.Size(node.Change == Change.Removed ? node.ShippedSize : node.Size), Theme.Foam);
        if (node.Entry?.ContentHash is string hash)
        {
            meta.Add("   ").Add(ShortDigest(hash.Contains(':') ? hash : "sha256:" + hash), Theme.Silt);
        }
        if (node.Target is not null)
        {
            meta.Add("   ").Add(node.HardLink ? "hard link to " : "→ ", Theme.Shale).Add(node.Target, Theme.Foam);
        }
        items.Add(meta.Truncate(w));
        items.Add(Line.Blank);
        items.Add(Line.Of("History", Theme.S(Theme.Foam, null, Deco.Bold)));

        List<(int Layer, Change Change)> history = img.PathHistory(path);
        int maxHistory = Math.Max(2, (RightInnerHeight - 12) / 2);
        IEnumerable<(int Layer, Change Change)> shown = history.Count > maxHistory ? history.Skip(history.Count - maxHistory) : history;
        if (history.Count > maxHistory)
        {
            items.Add(Line.Of($"  … {history.Count - maxHistory} earlier layers", Theme.Shale));
        }
        List<(int Layer, Change Change)> shownList = shown.ToList();
        for (int i = 0; i < shownList.Count; i++)
        {
            var (layer, change) = shownList[i];
            var (glyph, color) = Glyph(change);
            string what = change switch
            {
                Change.Added => "added",
                Change.Modified => "modified",
                Change.Identical => "rewritten with identical bytes",
                Change.Removed => "deleted",
                _ => "written",
            };
            bool current = layer == s.Layer;
            Line line = new Line()
                .Add(current ? "◆ " : "● ", current ? Theme.Channel : color)
                .Add($"layer {layer,-3}", current ? Theme.S(Theme.Foam, null, Deco.Bold) : Theme.S(Theme.Silt))
                .Add((change == Change.None ? "·" : glyph) + " ", Theme.S(color, null, Deco.Bold))
                .Add(what.PadRight(32), Theme.Foam);
            line.Append(Syntax.DockerfileLine(img.Row(layer).Instruction, Theme.Silt, Math.Max(0, w - line.Length)));
            items.Add(line);
            if (i < shownList.Count - 1)
            {
                items.Add(Line.Of("│", Theme.Shale));
            }
        }
        int identical = history.FindIndex(h => h.Change == Change.Identical && h.Layer == s.Layer);
        if (identical > 0)
        {
            items.Add(Line.Blank);
            items.Add(new Line().Add("= ", Theme.S(Theme.Silt, null, Deco.Bold))
                .Add($"Same content as layer {history[identical - 1].Layer}, so this copy adds bytes but no change.", Theme.Silt)
                .Truncate(w));
        }
        items.Add(Line.Blank);

        PreviewContent? preview = s.Preview is { } p && p.Path == path ? p : null;
        List<string> previewLines = preview?.Lines ?? [];
        string ruleText = node.Kind == Kind.Dir ? "  directory "
            : preview is null ? (img.Complete ? "  loading… " : "  available once every layer is indexed ")
            : preview.Lines is not null ? $"  {preview.Language ?? "text"} · {Fmt.N(previewLines.Count)} lines "
            : "  not shown ";
        Line rule = new Line().Add("── ", Theme.Shale).Add("Preview", Theme.Foam).Add(ruleText, Theme.Silt);
        items.Add(rule.Add(new string('─', Math.Max(0, w - rule.Length)), Theme.Shale));
        int room = Math.Max(1, RightInnerHeight - items.Count - 2);
        if (preview?.Message is string message)
        {
            items.AddRange(Syntax.Wrap([(message, new Sty(Theme.Silt))], w, Math.Min(room, 3)));
            room -= Math.Min(room, 3);
        }
        if (node.Kind == Kind.Dir)
        {
            items.Add(new Line().Add($"{Fmt.Count(node.FileCount, "file")} · ", Theme.Silt).Append(Keycap(Keys.Label(KeyAction.Extract)))
                .Add(" extracts the folder", Theme.Silt));
            room--;
        }
        s.PreviewScroll = Math.Clamp(s.PreviewScroll, 0, Math.Max(0, previewLines.Count - room));
        bool json = preview?.Language == "json";
        int shownLines = 0;
        foreach (var (text, i) in previewLines.Skip(s.PreviewScroll).Take(room).Select((t, i) => (t, i + s.PreviewScroll)))
        {
            Line body = json ? Syntax.Json(text) : Line.Of(text.Replace('\t', ' '), Theme.Foam);
            items.Add(new Line().Add($"{i + 1,4}  ", Theme.Shale).Append(body).Truncate(w));
            shownLines++;
        }
        for (int i = shownLines; i < room; i++)
        {
            items.Add(Line.Blank);
        }
        items.Add(new Line()
            .Add("$ ", Theme.Shale)
            .Add(CopyCommandText(s, path, node.Kind == Kind.Dir), Theme.Foam)
            .PadRight(w, new Line().Append(Keycap(Keys.Label(KeyAction.CopyCommand))).Add(" copy", Theme.Silt)));

        return Pane(items, path.Split('/')[^1], true, "/" + path);
    }

    public string CopyCommandText(ExplorerState s, string path, bool dir)
    {
        string platform = img.PlatformArguments.Length > 0 ? " " + img.PlatformArguments : "";
        return dir
            ? $"dredge image ls {img.Reference} /{path} --recursive{platform}"
            : $"dredge image cat {img.Reference} /{path}{platform}";
    }

    // ───────────────────────────── search ─────────────────────────────

    public (List<SearchHit> Hits, int Total) SearchResults(ExplorerState s)
    {
        if (searchCache is { } cached && cached.Version == version)
        {
            return (cached.Hits, cachedTotal);
        }
        List<SearchHit> hits = [];
        int total = 0;
        if (s.SearchQuery.Length > 0)
        {
            StringComparison comparison = s.SearchExactCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
            IReadOnlyDictionary<string, ScannedEntry>? live = img.Analysis?.LiveEntries;
            foreach ((string path, List<(int Layer, Change Change)> layers) in img.SearchIndex)
            {
                if (!path.Contains(s.SearchQuery, comparison)
                    || (s.SearchLayerOnly && !layers.Any(l => l.Layer == s.Layer))
                    || (!s.SearchIncludeDeleted && layers[^1].Change == Change.Removed))
                {
                    continue;
                }
                total++;
                if (hits.Count >= MaxSearchHits)
                {
                    continue;
                }
                ScannedEntry? entry = live is not null && live.TryGetValue(path, out ScannedEntry? found) ? found : null;
                string? note = null;
                foreach (ExplorerFinding f in img.Findings)
                {
                    if (!f.FromBase && f.Roots.Any(root => path == root || path.StartsWith(root + "/", StringComparison.Ordinal)))
                    {
                        note = f.NoteFor(f.Layers[0]);
                        break;
                    }
                }
                hits.Add(new SearchHit(path, entry?.Type == ImageFileType.Directory, entry?.Size ?? 0, [.. layers], note));
            }
            hits.Sort((a, b) => string.CompareOrdinal(a.Path, b.Path));
        }
        searchCache = (version, hits);
        cachedTotal = total;
        return (hits, total);
    }

    private int cachedTotal;

    private int SearchedPaths(ExplorerState s) => s.SearchLayerOnly
        ? img.SearchIndex.Count(pair => pair.Value.Any(l => l.Layer == s.Layer))
        : img.SearchIndex.Count;

    public PaneContent SearchPane(ExplorerState s)
    {
        int w = RightInner;
        (List<SearchHit> hits, int total) = SearchResults(s);
        s.SearchCursor = Math.Clamp(s.SearchCursor, 0, Math.Max(0, hits.Count - 1));
        Sty on = Theme.S(Theme.Foam, Theme.KeycapBg, Deco.Bold);
        Sty off = Theme.S(Theme.Silt, Theme.Graphite);
        string wholeChip = " Whole image ", layerChip = $" Layer {s.Layer} ";
        string deletedChip = s.SearchIncludeDeleted ? " ✓ Include deleted " : " Include deleted ";
        string caseChip = s.SearchExactCase ? " ✓ Exact case " : " Exact case ";
        Line scope = new Line()
            .Add(wholeChip, s.SearchLayerOnly ? off : on)
            .Add(layerChip, s.SearchLayerOnly ? on : off)
            .Add("  ");
        int deletedAt = scope.Length;
        scope.Add(deletedChip, s.SearchIncludeDeleted ? on : off);
        scope.Add(" ");
        int caseAt = scope.Length;
        scope.Add(caseChip, s.SearchExactCase ? on : off);
        string count = total > hits.Count ? $"first {Fmt.N(hits.Count)} of {Fmt.N(total)} matches" : total == 1 ? "1 match" : $"{Fmt.N(total)} matches";
        scope.PadRight(w, new Line().Add(count, Theme.Foam).Add($" in {Fmt.Count(SearchedPaths(s), "path")}", Theme.Silt));

        List<Line> items = [scope];
        items.Add(img.Loading
            ? new Line().Add(SpinnerGlyph(s) + " ", Theme.Channel).Add($"Searching {img.ReadyCount} of {img.LayerCount} indexed layers; results grow as layers load.", Theme.Silt)
            : Line.Blank);
        items.Add(new Line().Add("   ").Add("layers".PadRight(12), Theme.Shale).Add("size".PadLeft(8), Theme.Shale).Add("  ").Add("path", Theme.Shale));
        List<(int Line, int Hit)> clickable = [];

        int visible = SearchRows;
        int scroll = Math.Clamp(s.SearchScroll, 0, Math.Max(0, hits.Count - visible));
        if (s.SearchCursor < scroll)
        {
            scroll = s.SearchCursor;
        }
        if (s.SearchCursor >= scroll + visible)
        {
            scroll = s.SearchCursor - visible + 1;
        }
        s.SearchScroll = scroll;
        StringComparison comparison = s.SearchExactCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;

        for (int i = scroll; i < Math.Min(hits.Count, scroll + visible); i++)
        {
            SearchHit h = hits[i];
            bool selected = i == s.SearchCursor;
            bool deleted = h.Layers[^1].Change == Change.Removed;
            Line line = new Line().Add(selected ? "▌" : " ", Theme.Channel).Add("  ");
            var badgeLayers = h.Layers.Length > 3 ? h.Layers[^3..] : h.Layers;
            line.Append(Badges(badgeLayers).Truncate(12).Pad(12));
            line.Add((h.Dir || deleted && h.Size == 0 ? "" : Fmt.Size(h.Size)).PadLeft(8),
                deleted ? Theme.S(Theme.Garnet, null, Deco.Strikethrough) : Theme.S(Theme.Foam));
            line.Add("  ");

            Line path = new();
            string full = "/" + h.Path + (h.Dir ? "/" : "");
            Deco strike = deleted ? Deco.Strikethrough : Deco.None;
            int m = full.LastIndexOf(s.SearchQuery, comparison);
            Rgb leafColor = deleted ? Theme.Garnet : h.Dir ? Theme.DirName : Theme.Foam;
            if (m >= 0)
            {
                path.Add(full[..m], Theme.S(leafColor, null, strike))
                    .Add(full.Substring(m, s.SearchQuery.Length), Theme.S(Theme.Channel, null, Deco.Bold | Deco.Underline | strike))
                    .Add(full[(m + s.SearchQuery.Length)..], Theme.S(leafColor, null, strike));
            }
            else
            {
                path.Add(full, Theme.S(leafColor, null, strike));
            }
            int room = w - line.Length;
            if (h.Note is not null)
            {
                path.PadRight(room, Line.Of("▲ " + h.Note, Theme.Garnet));
            }
            else if (deleted)
            {
                path.PadRight(room, Line.Of($"deleted in layer {h.Layers[^1].Layer}", Theme.Silt));
            }
            else
            {
                path.Truncate(room).Pad(room);
            }
            line.Append(path);
            clickable.Add((items.Count, i));
            items.Add(selected ? line.WithBackground(Theme.ChannelDeep) : line);
        }

        if (s.SearchQuery.Length == 0)
        {
            items.Add(Line.Blank);
            items.Add(Line.Of("Type part of a file or folder name. Matching ignores case unless you press Alt+C.", Theme.Silt));
        }
        else if (hits.Count == 0)
        {
            items.Add(Line.Blank);
            Line none = new Line().Add("No names match ", Theme.Silt).Add(s.SearchQuery, Theme.Foam);
            if (s.SearchLayerOnly)
            {
                none.Add($" in layer {s.Layer}. ", Theme.Silt).Append(Keycap("Alt+L")).Add(" searches the whole image.", Theme.Silt);
            }
            else if (!s.SearchIncludeDeleted)
            {
                none.Add(". ", Theme.Silt).Append(Keycap("Alt+D")).Add(" includes deleted paths.", Theme.Silt);
            }
            else
            {
                none.Add(". Search covers every layer, including deleted paths.", Theme.Silt);
            }
            items.Add(none);
        }
        else
        {
            while (items.Count < 3 + visible)
            {
                items.Add(Line.Blank);
            }
            items.Add(Line.Of("── Selected ", Theme.Shale).Add(new string('─', Math.Max(0, w - 12)), Theme.Shale));
            SearchHit sel = hits[s.SearchCursor];
            items.Add(new Line().Add("/" + sel.Path + (sel.Dir ? "/" : ""), Theme.S(sel.Dir ? Theme.DirName : Theme.Foam, null, Deco.Bold)).Truncate(w));
            items.Add(new Line().Add("layers ", Theme.Silt).Append(Badges(sel.Layers)).Truncate(w));
            Line open = new Line().Add("Press ", Theme.Silt).Append(Keycap("Enter")).Add(" to open it in the last layer that wrote it.", Theme.Silt);
            if (sel.Note is not null)
            {
                open.Add("  ▲ " + sel.Note, Theme.Garnet);
            }
            items.Add(open.Truncate(w));
        }

        PaneContent pane = Pane(items, "Search", true, "names and paths");
        pane.On(0, new SetSearchScope(false), 0, wholeChip.Length);
        pane.On(0, new SetSearchScope(true), wholeChip.Length, wholeChip.Length + layerChip.Length);
        pane.On(0, new ToggleIncludeDeleted(), deletedAt, deletedAt + deletedChip.Length);
        pane.On(0, new ToggleExactCase(), caseAt, caseAt + caseChip.Length);
        clickable.ForEach(c => pane.On(c.Line, new SelectSearchHit(c.Hit)));
        return pane;
    }

    // ───────────────────────────── keys ─────────────────────────────

    private PaneContent KeysPane(ExplorerState s)
    {
        string K(KeyAction action) => Keys.Label(action);
        (string Group, (string Keys, string What)[] Items)[] left =
        [
            ("Move", [("↑ ↓", "Move"), ("PgUp PgDn", "Page"), ("Home End", "First or last"), ("Tab", "Switch pane")]),
            ("Layers", [($"{K(KeyAction.PreviousLayer)} {K(KeyAction.NextLayer)}", "Previous or next layer"),
                (K(KeyAction.WholeFilesystem), "Toggle whole filesystem"), (K(KeyAction.FirstUserLayer), "First layer after base"),
                (K(KeyAction.Compare), "Compare with a tag…"), (K(KeyAction.Platform), "Choose platform…"),
                (K(KeyAction.Retry), "Retry a failed layer")]),
            ("Files", [("← →", "Fold or unfold"), ("Space", "Fold everything below"),
                ($"{K(KeyAction.ToggleAdded)} {K(KeyAction.ToggleModified)} {K(KeyAction.ToggleIdentical)} {K(KeyAction.ToggleDeleted)}", "Show or hide changes"),
                (K(KeyAction.FindingsOnly), "Only paths with findings"), ("Enter", "Inspect file")]),
            ("Compare", [(K(KeyAction.SwapSides), "Swap sides"), ("Enter", "Diff a file"), ("Esc", "Leave compare")]),
        ];
        (string Group, (string Keys, string What)[] Items)[] right =
        [
            ("Views", [(K(KeyAction.Insights), "Insights"), (K(KeyAction.Search), "Search"), (K(KeyAction.Help), "This screen"), ("Esc", "Back")]),
            ("Search", [("Alt+L", "Whole image or this layer"), ("Alt+D", "Include deleted paths"), ("Alt+C", "Match exact case")]),
            ("Actions", [(K(KeyAction.Extract), "Extract file or folder…"), (K(KeyAction.CopyCommand), "Show as dredge command"),
                (K(KeyAction.Pager), "Open file in $PAGER"), (K(KeyAction.Quit), "Quit")]),
        ];

        int leftCol = Math.Min(46, RightInner / 2);
        List<Line> Column((string Group, (string Keys, string What)[] Items)[] groups, int keyWidth)
        {
            List<Line> lines = [];
            foreach (var (group, keys) in groups)
            {
                lines.Add(Line.Of(group, Theme.S(Theme.Foam, null, Deco.Bold)));
                foreach (var (k, what) in keys)
                {
                    Line line = new Line().Add("  ");
                    foreach (string part in k.Split(' '))
                    {
                        line.Append(Keycap(part)).Add(" ");
                    }
                    lines.Add(line.Pad(keyWidth).Add(what, Theme.Silt));
                }
                lines.Add(Line.Blank);
            }
            return lines;
        }

        List<Line> a = Column(left, 19), b = Column(right, 10);
        b.Add(Line.Of("Change markers", Theme.S(Theme.Foam, null, Deco.Bold)));
        foreach (var (change, label) in new[]
        {
            (Change.Added, "added in this layer"), (Change.Modified, "modified in this layer"),
            (Change.Identical, "rewritten with identical bytes"), (Change.Removed, "deleted in this layer"),
        })
        {
            var (glyph, color) = Glyph(change);
            b.Add(new Line().Add("  ").Add(glyph, Theme.S(color, null, Deco.Bold)).Add("  " + label, Theme.Silt));
        }
        b.Add(new Line().Add("  ").Add("▲", Theme.Garnet).Add("  part of a finding", Theme.Silt));

        List<Line> rows = [];
        for (int i = 0; i < Math.Max(a.Count, b.Count); i++)
        {
            Line line = (i < a.Count ? a[i] : new Line()).Truncate(leftCol).Pad(leftCol);
            rows.Add(line.Append(i < b.Count ? b[i] : new Line()).Truncate(RightInner));
        }
        rows.Add(Line.Of("Tips", Theme.S(Theme.Foam, null, Deco.Bold)));
        foreach (var (what, cmd) in new[]
        {
            ("Open at a layer", "dredge image explore <image> --layer 7"),
            ("Pick a platform", "dredge image explore <image> --os linux --arch arm64"),
            ("Change a key", "dredge settings set explore.keys.search f"),
            ("Light theme", "dredge settings set explore.theme light"),
            ("Turn off color", "NO_COLOR=1 dredge image explore <image>"),
        })
        {
            rows.Add(new Line().Add("  " + what.PadRight(22), Theme.Silt).Add("$ ", Theme.Shale).Add(cmd, Theme.Foam).Truncate(RightInner));
        }
        return Pane(rows.Take(RightInnerHeight).ToList(), "Keys", true);
    }
}
