using System.Globalization;
using System.Text.Json.Serialization;
using IOPath = System.IO.Path;

namespace Valleysoft.Dredge;

[GenerateSettings]
internal partial class AppSettings
{
    private static readonly object settingsFileLock = new();
    private string settingsPath = SettingsPath;

    public static readonly string SettingsPath =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Valleysoft.Dredge", "settings.json");

    public static readonly TimeSpan DefaultOperationTimeout = TimeSpan.FromMinutes(30);
    public static readonly TimeSpan MaximumOperationTimeout = TimeSpan.FromMilliseconds(int.MaxValue);

    public const string FileCompareToolName = "fileCompareTool";

    [JsonPropertyName(FileCompareToolName)]
    public FileCompareToolSettings FileCompareTool { get; set; } = new();

    [JsonPropertyName("operations")]
    public OperationsSettings Operations { get; set; } = new();

    [JsonPropertyName("platform")]
    public PlatformSettings Platform { get; set; } = new();

    [JsonPropertyName("cache")]
    public CacheSettings Cache { get; set; } = new();

    [JsonPropertyName("explore")]
    public ExploreSettings Explore { get; set; } = new();

    [JsonConstructor]
    internal AppSettings() {}

    public static AppSettings Load() => Load(SettingsPath);

    internal static AppSettings Load(string settingsPath)
    {
        lock (settingsFileLock)
        {
            if (!File.Exists(settingsPath))
            {
                AppSettings defaultSettings = new() { settingsPath = settingsPath };
                string settingsStr = JsonHelper.Serialize(defaultSettings);

                string? dirName = Path.GetDirectoryName(settingsPath);
                if (!string.IsNullOrEmpty(dirName) && !Directory.Exists(dirName))
                {
                    Directory.CreateDirectory(dirName);
                }

                File.WriteAllText(settingsPath, settingsStr);
                return defaultSettings;
            }

            string settingsContent = File.ReadAllText(settingsPath);
            if (string.IsNullOrWhiteSpace(settingsContent))
            {
                return null!;
            }

            AppSettings loadedSettings = JsonHelper.Deserialize<AppSettings>(
                JsonHelper.MergeDuplicateObjects(settingsContent))!;
            loadedSettings.settingsPath = settingsPath;
            return loadedSettings;
        }
    }

    public void Save()
    {
        lock (settingsFileLock)
        {
            string settingsStr = JsonHelper.Serialize(this);
            File.WriteAllText(settingsPath, settingsStr);
        }
    }
}

[GenerateSettings]
internal partial class FileCompareToolSettings
{
    [JsonPropertyName("exePath")]
    public string ExePath { get; set; } = string.Empty;

    [JsonPropertyName("args")]
    public string Args { get; set; } = string.Empty;
}

[GenerateSettings]
internal partial class OperationsSettings
{
    [JsonPropertyName("timeout")]
    public string? Timeout { get; set; } = AppSettings.DefaultOperationTimeout.ToString("c");

    public TimeSpan? GetTimeout()
    {
        string? value = Timeout;
        if (string.IsNullOrWhiteSpace(value) || value == "null")
        {
            return null;
        }

        if (!TimeSpan.TryParse(value, out TimeSpan parsed))
        {
            throw new InvalidOperationException($"Invalid operations.timeout value '{value}'.");
        }

        if (parsed > AppSettings.MaximumOperationTimeout)
        {
            throw new InvalidOperationException(
                $"The operations.timeout value '{value}' exceeds the maximum supported timeout of {AppSettings.MaximumOperationTimeout}.");
        }

        return parsed;
    }
}

[GenerateSettings]
internal partial class PlatformSettings
{
    [JsonPropertyName("os")]
    public string Os { get; set; } = string.Empty;

    [JsonPropertyName("osVersion")]
    public string OsVersion { get; set; } = string.Empty;

    [JsonPropertyName("arch")]
    public string Architecture { get; set; } = string.Empty;
}

[GenerateSettings]
internal partial class CacheSettings
{
    public const long DefaultMaxBytes = 5L * 1024 * 1024 * 1024;

    [JsonPropertyName("path")]
    public string Path { get; set; } = string.Empty;

    [JsonPropertyName("maxBytes")]
    public string MaxBytes { get; set; } = DefaultMaxBytes.ToString(CultureInfo.InvariantCulture);

    public long GetMaxBytes() =>
        long.TryParse(MaxBytes, NumberStyles.None,
            CultureInfo.InvariantCulture, out long value)
            ? value
            : throw new InvalidOperationException(
                $"Invalid cache.maxBytes value '{MaxBytes}'; expected a nonnegative integer.");

    public string GetPath()
    {
        string? configured = Environment.GetEnvironmentVariable("DREDGE_CACHE_DIR");
        if (string.IsNullOrWhiteSpace(configured))
        {
            configured = Path;
        }
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return IOPath.GetFullPath(configured);
        }

        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (OperatingSystem.IsWindows())
        {
            return IOPath.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Valleysoft.Dredge", "cache");
        }
        if (OperatingSystem.IsMacOS())
        {
            return IOPath.Combine(home, "Library", "Caches", "Valleysoft.Dredge");
        }
        string? xdg = Environment.GetEnvironmentVariable("XDG_CACHE_HOME");
        return IOPath.Combine(
            string.IsNullOrEmpty(xdg) || !IOPath.IsPathFullyQualified(xdg)
                ? IOPath.Combine(home, ".cache")
                : xdg,
            "Valleysoft.Dredge");
    }
}

[GenerateSettings]
internal partial class ExploreSettings
{
    [JsonPropertyName("theme")]
    public string Theme { get; set; } = "dark";

    [JsonPropertyName("mouse")]
    public string Mouse { get; set; } = "true";

    [JsonPropertyName("clipboard")]
    public string Clipboard { get; set; } = "off";

    [JsonPropertyName("keys")]
    public ExploreKeysSettings Keys { get; set; } = new();

    public bool IsMouseEnabled() => Mouse switch
    {
        "true" => true,
        "false" => false,
        _ => throw new InvalidOperationException(
            $"Invalid explore.mouse value '{Mouse}'; expected true or false.")
    };

    public bool IsOsc52ClipboardEnabled() => Clipboard switch
    {
        "off" or "" => false,
        "osc52" => true,
        _ => throw new InvalidOperationException(
            $"Invalid explore.clipboard value '{Clipboard}'; expected off or osc52.")
    };
}

// One remappable explorer key per action. Empty means the built-in default.
[GenerateSettings]
internal partial class ExploreKeysSettings
{
    [JsonPropertyName("quit")]
    public string Quit { get; set; } = "";

    [JsonPropertyName("help")]
    public string Help { get; set; } = "";

    [JsonPropertyName("insights")]
    public string Insights { get; set; } = "";

    [JsonPropertyName("search")]
    public string Search { get; set; } = "";

    [JsonPropertyName("wholeFilesystem")]
    public string WholeFilesystem { get; set; } = "";

    [JsonPropertyName("firstUserLayer")]
    public string FirstUserLayer { get; set; } = "";

    [JsonPropertyName("compare")]
    public string Compare { get; set; } = "";

    [JsonPropertyName("findingsOnly")]
    public string FindingsOnly { get; set; } = "";

    [JsonPropertyName("previousLayer")]
    public string PreviousLayer { get; set; } = "";

    [JsonPropertyName("nextLayer")]
    public string NextLayer { get; set; } = "";

    [JsonPropertyName("toggleAdded")]
    public string ToggleAdded { get; set; } = "";

    [JsonPropertyName("toggleModified")]
    public string ToggleModified { get; set; } = "";

    [JsonPropertyName("toggleIdentical")]
    public string ToggleIdentical { get; set; } = "";

    [JsonPropertyName("toggleDeleted")]
    public string ToggleDeleted { get; set; } = "";

    [JsonPropertyName("platform")]
    public string Platform { get; set; } = "";

    [JsonPropertyName("extract")]
    public string Extract { get; set; } = "";

    [JsonPropertyName("copyCommand")]
    public string CopyCommand { get; set; } = "";

    [JsonPropertyName("pager")]
    public string Pager { get; set; } = "";

    [JsonPropertyName("swapSides")]
    public string SwapSides { get; set; } = "";

    [JsonPropertyName("retry")]
    public string Retry { get; set; } = "";
}