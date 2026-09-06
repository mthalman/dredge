namespace Valleysoft.Dredge;

internal interface IAppSettingsStore
{
    string SettingsPath { get; }

    AppSettings Load();
}

internal sealed class AppSettingsStore : IAppSettingsStore
{
    public AppSettingsStore()
        : this(AppSettings.SettingsPath)
    {
    }

    internal AppSettingsStore(string settingsPath)
    {
        SettingsPath = settingsPath;
    }

    public string SettingsPath { get; }

    public AppSettings Load() => AppSettings.Load(SettingsPath);
}
