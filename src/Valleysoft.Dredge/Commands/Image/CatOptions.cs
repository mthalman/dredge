using System.CommandLine;

namespace Valleysoft.Dredge.Commands.Image;

public class CatOptions : PlatformOptionsBase
{
    private readonly Argument<string> imageArgument;
    private readonly Argument<string> pathArgument;
    private readonly Option<int?> layerOption;

    public string Image { get; set; } = string.Empty;
    public string Path { get; set; } = string.Empty;
    public int? Layer { get; set; }

    public CatOptions()
    {
        imageArgument = Add(ImageReferenceArgument.Create("image"));
        pathArgument = Add(new Argument<string>("path")
        {
            Description = "Image file path to write to standard output"
        });
        layerOption = Add(LayerIndexOption.Create(
            "--layer",
            "index of the image layer to inspect"));
    }

    protected override void GetValues()
    {
        base.GetValues();
        Image = GetValue(imageArgument);
        Path = GetValue(pathArgument);
        Layer = GetValue(layerOption);
    }
}
