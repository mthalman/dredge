using System.CommandLine;

namespace Valleysoft.Dredge.Commands.Manifest;

public class GetOptions : OptionsBase
{
    private readonly Argument<string> imageArg;

    public string Image { get; set; } = string.Empty;

    public GetOptions()
    {
        imageArg = Add(new Argument<string>("image") { Description = "Container image reference (<image>, <image>:<tag>, or <image>@<digest>)" });
    }

    protected override void GetValues()
    {
        Image = GetValue(imageArg);
    }
}
