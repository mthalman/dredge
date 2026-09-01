using System.CommandLine;

namespace Valleysoft.Dredge.Commands.Image;

public class CompareMetadataOptions : CompareOptionsBase
{
    private readonly Option<CompareOutput> outputOption;
    private readonly Option<bool> noColorOption;

    public CompareOutput OutputFormat { get; set; }
    public bool IsColorDisabled { get; set; }

    public CompareMetadataOptions()
    {
        outputOption = Add(new Option<CompareOutput>("--output")
        {
            Description = "Output format",
            DefaultValueFactory = _ => CompareOutput.SideBySide
        });
        noColorOption = Add(new Option<bool>("--no-color")
        {
            Description = "Disables dependency on color in comparison results"
        });
    }

    protected override void GetValues()
    {
        base.GetValues();
        OutputFormat = GetValue(outputOption);
        IsColorDisabled = GetValue(noColorOption);
    }
}
