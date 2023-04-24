using Spectre.Console;
using System.Text;
using System.Text.RegularExpressions;
using Valleysoft.DockerfileModel;
using Valleysoft.DockerfileModel.Tokens;
using Valleysoft.DockerRegistryClient;
using Valleysoft.DockerRegistryClient.Models;
using ImageConfig = Valleysoft.DockerRegistryClient.Models.Image;

namespace Valleysoft.Dredge.Commands.Image;

public class DockerfileCommand : RegistryCommandBase<DockerfileOptions>
{
    private static readonly Color SymbolColor = new(250, 200, 31); // yellow
    private static readonly Color StringColor = new(202, 145, 120); // tan
    private static readonly Color KeywordColor = new(194, 133, 191); // lavender
    private static readonly Color CommentColor = new(109, 154, 88); // green-ish
    private static readonly Color LiteralColor = new(150, 220, 254); // light turquoise
    private static readonly Color IdentifierColor = Color.Green;
    private static readonly string[] KeyValuePairInstructions = new string[]
    {
        "ARG",
        "ENV",
        "LABEL"
    };

    private readonly IDockerRegistryClientFactory dockerRegistryClientFactory;

    public DockerfileCommand(IDockerRegistryClientFactory dockerRegistryClientFactory)
        : base("dockerfile", "Generates a Dockerfile that represents the image", dockerRegistryClientFactory)
    {
        this.dockerRegistryClientFactory = dockerRegistryClientFactory;
    }

    protected override async Task ExecuteAsync()
    {
        ImageName imageName = ImageName.Parse(Options.Image);
        await CommandHelper.ExecuteCommandAsync(imageName.Registry, async () =>
        {
            Markup markupOutput = new(await GetMarkupStringAsync());
            AnsiConsole.Write(markupOutput);
        });
    }

    public async Task<string> GetMarkupStringAsync()
    {
        ImageName imageName = ImageName.Parse(Options.Image);
        using IDockerRegistryClient client = await dockerRegistryClientFactory.GetClientAsync(imageName.Registry);
        DockerManifestV2 manifest = (await ManifestHelper.GetResolvedManifestAsync(client, imageName, Options)).Manifest;

        string? digest = manifest.Config?.Digest;
        if (digest is null)
        {
            throw new NotSupportedException($"Could not resolve the image config digest of '{Options.Image}'.");
        }

        ImageConfig imageConfig = await client.Blobs.GetImageAsync(imageName.Repo, digest);
        bool isWindows = imageConfig.Os.Equals("windows", StringComparison.OrdinalIgnoreCase);

        StringBuilder dockerfileBuilder = new();
        IEnumerable<LayerHistory> layers;

        if (isWindows)
        {
            (WindowsOsInfo windowsOsInfo, string repo) = await GetWindowsInfoAsync(manifest, imageConfig);
            layers = await GetWindowsLayersAsync(imageConfig, manifest, repo);
            dockerfileBuilder.AppendLine($"FROM {RegistryHelper.McrRegistry}/{repo}:{windowsOsInfo.Version}-{imageConfig.Architecture}");
        }
        else
        {
            layers = imageConfig.History;
            dockerfileBuilder.AppendLine("FROM scratch");
        }

        string? currentShell = null;

        foreach (LayerHistory layerHistory in layers)
        {
            if (string.IsNullOrEmpty(layerHistory.CreatedBy))
            {
                dockerfileBuilder.AppendLine("# No instruction info");
                continue;
            }

            string line = GetHistoryLine(layerHistory.CreatedBy, ref currentShell);
            dockerfileBuilder.AppendLine(line);
        }

        StringBuilder markup = new();
        Dockerfile fullDockerfile = Dockerfile.Parse(dockerfileBuilder.ToString());

        foreach (DockerfileConstruct construct in fullDockerfile.Items)
        {
            foreach (Token token in construct.Tokens)
            {
                markup.Append(GetTokenMarkup(token, construct is RunInstruction));
            }
        }

        return markup.ToString();
    }

    private async Task<IEnumerable<LayerHistory>> GetWindowsLayersAsync(ImageConfig imageConfig, DockerManifestV2 manifest, string windowsRepo)
    {
        using IDockerRegistryClient mcrClient =
            await dockerRegistryClientFactory.GetClientAsync(RegistryHelper.McrRegistry);

        // For Windows images, the layers that we want to generate a Dockerfile from start after the initial set of base
        // Windows layers. There is no "FROM scratch" possible with Windows images.
        int i;
        for (i = 0; i < manifest.Layers.Length; i++)
        {
            string? layerDigest = manifest.Layers[i].Digest;
            if (string.IsNullOrEmpty(layerDigest))
            {
                throw new Exception($"No digest information defined for layer index {i} of the Windows image.");
            }
            bool exists = await mcrClient.Blobs.ExistsAsync(windowsRepo, layerDigest);
            if (!exists)
            {
                break;
            }
        }

        return imageConfig.History.Skip(i);
    }

    private async Task<(WindowsOsInfo Info, string Repo)> GetWindowsInfoAsync(DockerManifestV2 manifest, ImageConfig imageConfig)
    {
        string? initialLayerDigest = manifest.Layers.First().Digest;
        if (string.IsNullOrEmpty(initialLayerDigest))
        {
            throw new Exception("No digest information defined for the initial layer of the Windows image.");
        }
        var windowsOsInfo = await OsCommand.GetWindowsOsInfoAsync(
            imageConfig, initialLayerDigest, dockerRegistryClientFactory);
        if (windowsOsInfo is null)
        {
            throw new Exception("Could not determine info about the Windows image.");
        }
        if (string.IsNullOrEmpty(windowsOsInfo.Value.Info.Version))
        {
            throw new Exception("No os.version information defined for the Windows image.");
        }

        return (windowsOsInfo.Value.Info, windowsOsInfo.Value.Repo);
    }

    private string GetHistoryLine(string line, ref string? currentShell)
    {
        const string NopMarker = "#(nop)";

        int nopIndex = line.IndexOf(NopMarker);
        if (nopIndex >= 0)
        {
            currentShell ??= line[0..nopIndex].Trim();

            line = line[(nopIndex + NopMarker.Length)..].Trim();
            if (line.StartsWith("ADD", StringComparison.OrdinalIgnoreCase) ||
                line.StartsWith("COPY", StringComparison.OrdinalIgnoreCase))
            {
                // The history of ADD and COPY instructions includes this "in" keyword that isn't valid in a Dockerfile.
                // e.g. ADD file:c13b430c8699df107ffd9ea5230b92238bc037a8e1cbbe35d6ab664941d575da in /
                // This trims out the first occurrence of it.
                const string InMarker = " in ";
                int inIndex = line.IndexOf(InMarker);
                if (inIndex >= 0)
                {
                    line = $"{line[0..inIndex]}{line[(inIndex + InMarker.Length - 1)..]}";
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
                    int cmdIndex = line.IndexOf("[");
                    string command = line[cmdIndex..]
                        .Replace(" ", "\", \"");
                    line = line[0..cmdIndex] + command;
                }
            }

            Dockerfile? dockerfile = null;
            try
            {
                dockerfile = Dockerfile.Parse(line);
            }
            catch (Exception)
            {
            }

            if (dockerfile is not null)
            {
                DockerfileConstruct dockerfileConstruct = dockerfile.Items.First();
                if (dockerfileConstruct is ShellInstruction shellInstruction)
                {
                    // Track the current SHELL. This is needed in order to trim it from subsequent instructions
                    currentShell = string.Join(" ", ((ExecFormCommand)shellInstruction.Command).Values.ToArray());
                }
                else if (dockerfileConstruct is EnvInstruction envInstruction)
                {
                    if (!Options.NoFormat)
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

        if (KeyValuePairInstructions.Any(instruction => line.StartsWith(instruction, StringComparison.OrdinalIgnoreCase)))
        {
            // Ensure that key-value pair instructions have their values surrounded in quotes to account for any spaces
            line = Regex.Replace(line, "(^\\S+\\s+[A-Za-z0-9]*(\\s|=)+)(\\S*\\s.*)", "$1\"$3\"");
        }

        return line;
    }

    private static string FormatRunInstruction(string line)
    {
        // Reformat to support multi-line patterns: 
        // 1. '&&' before a new line
        line = Regex.Replace(line, @"[ \t]+&&[ \t]{2,}", $" && \\{Environment.NewLine}    ");
        // 2. '&&' after a new line
        line = Regex.Replace(line, @"[ \t]{2,}&&[ \t]+", $" \\{Environment.NewLine}    && ");
        // 3. ';' before a new line
        line = Regex.Replace(line, @";[ \t]{2,}", $"; \\{Environment.NewLine}    ");
        // 4. '{' before a new line
        line = Regex.Replace(line, @"[ \t]+\{[ \t]{2,}", $"{{ \\{Environment.NewLine}    ");
        // 5. '{' after a new line
        line = Regex.Replace(line, @"[ \t]{2,}\{[ \t]+", $" \\{Environment.NewLine}    {{ ");

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
            List<string> vars = envInstruction.Variables
                .Select(variable => $"{variable.Key}={variable.Value} \\")
                .ToList();
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
        // A regex that captures whitespace of length 2 or more that's not contained in quotes.
        Regex nonQuotedMultiWhitespaceRegex =
            new(@"\S+(?<whitespace>[ \t]{2,})((?=(?<singlequoted>[^'""]*'[^']*')*[^'""]*$)|(?=(?<doublequoted>[^""']*""[^""]*"")*[^""']*$))",
            RegexOptions.Multiline);
        MatchCollection matches = nonQuotedMultiWhitespaceRegex.Matches(line);
        if (!matches.Any())
        {
            return line;
        }

        StringBuilder formattedLine = new();
        int currentIndex = 0;
        foreach (Group group in matches.Select(match => match.Groups["whitespace"]))
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
}
