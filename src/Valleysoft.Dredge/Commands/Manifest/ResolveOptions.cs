using System.CommandLine;

namespace Valleysoft.Dredge.Commands.Manifest;

public class SetOptions : PlatformOptionsBase
{
    private readonly Argument<string> imageArg;

    public string Image { get; set; } = string.Empty;

    public SetOptions()
    {
        imageArg = Add(ImageReferenceArgument.Create("image"));
    }

    protected override void GetValues()
    {
        base.GetValues();
        Image = GetValue(imageArg);
    }
}
