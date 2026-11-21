using System.CommandLine;

namespace Valleysoft.Dredge.Commands.Image;

public enum ExploreOutput
{
    Tui,
    Html
}

public sealed class ExploreOptions : ImageOptionsBase
{
    private readonly Argument<string> imageArgument;
    private readonly Option<int?> layerOption;
    private readonly Option<string?> compareOption;
    private readonly Option<string[]> baseImageOption;
    private readonly Option<bool> noMouseOption;
    private readonly CliOutputOption<ExploreOutput> outputOption;

    public string Image { get; private set; } = string.Empty;
    public int? Layer { get; private set; }
    public string? Compare { get; private set; }
    public string[] BaseImages { get; private set; } = [];
    public bool NoMouse { get; private set; }
    public ExploreOutput OutputFormat { get; private set; }

    public ExploreOptions()
    {
        imageArgument = Add(ImageReferenceArgument.CreateImageSource("image"));
        layerOption = Add(new Option<int?>("--layer") { Description = "Initially selected zero-based layer" });
        compareOption = Add(new Option<string?>("--compare") { Description = "Image or tag to compare against" });
        baseImageOption = Add(new Option<string[]>("--base-image")
        {
            Description = "Base image reference; repeat for each ancestor (any order)",
            AllowMultipleArgumentsPerToken = false
        });
        noMouseOption = Add(new Option<bool>("--no-mouse") { Description = "Leave mouse selection to the terminal" });
        outputOption = new CliOutputOption<ExploreOutput>(
            "Output format",
            ExploreOutput.Tui,
            ("tui", ExploreOutput.Tui),
            ("html", ExploreOutput.Html));
        Add(outputOption.Option);
    }

    protected override void GetValues()
    {
        base.GetValues();
        Image = GetValue(imageArgument);
        Layer = GetValue(layerOption);
        Compare = GetValue(compareOption);
        BaseImages = GetValue(baseImageOption) ?? [];
        NoMouse = GetValue(noMouseOption);
        OutputFormat = outputOption.GetValue(GetValue(outputOption.Option));
    }
}
