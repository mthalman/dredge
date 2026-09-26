using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;

namespace Valleysoft.Dredge.Explorer.Tui;

// Title row plus the core sample. Clicking a stratum selects that layer.
internal sealed class HeaderView : View
{
    private readonly Func<List<Line>> lines;
    private readonly Func<int, int?> layerAt;
    private List<Line>? drawn;
    private List<Line>? latest;

    public HeaderView(Func<List<Line>> lines, Func<int, int?> layerAt)
    {
        this.lines = lines;
        this.layerAt = layerAt;
        CanFocus = false;
    }

    public event Action<Cmd>? Command;

    public void Update()
    {
        latest = lines();
        if (drawn is null || !Line.SameAs(latest, drawn))
        {
            SetNeedsDraw();
        }
    }

    protected override bool OnClearingViewport() => true;

    protected override bool OnDrawingContent(DrawContext? context)
    {
        List<Line> l = drawn = latest ?? lines();
        latest = null;
        for (int i = 0; i < Viewport.Height; i++)
        {
            Paint.Fill(this, 0, i, i < l.Count ? l[i] : null, Viewport.Width);
        }
        return true;
    }

    protected override bool OnMouseEvent(Mouse mouse)
    {
        if (!MousePress.Is(mouse) || mouse.Position is not { } p || p.Y < 1)
        {
            return mouse.Flags.HasFlag(MouseFlags.LeftButtonClicked);
        }
        if (layerAt(p.X) is int layer)
        {
            Command?.Invoke(new SelectLayer(layer));
        }
        return true;
    }
}
