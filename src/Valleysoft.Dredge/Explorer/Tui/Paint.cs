using Terminal.Gui.Drawing;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Text;
using System.Text;
using System.Globalization;
using Attribute = Terminal.Gui.Drawing.Attribute;

namespace Valleysoft.Dredge.Explorer.Tui;

// The only place the explorer meets Terminal.Gui's drawing types: palette
// tokens become Colors, a Line becomes AddStr calls, and pane chrome is drawn
// from the same tokens.
internal static class Paint
{
    public static Color ToColor(this Rgb c) => new(c.R, c.G, c.B);

    public static Attribute ToAttribute(this Sty s)
    {
        TextStyle t = TextStyle.None;
        if (s.Deco.HasFlag(Deco.Bold)) t |= TextStyle.Bold;
        if (s.Deco.HasFlag(Deco.Dim)) t |= TextStyle.Faint;
        if (s.Deco.HasFlag(Deco.Italic)) t |= TextStyle.Italic;
        if (s.Deco.HasFlag(Deco.Underline)) t |= TextStyle.Underline;
        if (s.Deco.HasFlag(Deco.Strikethrough)) t |= TextStyle.Strikethrough;
        if (Theme.NoColor)
        {
            // NO_COLOR keeps the terminal's own colors; selection and keycaps
            // stay visible through reverse video instead of a background.
            if (s.Background is Rgb bg && (bg == Theme.ChannelDeep || bg == Theme.KeycapBg || bg == Theme.Channel))
            {
                t |= TextStyle.Reverse;
            }
            return new Attribute(Color.None, Color.None, t);
        }
        return new Attribute((s.Foreground ?? Theme.Foam).ToColor(), (s.Background ?? Theme.Ground).ToColor(), t);
    }

    public static Attribute Attr(Rgb fg, Rgb? bg = null, Deco d = Deco.None) => new Sty(fg, bg, d).ToAttribute();

    public static void Clear(View v)
    {
        v.SetAttribute(Attr(Theme.Foam));
        string blank = new(' ', v.Viewport.Width);
        for (int row = 0; row < v.Viewport.Height; row++)
        {
            v.AddStr(0, row, blank);
        }
    }

    public static int Draw(View v, int col, int row, Line line, int width)
    {
        v.Move(col, row);
        Line visible = line.Length > width ? line.Slice(0, width) : line;
        if (Simple(visible))
        {
            foreach (var (text, sty) in visible.Parts)
            {
                v.SetAttribute(sty.ToAttribute());
                v.AddStr(text);
            }
        }
        else
        {
            TextElementEnumerator elements = StringInfo.GetTextElementEnumerator(visible.ToString());
            int part = 0, end = visible.Parts.Count > 0 ? visible.Parts[0].Text.Length : 0;
            Sty? style = null;
            StringBuilder run = new();
            while (elements.MoveNext())
            {
                while (elements.ElementIndex >= end && part < visible.Parts.Count - 1)
                {
                    end += visible.Parts[++part].Text.Length;
                }
                Sty next = visible.Parts[part].Sty;
                if (style is not null && style != next)
                {
                    v.SetAttribute(style.ToAttribute());
                    v.AddStr(run.ToString());
                    run.Clear();
                }
                style = next;
                run.Append(elements.GetTextElement());
            }
            if (style is not null)
            {
                v.SetAttribute(style.ToAttribute());
                v.AddStr(run.ToString());
            }
        }
        return visible.Length;
    }

    // Draws the line and blanks the rest of the width, so a view can paint every
    // cell exactly once instead of clearing first: each cell written costs a clip test.
    public static void Fill(View v, int col, int row, Line? line, int width)
    {
        int used = 0;
        if (line is not null)
        {
            used = Draw(v, col, row, line, width);
        }
        if (used < width)
        {
            v.SetAttribute(Attr(Theme.Foam));
            v.AddStr(col + used, row, new string(' ', width - used));
        }
    }

    // Repaints only the runs of cells where `line` differs from `old`, which
    // the buffer is known to hold. Lines with wide or combined characters,
    // whose columns don't map one-to-one onto chars, are painted whole.
    public static void Patch(View v, int col, int row, Line? line, Line? old, int width)
    {
        if (!Simple(line) || !Simple(old))
        {
            Fill(v, col, row, line, width);
            return;
        }
        (char[] chars, Sty[] styles) = Cells(line, width);
        (char[] was, Sty[] wasStyles) = Cells(old, width);
        int x = 0;
        while (x < width)
        {
            if (chars[x] == was[x] && styles[x] == wasStyles[x])
            {
                x++;
                continue;
            }
            int start = x;
            Sty style = styles[x];
            while (x < width && styles[x] == style && (chars[x] != was[x] || wasStyles[x] != style))
            {
                x++;
            }
            v.Move(col + start, row);
            v.SetAttribute(styles[start].ToAttribute());
            v.AddStr(new string(chars, start, x - start));
        }
    }

    private static readonly Sty Blank = new(Theme.Foam, null, Deco.None);

    private static (char[], Sty[]) Cells(Line? line, int width)
    {
        char[] chars = new char[width];
        Sty[] styles = new Sty[width];
        Array.Fill(chars, ' ');
        Array.Fill(styles, Blank);
        if (line is null)
        {
            return (chars, styles);
        }
        int x = 0;
        foreach (var (text, sty) in line.Parts)
        {
            for (int i = 0; i < text.Length && x < width; i++, x++)
            {
                chars[x] = text[i];
                styles[x] = sty;
            }
        }
        return (chars, styles);
    }

    private static bool Simple(Line? line)
    {
        if (line is null)
        {
            return true;
        }
        foreach (var (text, _) in line.Parts)
        {
            foreach (char c in text)
            {
                if (char.IsSurrogate(c) || new Rune(c).GetColumns() != 1)
                {
                    return false;
                }
            }
        }
        return true;
    }

    // Rounded and quiet when idle, heavy and in the channel color when focused,
    // so focus reads from across the room without relying on color alone.
    public static void Frame(View v, string title, string? subtitle, bool focused)
    {
        int w = v.Viewport.Width, h = v.Viewport.Height;
        if (w < 4 || h < 2)
        {
            return;
        }
        (char tl, char tr, char bl, char br, char hz, char vt) = focused
            ? ('┏', '┓', '┗', '┛', '━', '┃')
            : ('╭', '╮', '╰', '╯', '─', '│');
        Attribute edge = Attr(focused ? Theme.Channel : Theme.Shale);

        Line top = new Line().Add($"{tl}{hz} ", Theme.S(focused ? Theme.Channel : Theme.Shale))
            .Add(title, Theme.S(Theme.Foam, null, Deco.Bold));
        if (subtitle is not null)
        {
            top.Add("  " + subtitle, Theme.Silt);
        }
        top.Add(" ");
        top.Truncate(w - 1);
        top.Add(new string(hz, Math.Max(0, w - 1 - top.Length)), Theme.S(focused ? Theme.Channel : Theme.Shale))
            .Add(tr.ToString(), Theme.S(focused ? Theme.Channel : Theme.Shale));
        Draw(v, 0, 0, top, w);

        v.SetAttribute(edge);
        for (int row = 1; row < h - 1; row++)
        {
            v.AddStr(0, row, vt.ToString());
            v.AddStr(w - 1, row, vt.ToString());
        }
        v.AddStr(0, h - 1, bl + new string(hz, w - 2) + br);
    }
}

internal static class MousePress
{
    // A left press that isn't part of a drag.
    public static bool Is(Terminal.Gui.Input.Mouse m) =>
        m.Flags.HasFlag(Terminal.Gui.Input.MouseFlags.LeftButtonPressed)
        && !m.Flags.HasFlag(Terminal.Gui.Input.MouseFlags.PositionReport);
}
