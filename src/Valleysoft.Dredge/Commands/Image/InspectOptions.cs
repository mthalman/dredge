using System.CommandLine;

namespace Valleysoft.Dredge.Commands.Image;

public class InspectOptions : PlatformOptionsBase
{
    private readonly Argument<string> imageArg;

    public string Image { get; set; } = string.Empty;

    public InspectOptions()
    {
        imageArg = Add(ImageReferenceArgument.Create("image"));
    }

    protected override void GetValues()
    {
        base.GetValues();
        Image = GetValue(imageArg);
    }
}
