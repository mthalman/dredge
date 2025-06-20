using System.CommandLine;

namespace Valleysoft.Dredge.Commands.Image;

public class InspectOptions : PlatformOptionsBase
{
    private readonly Argument<string> imageArg;

    public string Image { get; set; } = string.Empty;

    public InspectOptions()
    {
        imageArg = Add(new Argument<string>("image") { Description = "Name of the container image (<image>, <image>:<tag>, or <image>@<digest>)" });
    }

    protected override void GetValues()
    {
        base.GetValues();
        Image = GetValue(imageArg);
    }
}
