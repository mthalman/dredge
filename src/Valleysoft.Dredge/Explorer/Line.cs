using System.Globalization;
using Terminal.Gui.Text;

namespace Valleysoft.Dredge.Explorer;

// Keep styles attached to text while measuring it with the driver's display-cell rules.
internal sealed class Line
{
    private readonly List<(string Text, Sty Sty)> parts = [];

    private int? length;
    public int Length => length ??= DisplayText.Width(ToString());

    public Line Add(string text, Sty? style = null)
    {
        if (text.Length == 0)
        {
            return this;
        }
        parts.Add((text, style ?? Sty.Plain));
        length = null;
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
        width = Math.Max(0, width);
        if (Length <= width)
        {
            return this;
        }
        int take = DisplayText.PrefixLength(ToString(), Math.Max(0, width - 1));
        List<(string, Sty)> kept = [];
        Sty ending = parts.Count > 0 ? parts[0].Sty : Sty.Plain;
        foreach (var (text, style) in parts)
        {
            if (take == 0)
            {
                break;
            }
            int count = Math.Min(take, text.Length);
            kept.Add((text[..count], style));
            ending = style;
            take -= count;
        }
        if (width > 0)
        {
            kept.Add(("…", ending));
        }
        parts.Clear();
        parts.AddRange(kept);
        length = null;
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
        string full = ToString();
        int skip = 0;
        int columns = 0;
        TextElementEnumerator elements = StringInfo.GetTextElementEnumerator(full);
        while (columns < start && elements.MoveNext())
        {
            string text = elements.GetTextElement();
            columns += DisplayText.Width(text);
            skip += text.Length;
        }
        if (columns > start)
        {
            line.Add(new string(' ', columns - start));
        }
        foreach (var (text, style) in parts)
        {
            if (skip >= text.Length)
            {
                skip -= text.Length;
                continue;
            }
            line.Add(text[skip..], style);
            skip = 0;
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

internal static class DisplayText
{
    public static int Width(string text)
    {
        foreach (char c in text)
        {
            if (c < ' ' || c > '~')
            {
                return text.GetColumns();
            }
        }
        return text.Length;
    }

    public static int PrefixLength(string text, int columns)
    {
        int count = 0;
        int used = 0;
        TextElementEnumerator elements = StringInfo.GetTextElementEnumerator(text);
        while (elements.MoveNext())
        {
            string element = elements.GetTextElement();
            int width = Width(element);
            if (used + width > columns)
            {
                break;
            }
            count += element.Length;
            used += width;
        }
        return count;
    }
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

    public static string Fit(string s, int width) => Line.Of(s).Truncate(width).ToString();

    public static long MB(double mb) => (long)(mb * 1_000_000);
    public static long KB(double kb) => (long)(kb * 1_000);
}
