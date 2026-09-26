using System.Net;
using System.Text;
using Terminal.Gui.App;
using Terminal.Gui.Drawing;
using Terminal.Gui.Drivers;
using Terminal.Gui.ViewBase;
using Attribute = Terminal.Gui.Drawing.Attribute;

namespace Valleysoft.Dredge.Explorer.Tui;

// Renders views headlessly with Terminal.Gui's ANSI driver and reads the cell
// buffer back. The same technique works for snapshot tests.
internal static class Snapshot
{
    public sealed record Frame(string Text, string Html, string Ansi);

    public static Frame Capture(int width, int height, Func<IApplication, IEnumerable<(View Top, Action? AfterBegin)>> build)
    {
        IApplication app = Application.Create();
        app.Init(DriverRegistry.Names.ANSI);
        app.Driver!.SetScreenSize(width, height);
        List<SessionToken> tokens = [];
        foreach ((View top, Action? afterBegin) in build(app))
        {
            tokens.Add(app.Begin((IRunnable)top)!);
            afterBegin?.Invoke();
            app.LayoutAndDraw(true);
        }
        app.LayoutAndDraw(true);
        Frame frame = Read(app);
        for (int i = tokens.Count - 1; i >= 0; i--)
        {
            app.End(tokens[i]);
        }
        app.Dispose();
        return frame;
    }

    private static Frame Read(IApplication app)
    {
        Cell[,] cells = app.Driver!.Contents!;
        (int caretCol, int caretRow) = Caret(app);
        int rows = cells.GetLength(0), cols = cells.GetLength(1);
        StringBuilder text = new(), html = new(), ansi = new();
        for (int r = 0; r < rows; r++)
        {
            StringBuilder line = new();
            Attribute? run = null;
            StringBuilder chunk = new();
            void Flush()
            {
                if (chunk.Length == 0)
                {
                    return;
                }
                html.Append(Span(run, chunk.ToString()));
                ansi.Append(Sgr(run)).Append(chunk).Append("\u001b[0m");
                chunk.Clear();
            }
            for (int c = 0; c < cols; c++)
            {
                Cell cell = cells[r, c];
                string g = string.IsNullOrEmpty(cell.Grapheme) ? " " : cell.Grapheme;
                Attribute? a = cell.Attribute;
                if (r == caretRow && c == caretCol && a is Attribute at)
                {
                    // Show the terminal cursor the way a terminal would: an inverted cell.
                    a = new Attribute(at.Background, at.Foreground, at.Style);
                }
                if (!Equals(a, run))
                {
                    Flush();
                    run = a;
                }
                chunk.Append(g);
                line.Append(g);
            }
            Flush();
            text.Append(line.ToString().TrimEnd()).Append('\n');
            html.Append('\n');
            ansi.Append('\n');
        }
        return new(text.ToString().TrimEnd('\n'), html.ToString().TrimEnd('\n'), ansi.ToString());
    }

    private static (int Col, int Row) Caret(IApplication app)
    {
        View? focused = app.TopRunnableView?.MostFocused;
        if (focused is Terminal.Gui.Views.TextField field)
        {
            System.Drawing.Point p = field.ViewportToScreen(new System.Drawing.Point(field.InsertionPoint - field.ScrollOffset, 0));
            return (p.X, p.Y);
        }
        return (-1, -1);
    }

    private static string Hex(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";

    private static string Span(Attribute? a, string s)
    {
        string t = WebUtility.HtmlEncode(s);
        if (a is not Attribute at)
        {
            return t;
        }
        List<string> css = [$"color:{Hex(at.Foreground)}"];
        if (Hex(at.Background) != "#" + Theme.Ground.ToHex())
        {
            css.Add($"background:{Hex(at.Background)}");
        }
        if (at.Style.HasFlag(TextStyle.Bold)) css.Add("font-weight:700");
        if (at.Style.HasFlag(TextStyle.Faint)) css.Add("opacity:.6");
        if (at.Style.HasFlag(TextStyle.Italic)) css.Add("font-style:italic");
        List<string> lines = [];
        if (at.Style.HasFlag(TextStyle.Underline)) lines.Add("underline");
        if (at.Style.HasFlag(TextStyle.Strikethrough)) lines.Add("line-through");
        if (lines.Count > 0) css.Add("text-decoration:" + string.Join(' ', lines));
        return $"<span style=\"{string.Join(';', css)}\">{t}</span>";
    }

    private static string Sgr(Attribute? a)
    {
        if (a is not Attribute at)
        {
            return "";
        }
        StringBuilder sb = new($"\u001b[38;2;{at.Foreground.R};{at.Foreground.G};{at.Foreground.B};48;2;{at.Background.R};{at.Background.G};{at.Background.B}");
        if (at.Style.HasFlag(TextStyle.Bold)) sb.Append(";1");
        if (at.Style.HasFlag(TextStyle.Faint)) sb.Append(";2");
        if (at.Style.HasFlag(TextStyle.Italic)) sb.Append(";3");
        if (at.Style.HasFlag(TextStyle.Underline)) sb.Append(";4");
        if (at.Style.HasFlag(TextStyle.Strikethrough)) sb.Append(";9");
        return sb.Append('m').ToString();
    }
}
