using System.CommandLine;

namespace Valleysoft.Dredge.Commands.Image;

public class InspectOptions : OptionsBase
{
    private readonly Argument<string> imageArg;

    public string Image { get; set; } = string.Empty;

    public InspectOptions()
    {
        imageArg = Add(new Argument<string>("image", "Name of the container image (<image>, <image>:<tag>, or <image>@<digest>)"));
    }

    protected override void GetValues()
    {
        Image = GetValue(imageArg);
    }
}
