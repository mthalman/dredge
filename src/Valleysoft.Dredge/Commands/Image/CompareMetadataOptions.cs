using System.CommandLine;

namespace Valleysoft.Dredge.Commands.Image;

public class CompareMetadataOptions : CompareOptionsBase
{
    private readonly CliOutputOption<CompareOutput> outputOption;
    private readonly Option<bool> noColorOption;

    public CompareOutput OutputFormat { get; set; }
    public bool IsColorDisabled { get; set; }

    public CompareMetadataOptions()
    {
        outputOption = new CliOutputOption<CompareOutput>(
            "Output format",
            CompareOutput.SideBySide,
            ("side-by-side", CompareOutput.SideBySide),
            ("inline", CompareOutput.Inline),
            ("json", CompareOutput.Json));
        Add(outputOption.Option);
        noColorOption = Add(new Option<bool>("--no-color")
        {
            Description = "Disables dependency on color in comparison results"
        });
    }

    protected override void GetValues()
    {
        base.GetValues();
        OutputFormat = outputOption.GetValue(GetValue(outputOption.Option));
        IsColorDisabled = GetValue(noColorOption);
    }
}
