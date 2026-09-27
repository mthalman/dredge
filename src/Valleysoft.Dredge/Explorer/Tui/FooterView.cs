using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;

namespace Valleysoft.Dredge.Explorer.Tui;

// The bottom row: key hints on the left, the status note on the right. It is
// one view painted from data, so a keypress never rebuilds widgets, and it
// repaints only when the row would look different.
internal sealed class FooterView : View
{
    private static readonly Sty KeyStyle = new(Theme.Foam, Theme.KeycapBg, Deco.Bold);

    private Line row = new();
    private List<(int Start, int End, Cmd Cmd)> hits = [];

    public FooterView()
    {
        CanFocus = false;
        MouseBindings.Clear();
    }

    public event Action<Cmd>? Command;

    public void Show(IReadOnlyList<Hint> shown, string status, bool error, int width)
    {
        Line line = new();
        List<(int, int, Cmd)> regions = [];
        for (int i = 0; i < shown.Count; i++)
        {
            Hint h = shown[i];
            if (i > 0)
            {
                line.Add("│", Theme.Silt);
            }
            int start = line.Length;
            line.Add(" ", Theme.Silt).Add(h.Key, KeyStyle).Add("  " + h.Label + " ", Theme.Silt);
            if (h.Cmd is Cmd cmd)
            {
                regions.Add((start, line.Length, cmd));
            }
        }
        int statusWidth = DisplayText.Width(status);
        line.Truncate(Math.Max(0, width - statusWidth));
        line.Pad(width - statusWidth, Theme.S(Theme.Silt));
        line.Add(status, error ? Theme.Garnet : Theme.Silt);
        hits = regions;
        if (!line.SameAs(row))
        {
            row = line;
            SetNeedsDraw();
        }
    }

    protected override bool OnClearingViewport() => true;

    protected override bool OnDrawingContent(DrawContext? context)
    {
        Paint.Fill(this, 0, 0, row, Viewport.Width);
        return true;
    }

    protected override bool OnMouseEvent(Mouse mouse)
    {
        if (!MousePress.Is(mouse) || mouse.Position is not { } p)
        {
            return mouse.Flags.HasFlag(MouseFlags.LeftButtonClicked);
        }
        foreach ((int start, int end, Cmd cmd) in hits)
        {
            if (p.X >= start && p.X < end)
            {
                Command?.Invoke(cmd);
                return true;
            }
        }
        return true;
    }
}
