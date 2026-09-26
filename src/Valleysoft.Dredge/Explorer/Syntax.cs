using System.Text.RegularExpressions;

namespace Valleysoft.Dredge.Explorer;

internal static partial class Syntax
{
    public static List<(string Text, Sty Sty)> Dockerfile(string instruction, Rgb args)
    {
        List<(string, Sty)> tokens = [];
        int sp = instruction.IndexOf(' ');
        string keyword = sp < 0 ? instruction : instruction[..sp];
        tokens.Add((keyword, new Sty(Theme.DfKeyword)));
        if (sp < 0)
        {
            return tokens;
        }
        foreach (Match m in DockerTokens().Matches(instruction[sp..]))
        {
            Sty style =
                m.Groups["str"].Success ? new Sty(Theme.DfString) :
                m.Groups["flag"].Success ? new Sty(Theme.DfLiteral) :
                m.Groups["sym"].Success ? new Sty(Theme.DfSymbol) :
                new Sty(args);
            tokens.Add((m.Value, style));
        }
        return tokens;
    }

    public static Line DockerfileLine(string instruction, Rgb args, int width)
    {
        Line line = new();
        foreach (var (text, style) in Dockerfile(instruction, args))
        {
            line.Add(text, style);
        }
        return line.Truncate(width);
    }

    public static List<Line> Wrap(List<(string Text, Sty Sty)> tokens, int width, int maxLines, int indent = 0)
    {
        // Split whitespace into separate tokens so lines only break between words.
        List<(string Text, Sty Sty)> words = [];
        foreach (var (text, style) in tokens)
        {
            foreach (Match m in Words().Matches(text))
            {
                words.Add((m.Value, style));
            }
        }

        List<Line> lines = [new Line()];
        foreach (var (text, style) in words)
        {
            Line current = lines[^1];
            bool isSpace = string.IsNullOrWhiteSpace(text);
            if (current.Length + text.Length > width && !isSpace)
            {
                if (lines.Count == maxLines)
                {
                    current.Add(text, style).Truncate(width);
                    if (!current.ToString()!.EndsWith('…'))
                    {
                        current.Truncate(width - 1).Add("…", style);
                    }
                    return lines;
                }
                lines.Add(new Line().Add(new string(' ', indent)));
                current = lines[^1];
            }
            if (isSpace && current.Length == (lines.Count == 1 ? 0 : indent))
            {
                continue;
            }
            current.Add(text, style);
        }
        return lines;
    }

    public static Line Json(string line)
    {
        Line result = new();
        foreach (Match m in JsonTokens().Matches(line))
        {
            Rgb color =
                m.Groups["key"].Success ? Theme.DfLiteral :
                m.Groups["str"].Success ? Theme.DfString :
                m.Groups["num"].Success ? Theme.JsonNumber :
                m.Groups["punct"].Success ? Theme.Silt :
                Theme.Foam;
            result.Add(m.Value, color);
        }
        return result;
    }

    [GeneratedRegex("""(?<str>"[^"]*"|'[^']*')|(?<flag>--[\w-]+(=\S+)?)|(?<sym>&&|\|\||[\[\],;\\|])|(?<ws>\s+)|(?<word>[^\s"'\[\],;\\|&]+|&)""")]
    private static partial Regex DockerTokens();

    [GeneratedRegex(@"\s+|\S+")]
    private static partial Regex Words();

    [GeneratedRegex("""(?<key>"[^"]*"(?=\s*:))|(?<str>"[^"]*")|(?<num>\b\d[\d.]*\b|true|false|null)|(?<punct>[{}\[\]:,])|(?<ws>\s+)|(?<other>[^\s"{}\[\]:,]+)""")]
    private static partial Regex JsonTokens();
}
