using System.CommandLine;

namespace Valleysoft.Dredge.Commands.Image;

public class CompareLayersOptions : CompareOptionsBase
{
    private readonly CliOutputOption<CompareOutput> outputOption;
    private readonly Option<bool> noColorOption;
    private readonly Option<bool> historyOption;
    private readonly Option<bool> compressedSizeOption;

    public CompareOutput OutputFormat { get; set; }
    public bool IsColorDisabled { get; set; }
    public bool IncludeHistory { get; set; }
    public bool IncludeCompressedSize { get; set; }

    public CompareLayersOptions()
    {
        outputOption = new CliOutputOption<CompareOutput>(
            "Output format",
            CompareOutput.SideBySide,
            ("side-by-side", CompareOutput.SideBySide),
            ("inline", CompareOutput.Inline),
            ("json", CompareOutput.Json));
        Add(outputOption.Option);
        noColorOption = Add(new Option<bool>("--no-color") { Description = "Disables dependency on color in comparison results" });
        historyOption = Add(new Option<bool>("--history") { Description = "Include layer history as part of the comparison" });
        compressedSizeOption = Add(new Option<bool>("--compressed-size") { Description = "Show the compressed size of the layer" });
    }

    protected override void GetValues()
    {
        base.GetValues();
        OutputFormat = outputOption.GetValue(GetValue(outputOption.Option));
        IsColorDisabled = GetValue(noColorOption);
        IncludeHistory = GetValue(historyOption);
        IncludeCompressedSize = GetValue(compressedSizeOption);
    }
}
