using Newtonsoft.Json;
using Valleysoft.Dredge.Core;

namespace Valleysoft.Dredge;

internal class AppSettingsHelper
{
    public static readonly string SettingsPath =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Valleysoft.Dredge", "settings.json");

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

    public static void Save(AppSettings appSettings)
    {
        string settingsStr = JsonConvert.SerializeObject(appSettings, Formatting.Indented);
        File.WriteAllText(SettingsPath, settingsStr);
    }
}
