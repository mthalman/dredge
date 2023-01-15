using System.CommandLine;

namespace Valleysoft.Dredge.Commands.Image;

public class CompareOptionsBase : OptionsBase
{
    public const string BaseArg = "base";
    public const string TargetArg = "target";

    private readonly Argument<string> baseImageArg;
    private readonly Argument<string> targetImageArg;

    public string BaseImage { get; set; } = string.Empty;
    public string TargetImage { get; set; } = string.Empty;

    public CompareOptionsBase()
    {
        baseImageArg = Add(new Argument<string>(BaseArg, "Name of the base container image (<image>, <image>:<tag>, or <image>@<digest>)"));
        targetImageArg = Add(new Argument<string>(TargetArg, "Name of the target container image (<image>, <image>:<tag>, or <image>@<digest>)"));
    }

    protected override void GetValues()
    {
        BaseImage = GetValue(baseImageArg);
        TargetImage = GetValue(targetImageArg);
    }
}
