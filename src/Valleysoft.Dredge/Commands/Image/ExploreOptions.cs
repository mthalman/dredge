using System.CommandLine;

namespace Valleysoft.Dredge.Commands.Image;

public sealed class ExploreOptions : PlatformOptionsBase
{
    private readonly Argument<string> imageArgument;
    private readonly Option<int?> layerOption;
    private readonly Option<string?> compareOption;
    private readonly Option<string?> baseImageOption;
    private readonly Option<bool> noMouseOption;

    public string Image { get; private set; } = string.Empty;
    public int? Layer { get; private set; }
    public string? Compare { get; private set; }
    public string? BaseImage { get; private set; }
    public bool NoMouse { get; private set; }

    public ExploreOptions()
    {
        imageArgument = Add(ImageReferenceArgument.Create("image"));
        layerOption = Add(new Option<int?>("--layer") { Description = "Initially selected zero-based layer" });
        compareOption = Add(new Option<string?>("--compare") { Description = "Image or tag to compare against" });
        baseImageOption = Add(new Option<string?>("--base-image") { Description = "Explicit base image reference" });
        noMouseOption = Add(new Option<bool>("--no-mouse") { Description = "Leave mouse selection to the terminal" });
    }

    protected override void GetValues()
    {
        base.GetValues();
        Image = GetValue(imageArgument);
        Layer = GetValue(layerOption);
        Compare = GetValue(compareOption);
        BaseImage = GetValue(baseImageOption);
        NoMouse = GetValue(noMouseOption);
    }
}
