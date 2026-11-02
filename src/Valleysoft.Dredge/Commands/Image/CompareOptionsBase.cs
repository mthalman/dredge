using System.CommandLine;

namespace Valleysoft.Dredge.Commands.Image;

public class CompareOptionsBase : ImageOptionsBase
{
    public const string BaseArg = "base";
    public const string TargetArg = "target";

    private readonly Argument<string> baseImageArg;
    private readonly Argument<string> targetImageArg;

    public string BaseImage { get; set; } = string.Empty;
    public string TargetImage { get; set; } = string.Empty;

    public CompareOptionsBase()
    {
        baseImageArg = Add(ImageReferenceArgument.CreateImageSource(BaseArg, "Base container image"));
        targetImageArg = Add(ImageReferenceArgument.CreateImageSource(TargetArg, "Target container image"));
    }

    protected override void GetValues()
    {
        base.GetValues();
        BaseImage = GetValue(baseImageArg);
        TargetImage = GetValue(targetImageArg);
    }
}
