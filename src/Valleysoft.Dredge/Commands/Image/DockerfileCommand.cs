using Spectre.Console;
using System.Text;
using Valleysoft.DockerfileModel;
using Valleysoft.DockerfileModel.Tokens;
using Valleysoft.Dredge.Core;

namespace Valleysoft.Dredge.Commands.Image;

public class DockerfileCommand : RegistryCommandBase<DockerfileOptions>
{
    private static readonly Color SymbolColor = new(250, 200, 31); // yellow
    private static readonly Color StringColor = new(202, 145, 120); // tan
    private static readonly Color KeywordColor = new(194, 133, 191); // lavender
    private static readonly Color CommentColor = new(109, 154, 88); // green-ish
    private static readonly Color LiteralColor = new(150, 220, 254); // light turquoise
    private static readonly Color IdentifierColor = Color.Green;

    private readonly AppSettings appSettings;

    public DockerfileCommand(IDockerRegistryClientFactory dockerRegistryClientFactory)
        : base("dockerfile", "Generates a Dockerfile that represents the image", dockerRegistryClientFactory)
    {
        this.appSettings = AppSettingsHelper.Load();
    }

    protected override async Task ExecuteAsync()
    {
        Core.ImageName imageName = Core.ImageName.Parse(Options.Image);
        await CommandHelper.ExecuteCommandAsync(imageName.Registry, async () =>
        {
            Markup markupOutput = new(await GetMarkupStringAsync());
            AnsiConsole.Write(markupOutput);
        });
    }

    public async Task<string> GetMarkupStringAsync()
    {
        Core.DockerfileOptions dockerfileOptions = new()
        {
            NoFormat = Options.NoFormat,
            PlatformOptions = Options.ToPlatformOptions()
        };
        string dockerfile = await DockerfileGenerator.GenerateAsync(Options.Image, DockerRegistryClientFactory, appSettings, dockerfileOptions);
        StringBuilder markup = new();
        Dockerfile fullDockerfile = Dockerfile.Parse(dockerfile);

        foreach (DockerfileConstruct construct in fullDockerfile.Items)
        {
            foreach (Token token in construct.Tokens)
            {
                markup.Append(GetTokenMarkup(token, construct is RunInstruction));
            }
        }

        return markup.ToString();
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
