using System.CommandLine;

namespace Valleysoft.Dredge.Commands.Image;

public class CatOptions : PlatformOptionsBase
{
    private readonly Argument<string> imageArgument;
    private readonly Argument<string> pathArgument;

    public string Image { get; set; } = string.Empty;
    public string Path { get; set; } = string.Empty;

    public CatOptions()
    {
        imageArgument = Add(new Argument<string>("image")
        {
            Description = "Name of the container image (<image>, <image>:<tag>, or <image>@<digest>)"
        });
        pathArgument = Add(new Argument<string>("path")
        {
            Description = "Image file path to write to standard output"
        });
    }

    protected override void GetValues()
    {
        base.GetValues();
        Image = GetValue(imageArgument);
        Path = GetValue(pathArgument);
    }
}
