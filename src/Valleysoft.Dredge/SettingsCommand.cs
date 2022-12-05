using System.CommandLine;
using System.Diagnostics;

namespace Valleysoft.Dredge;

internal class SettingsCommand : Command
{
    public SettingsCommand()
        : base("settings", "Opens the Dredge settings file")
    {
        this.SetHandler(ExecuteAsync);
    }

    private Task ExecuteAsync()
    {
        return CommandHelper.ExecuteCommandAsync(null, () =>
        {
            // Ensure the settings are loaded which creates a default settings file if necessary
            Settings.Load();

            try
            {
                Process.Start(new ProcessStartInfo(Settings.SettingsPath) { UseShellExecute = true });
            }
            catch (Exception)
            {
                Console.WriteLine(Settings.SettingsPath);
            }

            return Task.CompletedTask;
        });
    }
}
