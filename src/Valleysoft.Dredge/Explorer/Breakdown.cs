namespace Valleysoft.Dredge.Explorer;

// A proportional bar with a legend underneath, drawn with Lines so it looks the
// same in every pane that uses it.
internal static class Breakdown
{
    public static List<Line> Render(List<(string Label, long Value, Rgb Color)> parts, int width)
    {
        long total = parts.Sum(static p => p.Value);
        if (total <= 0 || parts.Count == 0)
        {
            return [Line.Of(new string('█', Math.Max(0, width)), Theme.Shale)];
        }
        int[] cells = new int[parts.Count];
        double carry = 0;
        for (int i = 0; i < parts.Count; i++)
        {
            double exact = (double)parts[i].Value / total * width + carry;
            cells[i] = Math.Max(parts[i].Value > 0 ? 1 : 0, (int)Math.Round(exact));
            carry = exact - cells[i];
        }
        cells[^1] = Math.Max(0, cells[^1] + width - cells.Sum());

        Line bar = new();
        for (int i = 0; i < parts.Count; i++)
        {
            bar.Add(new string('█', cells[i]), parts[i].Color);
        }

        List<Line> lines = [bar];
        Line legend = new();
        foreach (var (label, value, color) in parts)
        {
            Line tag = new Line().Add("■ ", color).Add(label + " ", Theme.Silt).Add(Fmt.Size(value), Theme.Foam);
            if (legend.Length > 0 && legend.Length + 3 + tag.Length > width)
            {
                lines.Add(legend);
                legend = new();
            }
            if (legend.Length > 0)
            {
                legend.Add("   ");
            }
            legend.Append(tag);
        }
        lines.Add(legend);
        return lines;
    }
}
