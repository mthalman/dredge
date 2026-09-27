using System.Globalization;

namespace Valleysoft.Dredge.Explorer;

// A single styled terminal row, independent of any rendering library. Building
// rows from spans gives exact column control for padding, truncation, and
// full-row selection highlights.
internal sealed class Line
{
    private readonly List<(string Text, Sty Sty)> parts = [];

    public int Length { get; private set; }

    public Line Add(string text, Sty? style = null)
    {
        if (text.Length == 0)
        {
            return this;
        }
        parts.Add((text, style ?? Sty.Plain));
        Length += text.Length;
        return this;
    }

    public Line Add(string text, Rgb fg, Deco decoration = Deco.None) =>
        Add(text, new Sty(fg, null, decoration));

    public Line Append(Line other)
    {
        foreach (var (text, style) in other.parts)
        {
            Add(text, style);
        }
        return this;
    }

    public Line Pad(int width, Sty? style = null)
    {
        if (Length < width)
        {
            Add(new string(' ', width - Length), style);
        }
        return this;
    }

    // Right-align `right` within `width`, truncating this line if needed.
    public Line PadRight(int width, Line right)
    {
        int room = width - right.Length;
        if (Length > room)
        {
            Truncate(Math.Max(0, room - 1));
            Add(" ");
        }
        Pad(room);
        return Append(right);
    }

    public Line Truncate(int width)
    {
        if (Length <= width)
        {
            return this;
        }
        List<(string, Sty)> kept = [];
        int used = 0;
        foreach (var (text, style) in parts)
        {
            if (used + text.Length < width)
            {
                kept.Add((text, style));
                used += text.Length;
                continue;
            }
            int take = Math.Max(0, width - used - 1);
            kept.Add((text[..take] + "…", style));
            used += take + 1;
            break;
        }
        parts.Clear();
        parts.AddRange(kept);
        Length = used;
        return this;
    }

    public Line WithBackground(Rgb bg)
    {
        Line line = new();
        foreach (var (text, style) in parts)
        {
            line.Add(text, new Sty(bg == Theme.ChannelDeep && style.Foreground == Theme.Silt ? Theme.Foam : style.Foreground, bg, style.Deco));
        }
        return line;
    }

    public Line Slice(int start, int width)
    {
        Line line = new();
        foreach (var (text, style) in parts)
        {
            if (start >= text.Length)
            {
                start -= text.Length;
                continue;
            }
            line.Add(text[start..], style);
            start = 0;
        }
        return line.Truncate(width);
    }

    // Like WithBackground, but keeps backgrounds the parts already set.
    public Line UnderBackground(Rgb bg)
    {
        Line line = new();
        foreach (var (text, style) in parts)
        {
            Rgb? foreground = bg == Theme.ChannelDeep && style.Foreground == Theme.Silt ? Theme.Foam : style.Foreground;
            line.Add(text, style.Background is null ? new Sty(foreground, bg, style.Deco) : style);
        }
        return line;
    }

    public IReadOnlyList<(string Text, Sty Sty)> Parts => parts;

    public bool SameAs(Line other) => Length == other.Length && parts.SequenceEqual(other.parts);

    public static bool SameAs(IReadOnlyList<Line> left, IReadOnlyList<Line> right)
    {
        if (left.Count != right.Count)
        {
            return false;
        }
        for (int i = 0; i < left.Count; i++)
        {
            if (!left[i].SameAs(right[i]))
            {
                return false;
            }
        }
        return true;
    }

    public static Line Of(string text, Sty? style = null) => new Line().Add(text, style);

    public override string ToString() => string.Concat(parts.Select(p => p.Text));
    public static Line Of(string text, Rgb fg, Deco d = Deco.None) => new Line().Add(text, fg, d);
    public static Line Blank => new();
}

internal static class Fmt
{
    public static string Size(long bytes)
    {
        CultureInfo c = CultureInfo.InvariantCulture;
        if (bytes < 1000)
        {
            return $"{bytes} B";
        }
        double kb = bytes / 1000.0;
        if (kb < 1000)
        {
            return kb < 10 ? $"{kb.ToString("0.0", c)} KB" : $"{kb.ToString("0", c)} KB";
        }
        double mb = kb / 1000.0;
        if (mb < 1000)
        {
            return $"{mb.ToString("0.0", c)} MB";
        }
        return $"{(mb / 1000.0).ToString("0.00", c)} GB";
    }

    public static string SizeShort(long bytes)
    {
        double mb = bytes / 1_000_000.0;
        return mb >= 10 ? $"{Math.Round(mb):0} MB" : Size(bytes);
    }

    public static string N(int n) => n.ToString("N0", CultureInfo.InvariantCulture);

    // "1 file", "2 files": a count with its noun in the right number.
    public static string Count(int n, string singular, string? plural = null) =>
        $"{N(n)} {(n == 1 ? singular : plural ?? singular + "s")}";

    public static string Fit(string s, int width) =>
        s.Length <= width ? s : width <= 1 ? "…" : s[..(width - 1)] + "…";

    public static long MB(double mb) => (long)(mb * 1_000_000);
    public static long KB(double kb) => (long)(kb * 1_000);
}
