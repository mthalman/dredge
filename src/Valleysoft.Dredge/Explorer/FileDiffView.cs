using System.Globalization;
using System.Text;

namespace Valleysoft.Dredge.Explorer;

internal sealed record DiffSpan(string Text, bool Changed);
internal sealed record VisualDiffLine(DiffLine Source, IReadOnlyList<DiffSpan> Spans);
internal sealed record VisualDiffPair(VisualDiffLine? Left, VisualDiffLine? Right);

internal class FileDiffState
{
    public FileDiffState(string baselineLabel, string targetLabel)
    {
        BaselineLabel = baselineLabel;
        TargetLabel = targetLabel;
    }

    public string BaselineLabel { get; set; }
    public string TargetLabel { get; set; }
    public TextDiffContent? Diff { get; set; }
    public int DiffScroll { get; set; }
    public int DiffColumn { get; set; }
    public bool UnifiedDiff { get; set; }

    public void ToggleDiffLayout()
    {
        if (Diff is null) return;
        FileDiffDocument document = Diff.Document;
        if (UnifiedDiff)
        {
            VisualDiffLine? anchor = document.Unified.ElementAtOrDefault(DiffScroll);
            DiffScroll = anchor is null ? 0 : document.Split.ToList()
                .FindIndex(pair => pair.Left?.Source == anchor.Source || pair.Right?.Source == anchor.Source);
        }
        else
        {
            VisualDiffPair? pair = document.Split.ElementAtOrDefault(DiffScroll);
            DiffLine? anchor = (pair?.Left ?? pair?.Right)?.Source;
            DiffScroll = anchor is null ? 0 : document.Unified.ToList().FindIndex(line => line.Source == anchor);
        }
        DiffScroll = Math.Max(0, DiffScroll);
        UnifiedDiff = !UnifiedDiff;
    }
}

internal sealed class FileDiffDocument
{
    public FileDiffDocument(IReadOnlyList<DiffLine> lines, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Dictionary<DiffLine, VisualDiffLine> rendered = [];
        List<VisualDiffPair> pairs = [];
        TextDiff.Workspace wordDiffWorkspace = new();
        foreach (var (left, right) in CompareView.Pair(lines))
        {
            cancellationToken.ThrowIfCancellationRequested();
            IReadOnlyList<DiffSpan> before = left is null ? [] : [new(left.Text, false)];
            IReadOnlyList<DiffSpan> after = right is null ? [] : [new(right.Text, false)];
            if (left?.Op == DiffOp.Delete && right?.Op == DiffOp.Insert &&
                left.Text.Length <= 4096 && right.Text.Length <= 4096)
            {
                IReadOnlyList<DiffLine>? words = TextDiff.Diff(Tokens(left.Text), Tokens(right.Text), maxEdits: 64,
                    cancellationToken: cancellationToken, workspace: wordDiffWorkspace);
                if (words is not null)
                {
                    before = [.. words.Where(word => word.Op != DiffOp.Insert).Select(word => new DiffSpan(word.Text, word.Op == DiffOp.Delete))];
                    after = [.. words.Where(word => word.Op != DiffOp.Delete).Select(word => new DiffSpan(word.Text, word.Op == DiffOp.Insert))];
                }
            }
            VisualDiffLine? oldLine = left is null ? null : new(left, before);
            VisualDiffLine? newLine = right is null ? null : new(right, after);
            if (oldLine is not null) rendered[left!] = oldLine;
            if (newLine is not null) rendered[right!] = newLine;
            pairs.Add(new(oldLine, newLine));
        }
        Split = pairs;
        Unified = [.. lines.Select(line => rendered[line])];
        NumberWidth = Math.Max(3, lines.Select(line => Math.Max(line.OldLine ?? 0, line.NewLine ?? 0))
            .DefaultIfEmpty().Max().ToString(CultureInfo.InvariantCulture).Length);
        TextWidth = lines.Select(line =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            int column = 0;
            return DisplayText.Width(ExpandTabs(line.Text, ref column));
        }).DefaultIfEmpty().Max();
    }

    public IReadOnlyList<VisualDiffPair> Split { get; }
    public IReadOnlyList<VisualDiffLine> Unified { get; }
    public int NumberWidth { get; }
    public int TextWidth { get; }

    private static List<string> Tokens(string text)
    {
        List<string> result = [];
        StringBuilder token = new();
        int previous = -1;
        TextElementEnumerator elements = StringInfo.GetTextElementEnumerator(text);
        while (elements.MoveNext())
        {
            string element = elements.GetTextElement();
            int kind = char.IsWhiteSpace(element, 0) ? 0
                : char.IsLetterOrDigit(element, 0) || element == "_" ? 1 : 2;
            if (token.Length > 0 && (kind != previous || kind == 2))
            {
                result.Add(token.ToString());
                token.Clear();
            }
            token.Append(element);
            previous = kind;
        }
        if (token.Length > 0) result.Add(token.ToString());
        return result;
    }

    internal static string ExpandTabs(string text, ref int column)
    {
        StringBuilder expanded = new();
        string[] segments = text.Split('\t');
        for (int i = 0; i < segments.Length; i++)
        {
            if (i > 0)
            {
                int spaces = 4 - column % 4;
                expanded.Append(' ', spaces);
                column += spaces;
            }
            expanded.Append(segments[i]);
            column += DisplayText.Width(segments[i]);
        }
        return expanded.ToString();
    }
}

internal static class FileDiffView
{
    public static PaneContent Render(ExplorerPresenter presenter, FileDiffState state, TextDiffContent diff)
    {
        FileDiffDocument document = diff.Document;
        int width = presenter.RightInner;
        int half = (width - 3) / 2;
        int gutter = state.UnifiedDiff ? 2 * document.NumberWidth + 4 : document.NumberWidth + 3;
        int textWidth = Math.Max(1, (state.UnifiedDiff ? width : half) - gutter);
        int maxColumn = Math.Max(0, document.TextWidth - textWidth);
        state.DiffColumn = Math.Clamp(state.DiffColumn, 0, maxColumn);
        int added = document.Unified.Count(static line => line.Source.Op == DiffOp.Insert);
        int removed = document.Unified.Count(static line => line.Source.Op == DiffOp.Delete);
        List<Line> lines =
        [
            new Line().Add(state.UnifiedDiff ? "Inline diff" : "Side-by-side diff", Theme.S(Theme.Foam, null, Deco.Bold))
                .Add(diff.Lines is null ? "" : $"   +{Fmt.N(added)} ", Theme.Kelp)
                .Add(diff.Lines is null ? "" : $"-{Fmt.N(removed)}", Theme.Garnet).Truncate(width),
            state.UnifiedDiff
                ? Line.Of($"{state.BaselineLabel} → {state.TargetLabel}", Theme.Silt).Truncate(width)
                : new Line().Add(Fmt.Fit(state.BaselineLabel, half).PadRight(half), Theme.Silt).Add(" │ ", Theme.Shale)
                    .Add(Fmt.Fit(state.TargetLabel, width - half - 3), Theme.Foam),
        ];
        if (diff.Message is not null)
        {
            lines.AddRange(Syntax.Wrap([(diff.Message, new Sty(Theme.Silt))], width, 4));
        }
        if (diff.Lines is not null)
        {
            int count = state.UnifiedDiff ? document.Unified.Count : document.Split.Count;
            int room = Math.Max(1, presenter.RightInnerHeight - lines.Count);
            state.DiffScroll = Math.Clamp(state.DiffScroll, 0, Math.Max(0, count - room));
            for (int i = state.DiffScroll; i < Math.Min(count, state.DiffScroll + room); i++)
            {
                if (state.UnifiedDiff)
                {
                    lines.Add(Row(document.Unified[i], width, document.NumberWidth, state.DiffColumn, unified: true));
                }
                else
                {
                    VisualDiffPair pair = document.Split[i];
                    lines.Add(Row(pair.Left, half, document.NumberWidth, state.DiffColumn, left: true)
                        .Add(" │ ", Theme.Shale)
                        .Append(Row(pair.Right, width - half - 3, document.NumberWidth, state.DiffColumn)));
                }
            }
            if (count == 0)
            {
                lines.Add(Line.Of("No text differences.", Theme.Silt));
            }
        }
        return ExplorerPresenter.Pane(lines, diff.Path.Split('/')[^1], true,
            $"/{diff.Path}" + (diff.Lines is null ? "" : $" · {Fmt.N(added + removed)} changed lines") +
            (maxColumn > 0 ? $" · column {state.DiffColumn + 1}" : ""));
    }

    private static Line Row(VisualDiffLine? line, int width, int digits, int pan, bool unified = false, bool left = false)
    {
        if (line is null)
        {
            return new Line().Pad(width, Theme.S(Theme.Silt, Theme.Graphite));
        }
        DiffLine source = line.Source;
        Rgb? background = source.Op switch
        {
            DiffOp.Delete => Theme.GarnetDeep,
            DiffOp.Insert => Theme.KelpDeep,
            _ => null,
        };
        Sty style = Theme.S(Theme.Foam, background);
        string Number(int? number) => (number?.ToString(CultureInfo.InvariantCulture) ?? "").PadLeft(digits);
        string number = unified ? $"{Number(source.OldLine)} {Number(source.NewLine)}"
            : Number(left ? source.OldLine : source.NewLine);
        string marker = source.Op == DiffOp.Insert ? "+" : source.Op == DiffOp.Delete ? "-" : " ";
        Line result = Line.Of($"{number} {marker} ", background is null ? Theme.S(Theme.Silt) : style);
        Line content = new();
        int column = 0;
        foreach (DiffSpan span in line.Spans)
        {
            Sty spanStyle = span.Changed
                ? Theme.S(Theme.Foam, source.Op == DiffOp.Delete ? Theme.DiffRemovedWord : Theme.DiffAddedWord,
                    Theme.NoColor ? Deco.Bold | Deco.Underline : Deco.Bold)
                : style;
            content.Add(FileDiffDocument.ExpandTabs(span.Text, ref column), spanStyle);
        }
        result.Append(content.Slice(pan, Math.Max(0, width - result.Length)).UnderBackground(background ?? Theme.Ground));
        return result.Truncate(width).Pad(width, style);
    }
}
