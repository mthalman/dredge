using Spectre.Console;
using System.Text;
using System.Text.RegularExpressions;
using Valleysoft.DockerfileModel;
using Valleysoft.DockerfileModel.Tokens;
using Valleysoft.DockerRegistryClient;
using Valleysoft.DockerRegistryClient.Models.Images;
using Valleysoft.DockerRegistryClient.Models.Manifests;
using ImageConfig = Valleysoft.DockerRegistryClient.Models.Images.Image;

namespace Valleysoft.Dredge.Commands.Image;

public partial class DockerfileCommand : RegistryCommandBase<DockerfileOptions>
{
    private static readonly Color SymbolColor = new(250, 200, 31); // yellow
    private static readonly Color StringColor = new(202, 145, 120); // tan
    private static readonly Color KeywordColor = new(194, 133, 191); // lavender
    private static readonly Color CommentColor = new(109, 154, 88); // green-ish
    private static readonly Color LiteralColor = new(150, 220, 254); // light turquoise
    private static readonly Color IdentifierColor = Color.Green;
    private readonly IDockerRegistryClientFactory dockerRegistryClientFactory;
    private readonly IAnsiConsole ansiConsole;

    public DockerfileCommand(IDockerRegistryClientFactory dockerRegistryClientFactory)
        : this(dockerRegistryClientFactory, AnsiConsole.Console)
    {
    }

    internal DockerfileCommand(
        IDockerRegistryClientFactory dockerRegistryClientFactory,
        IAnsiConsole ansiConsole)
        : base("dockerfile", "Generates a Dockerfile that represents the image", dockerRegistryClientFactory)
    {
        this.dockerRegistryClientFactory = dockerRegistryClientFactory;
        this.ansiConsole = ansiConsole;
    }

    protected override async Task ExecuteAsync(CancellationToken cancellationToken)
    {
        ImageSourceReference reference = ImageSourceReference.Parse(Options.Image);
        await ExecuteCommandAsync(reference.Registry, cancellationToken, async ct =>
        {
            Markup markupOutput = new(await GetMarkupStringAsync(ct));
            ansiConsole.Write(markupOutput);
        });
    }

    public async Task<string> GetMarkupStringAsync(CancellationToken cancellationToken = default)
    {
        await using ResolvedImage source = await OpenImageAsync(Options.Image, Options, cancellationToken,
            requirements: ImageSourceRequirements.Metadata);
        source.Report(Error);
        IImageManifest manifest = source.Resolved.Manifest;
        ImageConfig imageConfig = source.Configuration;
        bool isWindows = imageConfig.Os.Equals("windows", StringComparison.OrdinalIgnoreCase);

        List<string> dockerfileLines = [];
        IEnumerable<LayerHistory> layers;

        if (isWindows)
        {
            WindowsImageInfo windowsImageInfo =
                await GetWindowsInfoAsync(manifest, imageConfig, cancellationToken, !source.IsDaemon,
                    source.SelectedPlatform);
            layers = imageConfig.History.Skip(windowsImageInfo.BaseHistoryCount);
            dockerfileLines.Add(
                $"FROM {RegistryHelper.McrRegistry}/{windowsImageInfo.Repo}:{windowsImageInfo.Info.Version}-{imageConfig.Architecture}");
        }
        else
        {
            layers = imageConfig.History;
            dockerfileLines.Add("FROM scratch");
        }

        string? currentShell = null;

        foreach (LayerHistory layerHistory in layers)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrEmpty(layerHistory.CreatedBy))
            {
                dockerfileLines.Add("# No instruction info");
                continue;
            }

            string line = GetHistoryLine(layerHistory.CreatedBy, ref currentShell);
            dockerfileLines.Add(line);
        }

        StringBuilder markup = new();
        foreach (string line in dockerfileLines)
        {
            AppendLineMarkup(line, markup, cancellationToken);
        }

        return markup.ToString();
    }

    private void AppendLineMarkup(
        string line,
        StringBuilder markup,
        CancellationToken cancellationToken)
    {
        string source = line + Environment.NewLine;
        Dockerfile dockerfile = Dockerfile.Parse(source);

        if (!IsParseLossless(source, dockerfile))
        {
            // Image history can be ambiguous after the builder discards original quoting.
            // Preserve it verbatim rather than silently dropping text or changing its meaning.
            KeywordToken? keywordToken = dockerfile.Items
                .SelectMany(static construct => construct.Tokens)
                .OfType<KeywordToken>()
                .FirstOrDefault();
            string? keyword = keywordToken?.ToString();
            if (keywordToken is not null &&
                keyword is not null &&
                line.StartsWith(keyword, StringComparison.OrdinalIgnoreCase))
            {
                markup.Append(GetTokenMarkup(keywordToken, inRunInstruction: false));
                markup.AppendLine(Markup.Escape(line[keyword.Length..]));
            }
            else
            {
                markup.AppendLine(Markup.Escape(line));
            }
            return;
        }

        foreach (DockerfileConstruct construct in dockerfile.Items)
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (Token token in construct.Tokens)
            {
                markup.Append(GetTokenMarkup(token, construct is RunInstruction));
            }
        }
    }

    private static bool IsParseLossless(string source, Dockerfile dockerfile) =>
        string.Equals(
            source,
            string.Concat(dockerfile.Items.SelectMany(static construct => construct.Tokens)),
            StringComparison.Ordinal);

    private async Task<WindowsImageInfo> GetWindowsInfoAsync(
        IImageManifest manifest,
        ImageConfig imageConfig,
        CancellationToken cancellationToken,
        bool useDistributionDigest, ExplorerPlatform? selectedPlatform)
    {
        WindowsImageInfo windowsOsInfo = await OsCommand.GetWindowsOsInfoAsync(
            imageConfig, manifest, dockerRegistryClientFactory, cancellationToken, useDistributionDigest,
            selectedPlatform) ??
            throw new Exception("Could not determine info about the Windows image.");
        if (string.IsNullOrEmpty(windowsOsInfo.Info.Version))
        {
            throw new Exception("No os.version information defined for the Windows image.");
        }

        return windowsOsInfo;
    }

    private string GetHistoryLine(string line, ref string? currentShell)
    {
        const string NopMarker = "#(nop)";

        int nopIndex;
        if (currentShell is not null && line.StartsWith(currentShell, StringComparison.Ordinal))
        {
            nopIndex = currentShell.Length;
        }
        else
        {
            nopIndex = FindHistoryMetadataIndex(line);
        }
        if (nopIndex < 0)
        {
            nopIndex = 0;
        }
        while (nopIndex < line.Length && line[nopIndex] is ' ' or '\t')
        {
            nopIndex++;
        }
        if (!line.AsSpan(nopIndex).StartsWith(NopMarker, StringComparison.Ordinal) ||
            nopIndex + NopMarker.Length >= line.Length ||
            line[nopIndex + NopMarker.Length] is not (' ' or '\t'))
        {
            nopIndex = -1;
        }
        if (nopIndex >= 0 || currentShell is null)
        {
            if (nopIndex >= 0)
            {
                currentShell ??= line[0..nopIndex].Trim();
                line = line[(nopIndex + NopMarker.Length)..].Trim();
            }
            
            if (line.StartsWith("ADD", StringComparison.OrdinalIgnoreCase) ||
                line.StartsWith("COPY", StringComparison.OrdinalIgnoreCase))
            {
                // The history of ADD and COPY instructions includes this "in" keyword that isn't valid in a Dockerfile.
                // e.g. ADD file:c13b430c8699df107ffd9ea5230b92238bc037a8e1cbbe35d6ab664941d575da in /
                // This trims out the first occurrence of it.
                const string InMarker = " in ";
                int inIndex = line.IndexOf(InMarker, StringComparison.Ordinal);
                if (inIndex >= 0)
                {
                    string linePrefix = line[..inIndex];
                    string lineSuffix = line[(inIndex + InMarker.Length - 1)..];
                    line = $"{linePrefix}{lineSuffix}";
                }
            }

            if (line.StartsWith("SHELL", StringComparison.OrdinalIgnoreCase))
            {
                // The SHELL instruction doesn't include quotes in its JSON format which isn't valid Dockerfile syntax
                // so this injects them back in.
                if (!line.Contains('\"'))
                {
                    line = line
                        .Replace("[", "[\"")
                        .Replace("]", "\"]");
                    int cmdIndex = line.IndexOf('[');
                    if (cmdIndex >= 0)
                    {
                        string command = line[cmdIndex..]
                            .Replace(" ", "\", \"");
                        line = line[0..cmdIndex] + command;
                    }
                }
            }

            Dockerfile? dockerfile = null;
            string source = line + Environment.NewLine;
            try
            {
                dockerfile = Dockerfile.Parse(source);
            }
            catch (Exception)
            {
            }

            if (dockerfile is not null)
            {
                DockerfileConstruct dockerfileConstruct = dockerfile.Items.First();
                if (dockerfileConstruct is ShellInstruction shellInstruction)
                {
                    // Track the current SHELL. This is needed in order to trim it from subsequent instructions.
                    if (shellInstruction.Command is ExecFormCommand execFormCommand && execFormCommand.Values is not null && execFormCommand.Values.Count > 0)
                    {
                        currentShell = string.Join(" ", execFormCommand.Values);
                    }
                }
                else if (dockerfileConstruct is EnvInstruction envInstruction)
                {
                    if (!Options.NoFormat && IsParseLossless(source, dockerfile))
                    {
                        line = FormatEnvInstruction(line, envInstruction);
                    }
                }
            }
        }
        else
        {
            if (currentShell is null)
            {
                throw new Exception("Unable to determine initial shell from instructions.");
            }

            if (line.StartsWith(currentShell))
            {
                line = line[currentShell.Length..].Trim();

                if (!Options.NoFormat)
                {
                    line = FormatRunInstruction(line);
                }

                line = $"RUN {line}";
            }
        }

        return line;
    }

    internal static int FindHistoryMetadataIndex(string line)
    {
        if (DockerfileInstructionPrefixRegex().IsMatch(line))
        {
            return -1;
        }

        const string Marker = "#(nop)";
        bool atTokenStart = true;
        char quote = '\0';
        for (int i = 0; i < line.Length; i++)
        {
            char character = line[i];
            if (character is '\r' or '\n')
            {
                return -1;
            }
            if (quote != '\0')
            {
                if (quote == '"' && character == '\\' && i + 1 < line.Length)
                {
                    i++;
                }
                else if (character == quote)
                {
                    quote = '\0';
                }
                continue;
            }
            if (character is ' ' or '\t')
            {
                atTokenStart = true;
                continue;
            }
            if (atTokenStart)
            {
                if (character == '#')
                {
                    return line.AsSpan(i).StartsWith(Marker, StringComparison.Ordinal) &&
                        i + Marker.Length < line.Length &&
                        line[i + Marker.Length] is ' ' or '\t'
                        ? i
                        : -1;
                }
                // Flattened history can contain literal punctuation within executable paths.
                if (character is '\'' or '"')
                {
                    quote = character;
                }
            }
            atTokenStart = false;
        }
        return -1;
    }

    private static string FormatRunInstruction(string line)
    {
        Match powerShellCommand = QuotedPowerShellCommandRegex().Match(line);
        if (powerShellCommand.Success)
        {
            return FormatPowerShellCommand(line, powerShellCommand);
        }

        // These constructs need shell-specific interpretation beyond balanced delimiters.
        if (line.Contains("$'", StringComparison.Ordinal) ||
            line.Contains("$\"", StringComparison.Ordinal) ||
            line.Contains("<<", StringComparison.Ordinal) ||
            line.IndexOfAny(['`', '\r', '\n']) >= 0)
        {
            return line;
        }

        StringBuilder formattedLine = new();
        int segmentStart = 0;
        for (int i = 0; i < line.Length; i++)
        {
            char character = line[i];
            if (character == '^')
            {
                return line;
            }

            bool expansion = character == '$' && i + 1 < line.Length && line[i + 1] is '(' or '{';
            if (!expansion && character is not ('\'' or '"' or '\\' or '#'))
            {
                continue;
            }

            formattedLine.Append(FormatUnquotedRunSegment(line[segmentStart..i]));
            int protectedStart = i;
            if (character == '#')
            {
                formattedLine.Append(line[i..]);
                return formattedLine.ToString();
            }
            if (character == '\\')
            {
                if (++i >= line.Length)
                {
                    return line;
                }
            }
            else if (!TryFindProtectedRunTextEnd(line, i, out i))
            {
                return line;
            }

            formattedLine.Append(line[protectedStart..(i + 1)]);
            segmentStart = i + 1;
        }
        formattedLine.Append(FormatUnquotedRunSegment(line[segmentStart..]));
        return formattedLine.ToString();
    }

    private static string FormatPowerShellCommand(string line, Match command)
    {
        if (line.IndexOfAny(['\u2018', '\u2019', '\u201c', '\u201d']) >= 0)
        {
            return line;
        }

        Group script = command.Groups["script"];
        StringBuilder formatted = new(line[..script.Index]);
        bool inString = false;
        for (int i = script.Index; i < script.Index + script.Length; i++)
        {
            char character = line[i];
            if (character == '\'')
            {
                if (inString && i + 1 < script.Index + script.Length && line[i + 1] == '\'')
                {
                    formatted.Append("''");
                    i++;
                    continue;
                }
                inString = !inString;
            }
            if (inString)
            {
                formatted.Append(character);
                continue;
            }
            if (character is '`' or '#' ||
                line.AsSpan(i).StartsWith("${", StringComparison.Ordinal) ||
                line.AsSpan(i).StartsWith("@'", StringComparison.Ordinal) ||
                line.AsSpan(i).StartsWith("--%", StringComparison.Ordinal))
            {
                return line;
            }

            if (character is ' ' or '\t')
            {
                int start = i;
                while (i + 1 < script.Index + script.Length && line[i + 1] is ' ' or '\t')
                {
                    i++;
                }
                if (i > start && start > script.Index && line[start - 1] is ';' or '{')
                {
                    formatted.Append($" \\{Environment.NewLine}    ");
                }
                else
                {
                    formatted.Append(' ');
                }
            }
            else
            {
                formatted.Append(character);
            }
        }

        return inString ? line : formatted.Append(line[(script.Index + script.Length)..]).ToString();
    }

    private static bool TryFindProtectedRunTextEnd(string line, int start, out int end)
    {
        Stack<char> delimiters = new();
        int i = start;
        if (line[i] == '$')
        {
            delimiters.Push(line[++i] == '(' ? ')' : '}');
        }
        else
        {
            delimiters.Push(line[i]);
        }

        while (++i < line.Length)
        {
            char character = line[i];
            char delimiter = delimiters.Peek();
            if (delimiter == ')' &&
                line.AsSpan(i).StartsWith("case", StringComparison.Ordinal) &&
                i + 4 < line.Length && line[i + 4] is ' ' or '\t' &&
                !char.IsAsciiLetterOrDigit(line[i - 1]) && line[i - 1] != '_')
            {
                break;
            }
            if (character == '\\' && delimiter != '\'')
            {
                i++;
                continue;
            }
            if (character == delimiter)
            {
                delimiters.Pop();
                if (delimiters.Count == 0)
                {
                    end = i;
                    return true;
                }
                continue;
            }
            if (delimiter == '\'')
            {
                continue;
            }
            if (character == '$' && i + 1 < line.Length && line[i + 1] is '(' or '{')
            {
                delimiters.Push(line[++i] == '(' ? ')' : '}');
            }
            else if (delimiter != '"')
            {
                if (character is '\'' or '"')
                {
                    delimiters.Push(character);
                }
                else if (character is '(' or '{')
                {
                    delimiters.Push(character == '(' ? ')' : '}');
                }
                else if (character == '#' && delimiter == ')')
                {
                    break;
                }
            }
        }

        end = start;
        return false;
    }

    private static string FormatUnquotedRunSegment(string line)
    {
        // Reformat to support multi-line patterns: 
        // 1. '&&' before a new line
        line = AndBeforeNewLineRegex().Replace(line, $" && \\{Environment.NewLine}    ");
        // 2. '&&' after a new line
        line = AndAfterNewLineRegex().Replace(line, $" \\{Environment.NewLine}    && ");
        // 3. ';' before a new line
        line = SemiColonBeforeNewLineRegex().Replace(line, $"; \\{Environment.NewLine}    ");
        // 4. '{' before a new line
        line = OpenParenBeforeNewLineRegex().Replace(line, $"{{ \\{Environment.NewLine}    ");
        // 5. '{' after a new line
        line = CloseParenAfterNewLineRegex().Replace(line, $" \\{Environment.NewLine}    {{ ");

        int leadingWhitespace = 0;
        while (leadingWhitespace < line.Length && line[leadingWhitespace] is ' ' or '\t')
        {
            leadingWhitespace++;
        }
        if (leadingWhitespace > 1)
        {
            line = " " + line[leadingWhitespace..];
        }

        // Trim extra spaces from commands that were originally multiline but represented in layer history
        // without newlines, leading to a bunch of spaces.
        line = FormatLineToRemoveWhitespace(line);
        return line;
    }

    private static string FormatEnvInstruction(string line, EnvInstruction envInstruction)
    {
        if (envInstruction.Variables.Count > 1)
        {
            StringBuilder envBuilder = new();
            envBuilder.Append("ENV ");
            List<string> vars = [.. envInstruction.Variables.Select(static variable => $"{variable.Key}={variable.Value} \\")];
            for (int i = 0; i < envInstruction.Variables.Count; i++)
            {
                IKeyValuePair variable = envInstruction.Variables[i];
                if (i != 0)
                {
                    envBuilder.Append("    ");
                }
                envBuilder.Append($"{variable.Key}={variable.Value}");
                if (i + 1 != envInstruction.Variables.Count)
                {
                    envBuilder.AppendLine(" \\");
                }
            }

            line = envBuilder.ToString();
        }

        return line;
    }

    private static string FormatLineToRemoveWhitespace(string line)
    {
        Regex multiWhitespaceRegex =
            TwoOrMoreWhitespaceRegex();
        MatchCollection matches = multiWhitespaceRegex.Matches(line);
        if (matches.Count == 0)
        {
            return line;
        }

        StringBuilder formattedLine = new();
        int currentIndex = 0;
        foreach (Group group in matches.Select(static match => match.Groups["whitespace"]))
        {
            formattedLine.Append(line[currentIndex..group.Index]);
            formattedLine.Append(' ');
            currentIndex = group.Index + group.Length;
        }

        if (currentIndex < line.Length)
        {
            formattedLine.Append(line[currentIndex..]);
        }

        return formattedLine.ToString();
    }

    private string GetTokenMarkup(Token token, bool inRunInstruction)
    {
        string tokenStr = Markup.Escape(token.ToString());
        Color? tokenColor = null;
        if (!Options.NoColor)
        {
            if (token is KeywordToken)
            {
                tokenColor = KeywordColor;
            }
            else if (token is CommentToken)
            {
                tokenColor = CommentColor;
            }
            else if (token is LiteralToken)
            {
                if (tokenStr.StartsWith('\'') && tokenStr.EndsWith('\'') ||
                    tokenStr.StartsWith('\"') && tokenStr.EndsWith('\"'))
                {
                    tokenColor = StringColor;
                }
                else
                {
                    tokenColor = LiteralColor;
                }
            }
            else if (token is IdentifierToken)
            {
                tokenColor = IdentifierColor;
            }
            else if (token is SymbolToken)
            {
                tokenColor = SymbolColor;
            }
            else if (token is AggregateToken aggregateToken)
            {
                StringBuilder builder = new();
                foreach (Token childToken in aggregateToken.Tokens)
                {
                    builder.Append(GetTokenMarkup(childToken, inRunInstruction));
                }
                return builder.ToString();
            }
        }

        if (tokenColor is not null)
        {
            if (inRunInstruction)
            {
                string lineContinuation = $"\\{Environment.NewLine}";
                tokenStr = tokenStr.Replace(lineContinuation, $"[{SymbolColor.ToMarkup()}]\\[/]{Environment.NewLine}");
            }

            return $"[{tokenColor.Value.ToMarkup()}]{tokenStr}[/]";
        }

        else
        {
            return tokenStr;
        }
    }

    [GeneratedRegex(@"[ \t]+&&[ \t]{2,}")]
    private static partial Regex AndBeforeNewLineRegex();

    [GeneratedRegex(@"[ \t]{2,}&&[ \t]+")]
    private static partial Regex AndAfterNewLineRegex();
    
    [GeneratedRegex(@";[ \t]{2,}")]
    private static partial Regex SemiColonBeforeNewLineRegex();
    
    [GeneratedRegex(@"[ \t]+\{[ \t]{2,}")]
    private static partial Regex OpenParenBeforeNewLineRegex();
    
    [GeneratedRegex(@"[ \t]{2,}\{[ \t]+")]
    private static partial Regex CloseParenAfterNewLineRegex();
    [GeneratedRegex(@"\S+(?<whitespace>[ \t]{2,})", RegexOptions.Multiline)]
    private static partial Regex TwoOrMoreWhitespaceRegex();

    [GeneratedRegex(@"\A[ \t]*(?:ADD|ARG|CMD(?![ \t]+/[A-Za-z](?::[^ \t\r\n]+)?(?:[ \t]|$))|COPY|ENTRYPOINT|ENV|EXPOSE|FROM|HEALTHCHECK|LABEL|MAINTAINER|ONBUILD|RUN|SHELL|STOPSIGNAL|USER|VOLUME|WORKDIR)[ \t]", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DockerfileInstructionPrefixRegex();

    [GeneratedRegex("\\A(?:powershell|pwsh)(?:\\.exe)?[ \\t]+(?:(?:-NoLogo|-NoProfile|-NonInteractive)[ \\t]+)*-Command[ \\t]+\"(?<script>[^\"\\r\\n]*)\"[ \\t]*\\z", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex QuotedPowerShellCommandRegex();
}
