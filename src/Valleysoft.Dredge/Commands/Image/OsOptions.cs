using System.CommandLine;

namespace Valleysoft.Dredge.Commands.Image;

public class OsOptions : ImageOptionsBase
{
    private readonly Argument<string> imageArg;

    public string Image { get; set; } = string.Empty;

    public OsOptions()
    {
        imageArg = Add(ImageReferenceArgument.CreateImageSource("image"));
    }

    protected override void GetValues()
    {
        base.GetValues();
        Image = GetValue(imageArg);
    }
}
