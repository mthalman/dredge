using System.CommandLine;

namespace Valleysoft.Dredge.Commands.Image;

public class ExtractOptions : PlatformOptionsBase
{
    private readonly Argument<string> imageArgument;
    private readonly Argument<string> pathArgument;
    private readonly Argument<string> outputPathArgument;

    public string Image { get; set; } = string.Empty;
    public string Path { get; set; } = string.Empty;
    public string OutputPath { get; set; } = string.Empty;

    public ExtractOptions()
    {
        imageArgument = Add(new Argument<string>("image")
        {
            Description = "Name of the container image (<image>, <image>:<tag>, or <image>@<digest>)"
        });
        pathArgument = Add(new Argument<string>("path")
        {
            Description = "Image file or directory path to extract"
        });
        outputPathArgument = Add(new Argument<string>("output-path")
        {
            Description = "New destination path"
        });
    }

    protected override void GetValues()
    {
        base.GetValues();
        Image = GetValue(imageArgument);
        Path = GetValue(pathArgument);
        OutputPath = GetValue(outputPathArgument);
    }
}
