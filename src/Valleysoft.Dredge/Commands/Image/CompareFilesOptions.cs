using System.CommandLine;

namespace Valleysoft.Dredge.Commands.Image;

public class CompareFilesOptions : CompareOptionsBase
{
    public const string LayerIndexSuffix = "-layer-index";
    public const string BaseLayerIndexOptionName = $"--{BaseArg}{LayerIndexSuffix}";
    public const string TargetLayerIndexOptionName = $"--{TargetArg}{LayerIndexSuffix}";

    private readonly Option<int?> baseLayerIndex;
    private readonly Option<int?> targetLayerIndex;
    private readonly CliOutputOption<CompareFilesOutput> outputOption;

    public int? BaseLayerIndex { get; set; }
    public int? TargetLayerIndex { get; set; }
    public CompareFilesOutput OutputType { get; set; }

    public CompareFilesOptions()
    {
        baseLayerIndex = Add(LayerIndexOption.Create(
            BaseLayerIndexOptionName,
            "non-empty layer index of the base container image to compare with"));
        targetLayerIndex = Add(LayerIndexOption.Create(
            TargetLayerIndexOptionName,
            "non-empty layer index of the target container image to compare against"));
        outputOption = new CliOutputOption<CompareFilesOutput>(
            "Output type",
            CompareFilesOutput.ExternalTool,
            ("external-tool", CompareFilesOutput.ExternalTool));
        Add(outputOption.Option);
    }

    protected override void GetValues()
    {
        base.GetValues();
        BaseLayerIndex = GetValue(baseLayerIndex);
        TargetLayerIndex = GetValue(targetLayerIndex);
        OutputType = outputOption.GetValue(GetValue(outputOption.Option));
    }
}
