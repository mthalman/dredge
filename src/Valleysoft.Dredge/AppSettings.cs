using Newtonsoft.Json;

namespace Valleysoft.Dredge;

internal partial class AppSettings
{
    private static readonly object settingsFileLock = new();

    public static readonly string SettingsPath =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Valleysoft.Dredge", "settings.json");

    public const string FileCompareToolName = "fileCompareTool";

    [JsonProperty(FileCompareToolName)]
    public FileCompareToolSettings FileCompareTool { get; set; } = new();

    [JsonProperty("platform")]
    public PlatformSettings Platform { get; set; } = new();

    private AppSettings() {}

    public static AppSettings Load() => Load(SettingsPath);

    internal static AppSettings Load(string settingsPath)
    {
        lock (settingsFileLock)
        {
            if (!File.Exists(settingsPath))
            {
                AppSettings settings = new();
                string settingsStr = JsonConvert.SerializeObject(settings, JsonHelper.Settings);

                string dirName = Path.GetDirectoryName(settingsPath)!;
                if (!Directory.Exists(dirName))
                {
                    Directory.CreateDirectory(dirName);
                }

                File.WriteAllText(settingsPath, settingsStr);
                return settings;
            }

            string settingsContent = File.ReadAllText(settingsPath);
            return JsonConvert.DeserializeObject<AppSettings>(settingsContent)!;
        }
    }

    public void Save()
    {
        lock (settingsFileLock)
        {
            string settingsStr = JsonConvert.SerializeObject(this, JsonHelper.Settings);
            File.WriteAllText(SettingsPath, settingsStr);
        }
    }
}

internal partial class FileCompareToolSettings
{
    [JsonProperty("exePath")]
    public string ExePath { get; set; } = string.Empty;

    [JsonProperty("args")]
    public string Args { get; set; } = string.Empty;
}

internal partial class PlatformSettings
{
    [JsonProperty("os")]
    public string Os { get; set; } = string.Empty;

    [JsonProperty("osVersion")]
    public string OsVersion { get; set; } = string.Empty;

    [JsonProperty("arch")]
    public string Architecture { get; set; } = string.Empty;
}
