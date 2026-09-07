using System.CommandLine;

namespace Valleysoft.Dredge.Commands.Manifest;

public class DigestOptions : OptionsBase
{
    private readonly Argument<string> imageArg;

    public string Image { get; set; } = string.Empty;

    public DigestOptions()
    {
        imageArg = Add(ImageReferenceArgument.Create("image"));
    }

    protected override void GetValues()
    {
        Image = GetValue(imageArg);
    }
}
