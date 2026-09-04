using System.CommandLine;

namespace Valleysoft.Dredge.Commands.Image;

public class CompareOptionsBase : PlatformOptionsBase
{
    public const string BaseArg = "base";
    public const string TargetArg = "target";

    private readonly Argument<string> baseImageArg;
    private readonly Argument<string> targetImageArg;

    public string BaseImage { get; set; } = string.Empty;
    public string TargetImage { get; set; } = string.Empty;

    public CompareOptionsBase()
    {
        baseImageArg = Add(new Argument<string>(BaseArg) { Description = "Base container image reference (<image>, <image>:<tag>, or <image>@<digest>)" });
        targetImageArg = Add(new Argument<string>(TargetArg) { Description = "Target container image reference (<image>, <image>:<tag>, or <image>@<digest>)" });
    }

    protected override void GetValues()
    {
        base.GetValues();
        BaseImage = GetValue(baseImageArg);
        TargetImage = GetValue(targetImageArg);
    }
}
