using System.CommandLine;

namespace Valleysoft.Dredge.Commands;

public class PlatformOptionsBase : OptionsBase
{
    public const string OsOptionName = "--os";
    public const string OsVersionOptionName = "--os-version";
    public const string ArchOptionName = "--arch";

    private readonly Option<string> osOpt;
    private readonly Option<string> osVersionOpt;
    private readonly Option<string> emptyOsVersionOpt;
    private readonly Option<string> archOpt;

    public string? Os { get; set; }
    public string? OsVersion { get; set; }
    public string? Architecture { get; set; }
    public string? DockerContext { get; set; }
    public string? DockerHost { get; set; }
    internal bool UsePlatformDefaults { get; set; } = true;

    public PlatformOptionsBase()
    {
        osOpt = Add(new Option<string>(OsOptionName) { Description = "Target OS of image (e.g. \"linux\", \"windows\")" });
        osVersionOpt = Add(new Option<string>(OsVersionOptionName)
        {
            Description = "Target OS version of image (e.g. \"10.0.20348.1129\"); pass an empty value to ignore the saved version",
            Arity = ArgumentArity.ZeroOrMore,
            AllowMultipleArgumentsPerToken = false,
            CustomParser = static result => result.Tokens.LastOrDefault()?.Value ?? ""
        });
        osVersionOpt.Validators.Add(static result =>
        {
            if (result.Tokens.Count == 0)
            {
                result.AddError($"Required argument missing for option: '{OsVersionOptionName}'.");
            }
        });
        // An attached empty value must not consume the next positional argument.
        emptyOsVersionOpt = Add(new Option<string>(OsVersionOptionName + "=")
        {
            Arity = ArgumentArity.Zero,
            Hidden = true,
            CustomParser = static _ => ""
        });
        archOpt = Add(new Option<string>(ArchOptionName) { Description = "Target architecture of image (e.g. \"amd64\", \"arm64\")" });
    }

    protected override void GetValues()
    {
        Os = GetValue(osOpt);
        OsVersion = GetValue(osVersionOpt, emptyOsVersionOpt);
        Architecture = GetValue(archOpt);
    }

    internal (string? Os, string? Architecture, string? OsVersion) ResolvePlatform(IAppSettingsStore settingsStore)
    {
        PlatformSettings? defaults = UsePlatformDefaults &&
            (string.IsNullOrEmpty(Os) || string.IsNullOrEmpty(Architecture) || OsVersion is null)
            ? settingsStore.Load().Platform : null;
        string? version = OsVersion ?? defaults?.OsVersion;
        return (Resolve(Os, defaults?.Os), Resolve(Architecture, defaults?.Architecture),
            string.IsNullOrEmpty(version) ? null : version);

        static string? Resolve(string? option, string? setting) =>
            !string.IsNullOrEmpty(option) ? option : string.IsNullOrEmpty(setting) ? null : setting;
    }
}
