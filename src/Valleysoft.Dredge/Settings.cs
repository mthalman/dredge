using Newtonsoft.Json;

namespace Valleysoft.Dredge;

internal class Settings
{
    public static readonly string SettingsPath =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Valleysoft.Dredge", "settings.json");

    public const string FileCompareToolName = "fileCompareTool";

    [JsonProperty(FileCompareToolName)]
    public FileCompareToolSettings FileCompareTool { get; set; } = new();

    private Settings() {}

    public static Settings Load()
    {
        if (!File.Exists(SettingsPath))
        {
            Settings settings = new();
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
            return JsonConvert.DeserializeObject<Settings>(settings);
        }
    }
}

internal class FileCompareToolSettings
{
    [JsonProperty("exePath")]
    public string ExePath { get; set; } = string.Empty;

    [JsonProperty("args")]
    public string Args { get; set; } = string.Empty;
}
