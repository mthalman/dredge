using System.CommandLine;

namespace Valleysoft.Dredge.Commands.Manifest;

public class SetOptions : PlatformOptionsBase
{
    private readonly Argument<string> imageArg;

    public string Image { get; set; } = string.Empty;

    public SetOptions()
    {
        imageArg = Add(new Argument<string>("image", "Name of the container image (<image>, <image>:<tag>, or <image>@<digest>)"));
    }

    protected override void GetValues()
    {
        base.GetValues();
        Image = GetValue(imageArg);
    }
}
