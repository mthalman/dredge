using System.CommandLine;

namespace Valleysoft.Dredge.Commands.Manifest;

public class ResolveOptions : OptionsBase
{
    public const string OsOptionName = "--os";
    public const string OsVersionOptionName = "--os-version";
    public const string ArchOptionName = "--arch";

    private readonly Argument<string> imageArg;
    private readonly Option<string> osOpt;
    private readonly Option<string> osVersionOpt;
    private readonly Option<string> archOpt;

    public string Image { get; set; } = string.Empty;
    public string? Os { get; set; }
    public string? OsVersion { get; set; }
    public string? Architecture { get; set; }

    public ResolveOptions()
    {
        imageArg = Add(new Argument<string>("image", "Name of the container image (<image>, <image>:<tag>, or <image>@<digest>)"));
        osOpt = Add(new Option<string>(OsOptionName, "Target OS of image (e.g. \"linux\", \"windows\")"));
        osVersionOpt = Add(new Option<string>(OsVersionOptionName, "Target OS version of image (Windows only, e.g. \"10.0.20348.1129\")"));
        archOpt = Add(new Option<string>(ArchOptionName, "Target architecture of image (e.g. \"amd64\", \"arm64\")"));
    }

    protected override void GetValues()
    {
        Image = GetValue(imageArg);
        Os = GetValue(osOpt);
        OsVersion = GetValue(osVersionOpt);
        Architecture = GetValue(archOpt);
    }
}
