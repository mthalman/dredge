using System.CommandLine;

namespace Valleysoft.Dredge.Commands.Manifest;

public class DigestOptions : OptionsBase
{
    private readonly Argument<string> imageArg;

    public string Image { get; set; } = string.Empty;

    public DigestOptions()
    {
        imageArg = Add(new Argument<string>("name", "Name of the manifest (<name>, <name>:<tag>, or <name>@<digest>)"));
    }

    protected override void GetValues()
    {
        Image = GetValue(imageArg);
    }
}
