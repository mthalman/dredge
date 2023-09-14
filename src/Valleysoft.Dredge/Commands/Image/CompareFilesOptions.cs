using System.CommandLine;

namespace Valleysoft.Dredge.Commands.Image;

public class CompareFilesOptions : CompareOptionsBase
{
    public const string LayerIndexSuffix = "-layer-index";

    private readonly Option<int?> baseLayerIndex;
    private readonly Option<int?> targetLayerIndex;
    private readonly Option<CompareFilesOutput> outputOption;

    public int? BaseLayerIndex { get; set; }
    public int? TargetLayerIndex { get; set; }
    public CompareFilesOutput OutputType { get; set; }

    public CompareFilesOptions()
    {
        baseLayerIndex = Add(new Option<int?>($"--{BaseArg}{LayerIndexSuffix}", "Non-empty layer index of the base container image to compare with"));
        targetLayerIndex = Add(new Option<int?>($"--{TargetArg}{LayerIndexSuffix}", "Non-empty layer index of the target container image to compare against"));
        outputOption = Add(new Option<CompareFilesOutput>("--output", () => CompareFilesOutput.ExternalTool, "Output type"));
    }

    protected override void GetValues()
    {
        base.GetValues();
        BaseLayerIndex = GetValue(baseLayerIndex);
        TargetLayerIndex = GetValue(targetLayerIndex);
        OutputType = GetValue(outputOption);
    }
}
