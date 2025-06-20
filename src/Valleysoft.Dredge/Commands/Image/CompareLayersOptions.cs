using System.CommandLine;

namespace Valleysoft.Dredge.Commands.Image;

public class CompareLayersOptions : CompareOptionsBase
{
    private readonly Option<CompareLayersOutput> outputOption;
    private readonly Option<bool> noColorOption;
    private readonly Option<bool> historyOption;
    private readonly Option<bool> compressedSizeOption;

    public CompareLayersOutput OutputFormat { get; set; }
    public bool IsColorDisabled { get; set; }
    public bool IncludeHistory { get; set; }
    public bool IncludeCompressedSize { get; set; }

    public CompareLayersOptions()
    {
        outputOption = Add(new Option<CompareLayersOutput>("--output") { Description = "Output format", DefaultValueFactory = _ => CompareLayersOutput.SideBySide });
        noColorOption = Add(new Option<bool>("--no-color") { Description = "Disables dependency on color in comparison results" });
        historyOption = Add(new Option<bool>("--history") { Description = "Include layer history as part of the comparison" });
        compressedSizeOption = Add(new Option<bool>("--compressed-size") { Description = "Show the compressed size of the layer" });
    }

    protected override void GetValues()
    {
        base.GetValues();
        OutputFormat = GetValue(outputOption);
        IsColorDisabled = GetValue(noColorOption);
        IncludeHistory = GetValue(historyOption);
        IncludeCompressedSize = GetValue(compressedSizeOption);
    }
}
