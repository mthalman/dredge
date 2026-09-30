namespace Valleysoft.Dredge.Explorer;

internal sealed partial class ExplorerPresenter
{
    private PaneContent HistoryPane(ExplorerState s)
    {
        if (s.HistoryDiff is { Diff: { } diff } state)
        {
            return FileDiffView.Render(this, state, diff);
        }
        if (s.HistoryLayer is int layer)
        {
            return VersionPane(s, layer);
        }
        List<(int Layer, Change Change)> history = img.PathHistory(s.InspectPath);
        List<Line> lines = [Line.Of("Enter previews a version. Alt+D compares it with the previous event.", Theme.Silt), Line.Blank];
        PaneContent pane = Pane(lines, "File history", true, "/" + s.InspectPath);
        int room = Math.Max(1, RightInnerHeight - lines.Count);
        s.HistoryCursor = Math.Clamp(s.HistoryCursor, 0, Math.Max(0, history.Count - 1));
        s.HistoryScroll = Math.Clamp(s.HistoryScroll, Math.Max(0, s.HistoryCursor - room + 1), s.HistoryCursor);
        s.HistoryScroll = Math.Min(s.HistoryScroll, Math.Max(0, history.Count - room));
        for (int i = s.HistoryScroll; i < Math.Min(history.Count, s.HistoryScroll + room); i++)
        {
            var (layerNumber, change) = history[i];
            var (glyph, color) = Glyph(change);
            bool selected = i == s.HistoryCursor;
            string label = change == Change.Identical ? "identical rewrite" : change.ToString().ToLowerInvariant();
            Line line = new Line().Add(selected ? "▌ " : "  ", Theme.Channel)
                .Add($"layer {layerNumber,-4} ", Theme.Foam).Add(glyph + " ", color)
                .Add(label.PadRight(20), Theme.Silt);
            line.Append(Syntax.DockerfileLine(img.Row(layerNumber).Instruction, Theme.Silt,
                Math.Max(0, RightInner - line.Length)));
            pane.On(lines.Count, new SelectHistory(i));
            lines.Add(selected ? line.WithBackground(Theme.ChannelDeep) : line);
        }
        return pane;
    }

    private PaneContent VersionPane(ExplorerState s, int layer)
    {
        PreviewContent? preview = s.HistoryPreview;
        List<Line> lines = [Line.Of($"Version at layer {layer}", Theme.S(Theme.Foam, null, Deco.Bold))];
        if (preview?.Entry is { } entry)
        {
            lines.Add(Line.Of($"{entry.Type}  mode {Convert.ToString(entry.Mode, 8)}  " +
                $"{entry.UserId}:{entry.GroupId}  {Fmt.Size(entry.Size)}" +
                (entry.LinkTarget is null ? "" : $"  target {entry.LinkTarget}"), Theme.Silt).Truncate(RightInner));
        }
        string? message = preview is null ? "Loading historical version..." : preview.Message;
        if (message is not null)
        {
            lines.AddRange(Syntax.Wrap([(message, new Sty(Theme.Silt))], RightInner, 3));
        }
        lines.Add(Line.Blank);
        List<string> text = preview?.Lines ?? [];
        int width = Math.Max(1, RightInner - 6);
        int room = Math.Max(1, RightInnerHeight - lines.Count);
        s.HistoryPreviewColumn = Math.Clamp(s.HistoryPreviewColumn, 0,
            Math.Max(0, text.Select(DisplayText.Width).DefaultIfEmpty().Max() - width));
        s.HistoryPreviewScroll = Math.Clamp(s.HistoryPreviewScroll, 0, Math.Max(0, text.Count - room));
        foreach (int i in Enumerable.Range(s.HistoryPreviewScroll, Math.Min(room, text.Count - s.HistoryPreviewScroll)))
        {
            Line body = preview?.Language == "json" ? Syntax.Json(text[i]) : Line.Of(text[i].Replace('\t', ' '), Theme.Foam);
            lines.Add(new Line().Add($"{i + 1,4}  ", Theme.Silt).Append(body.Slice(s.HistoryPreviewColumn, width)));
        }
        return Pane(lines, "Historical preview", true, "/" + s.InspectPath +
            (s.HistoryPreviewColumn > 0 ? $" · column {s.HistoryPreviewColumn + 1}" : ""));
    }
}
