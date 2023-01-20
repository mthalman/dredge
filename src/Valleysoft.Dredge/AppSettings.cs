using Newtonsoft.Json;

namespace Valleysoft.Dredge;

internal class AppSettings
{
    public static readonly string SettingsPath =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Valleysoft.Dredge", "settings.json");

    public const string FileCompareToolName = "fileCompareTool";

    [JsonProperty(FileCompareToolName)]
    public FileCompareToolSettings FileCompareTool { get; set; } = new();

    [JsonProperty("platform")]
    public PlatformSettings Platform { get; set; } = new();

    private AppSettings() {}

    public static AppSettings Load()
    {
        if (!File.Exists(SettingsPath))
        {
            AppSettings settings = new();
            string settingsStr = JsonConvert.SerializeObject(settings, Formatting.Indented);

            string dirName = Path.GetDirectoryName(SettingsPath)!;
            if (!Directory.Exists(dirName))
            {
                Directory.CreateDirectory(dirName);
            }

            File.WriteAllText(SettingsPath, settingsStr);
            return settings;
        }
        else
        {
            string settings = File.ReadAllText(SettingsPath);
            return JsonConvert.DeserializeObject<AppSettings>(settings);
        }
    }

    public void Save()
    {
        string settingsStr = JsonConvert.SerializeObject(this, Formatting.Indented);
        File.WriteAllText(SettingsPath, settingsStr);
    }
}

internal class FileCompareToolSettings
{
    [JsonProperty("exePath")]
    public string ExePath { get; set; } = string.Empty;

    [JsonProperty("args")]
    public string Args { get; set; } = string.Empty;
}

internal class PlatformSettings
{
    [JsonProperty("os")]
    public string Os { get; set; } = string.Empty;

    [JsonProperty("osVersion")]
    public string OsVersion { get; set; } = string.Empty;

    [JsonProperty("arch")]
    public string Architecture { get; set; } = string.Empty;
}
