using System.Drawing;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;

namespace Valleysoft.Dredge.Explorer.Tui;

// A framed pane that draws whatever its presenter returns. It owns no state:
// clicks become the Cmd attached to the region under the pointer, wheel
// notches become Move commands, and the controller decides what they mean.
internal sealed class PaneView : View
{
    private readonly Func<PaneContent> content;
    private PaneContent? drawn;
    private PaneContent? latest;
    private Rectangle drawnFrame;
    // Set when Update has just computed the content, so the draw that follows can reuse it.
    private bool fresh;

    public PaneView(Func<PaneContent> content, bool focusable)
    {
        this.content = content;
        CanFocus = focusable;
        TabStop = focusable ? TabBehavior.TabStop : TabBehavior.NoStop;
        // The default bindings activate and focus the pane on any click; OnMouseEvent decides instead.
        MouseBindings.Clear();
    }

    public event Action<Cmd>? Command;

    // Double-click runs the region's command and then Activate, like Enter.
    public bool ActivateOnDoubleClick { get; init; }

    // While a text field is being typed into, the mouse still drives the pane
    // but leaves focus in the field, so the next keystroke isn't read as a command.
    public Func<bool>? KeepsFocusAway { get; init; }

    private void TakeFocus()
    {
        if (CanFocus && !HasFocus && KeepsFocusAway?.Invoke() != true)
        {
            SetFocus();
        }
    }

    // Recomputes the content but repaints only when it would look different:
    // every repainted cell is written to the terminal again.
    public void Update()
    {
        // A hidden or not-yet-laid-out pane has no geometry to present; it repaints in full once shown.
        if (!Visible || Viewport.Width <= 0 || Viewport.Height <= 0)
        {
            drawn = null;
            SetNeedsDraw();
            return;
        }
        PaneContent c = latest = content();
        fresh = true;
        if (drawn is null || !c.LooksLike(drawn))
        {
            SetNeedsDraw();
        }
    }

    // Every cell is painted in OnDrawingContent, so clearing first is wasted work.
    protected override bool OnClearingViewport() => true;

    // Whether the screen buffer still holds exactly what this window drew last
    // frame: no dialog, popover, clear, or resize has touched it since.
    public Func<bool>? BufferIntact { get; init; }

    protected override bool OnDrawingContent(DrawContext? context)
    {
        PaneContent? before = drawn;
        Rectangle frame = FrameToScreen();
        PaneContent c = drawn = latest = fresh && latest is not null ? latest : content();
        fresh = false;
        bool partial = before is not null && before.Inset == c.Inset && frame == drawnFrame && BufferIntact?.Invoke() == true;
        drawnFrame = frame;
        if (Viewport.Width < 4 || Viewport.Height < 2)
        {
            Paint.Clear(this);
            return true;
        }
        // Each painted cell costs several microseconds inside Terminal.Gui, so
        // when the buffer is intact only rows that changed are painted again.
        if (!partial || c.Title != before!.Title || c.Subtitle != before.Subtitle || c.Focused != before.Focused)
        {
            Paint.Frame(this, c.Title, c.Subtitle, c.Focused);
        }
        int left = 1 + c.Inset, inner = Viewport.Width - 2 - 2 * c.Inset;
        for (int i = 0; i < Viewport.Height - 2; i++)
        {
            Line? line = i < c.Lines.Count ? c.Lines[i] : null;
            if (partial)
            {
                Line? old = i < before!.Lines.Count ? before.Lines[i] : null;
                if (line is null ? old is null : old is not null && line.SameAs(old))
                {
                    continue;
                }
            }
            else
            {
                Paint.Fill(this, 1, 1 + i, null, c.Inset);
                Paint.Fill(this, Viewport.Width - 1 - c.Inset, 1 + i, null, c.Inset);
            }
            if (partial)
            {
                Paint.Patch(this, left, 1 + i, line, i < before!.Lines.Count ? before.Lines[i] : null, inner);
            }
            else
            {
                Paint.Fill(this, left, 1 + i, line, inner);
            }
        }
        return true;
    }

    protected override bool OnMouseEvent(Mouse mouse)
    {
        if (mouse.Flags.HasFlag(MouseFlags.WheeledDown) || mouse.Flags.HasFlag(MouseFlags.WheeledUp))
        {
            // The wheel scrolls the pane under the pointer, so it takes focus first.
            TakeFocus();
            Command?.Invoke(new Move(mouse.Flags.HasFlag(MouseFlags.WheeledDown) ? 3 : -3));
            return true;
        }
        // Acting on press rather than on the click that follows release saves
        // the whole time the button is held down.
        bool press = MousePress.Is(mouse);
        bool twice = mouse.Flags.HasFlag(MouseFlags.LeftButtonDoubleClicked);
        if (!press && !twice)
        {
            return mouse.Flags.HasFlag(MouseFlags.LeftButtonClicked);
        }
        if (press)
        {
            TakeFocus();
        }
        if (mouse.Position is { } p && latest?.HitTest(p.Y - 1, p.X - 1 - latest.Inset) is Cmd cmd)
        {
            if (press)
            {
                Command?.Invoke(cmd);
            }
            // Only rows open on double-click; a fast second click on a chip or toggle is just a click.
            else if (ActivateOnDoubleClick && cmd is SetCursor or SelectFinding or SelectSearchHit)
            {
                Command?.Invoke(new Activate());
            }
        }
        return true;
    }
}
