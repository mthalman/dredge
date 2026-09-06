using System.Text.Json.Serialization;

namespace Valleysoft.Dredge;

internal partial class AppSettings
{
    private static readonly object settingsFileLock = new();

    public static readonly string SettingsPath =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Valleysoft.Dredge", "settings.json");

    public const string FileCompareToolName = "fileCompareTool";

    [JsonPropertyName(FileCompareToolName)]
    public FileCompareToolSettings FileCompareTool { get; set; } = new();

    [JsonPropertyName("platform")]
    public PlatformSettings Platform { get; set; } = new();

    [JsonConstructor]
    private AppSettings() {}

    public static AppSettings Load() => Load(SettingsPath);

    internal static AppSettings Load(string settingsPath)
    {
        lock (settingsFileLock)
        {
            if (!File.Exists(settingsPath))
            {
                AppSettings settings = new();
                string settingsStr = JsonHelper.Serialize(settings);

                string? dirName = Path.GetDirectoryName(settingsPath);
                if (!string.IsNullOrEmpty(dirName) && !Directory.Exists(dirName))
                {
                    Directory.CreateDirectory(dirName);
                }

                File.WriteAllText(settingsPath, settingsStr);
                return settings;
            }

            string settingsContent = File.ReadAllText(settingsPath);
            if (string.IsNullOrWhiteSpace(settingsContent))
            {
                return null!;
            }

            return JsonHelper.Deserialize<AppSettings>(
                JsonHelper.MergeDuplicateObjects(settingsContent))!;
        }
    }

    public void Save()
    {
        lock (settingsFileLock)
        {
            string settingsStr = JsonHelper.Serialize(this);
            File.WriteAllText(SettingsPath, settingsStr);
        }
    }
}

internal partial class FileCompareToolSettings
{
    [JsonPropertyName("exePath")]
    public string ExePath { get; set; } = string.Empty;

    [JsonPropertyName("args")]
    public string Args { get; set; } = string.Empty;
}

internal partial class PlatformSettings
{
    [JsonPropertyName("os")]
    public string Os { get; set; } = string.Empty;

    [JsonPropertyName("osVersion")]
    public string OsVersion { get; set; } = string.Empty;

    [JsonPropertyName("arch")]
    public string Architecture { get; set; } = string.Empty;
}
