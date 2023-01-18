using System.CommandLine;

namespace Valleysoft.Dredge.Commands;

public class PlatformOptionsBase : OptionsBase
{
    public const string OsOptionName = "--os";
    public const string OsVersionOptionName = "--os-version";
    public const string ArchOptionName = "--arch";

    private readonly Option<string> osOpt;
    private readonly Option<string> osVersionOpt;
    private readonly Option<string> archOpt;

    public string? Os { get; set; }
    public string? OsVersion { get; set; }
    public string? Architecture { get; set; }

    public PlatformOptionsBase()
    {
        osOpt = Add(new Option<string>(OsOptionName, "Target OS of image (e.g. \"linux\", \"windows\")"));
        osVersionOpt = Add(new Option<string>(OsVersionOptionName, "Target OS version of image (e.g. \"10.0.20348.1129\")"));
        archOpt = Add(new Option<string>(ArchOptionName, "Target architecture of image (e.g. \"amd64\", \"arm64\")"));
    }

    protected override void GetValues()
    {
        Os = GetValue(osOpt);
        OsVersion = GetValue(osVersionOpt);
        Architecture = GetValue(archOpt);
    }
}
