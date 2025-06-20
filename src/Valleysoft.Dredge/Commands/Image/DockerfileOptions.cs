using System.CommandLine;

namespace Valleysoft.Dredge.Commands.Image;

public class DockerfileOptions : PlatformOptionsBase
{
    private readonly Argument<string> imageArg;
    private readonly Option<bool> noColorOption;
    private readonly Option<bool> noFormatOption;

    public string Image { get; set; } = string.Empty;
    public bool NoColor { get; set; }
    public bool NoFormat { get; set; }

    public DockerfileOptions()
    {
        imageArg = Add(new Argument<string>("image") { Description = "Name of the container image (<image>, <image>:<tag>, or <image>@<digest>)" });
        noColorOption = Add(new Option<bool>("--no-color") { Description = "Disables use of syntax color in the output" });
        noFormatOption = Add(new Option<bool>("--no-format") { Description = "Disables use of heuristics to format the layer history for better readability" });
    }

    protected override void GetValues()
    {
        base.GetValues();
        Image = GetValue(imageArg);
        NoColor = GetValue(noColorOption);
        NoFormat = GetValue(noFormatOption);
    }
}
