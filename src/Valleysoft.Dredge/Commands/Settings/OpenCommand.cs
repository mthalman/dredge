using System.CommandLine;
using System.Diagnostics;

namespace Valleysoft.Dredge.Commands.Settings;

public class OpenCommand : Command
{
    public OpenCommand()
        : base("open", "Opens the Dredge settings file")
    {
        this.SetHandler(ExecuteAsync);
    }

    private Task ExecuteAsync()
    {
        return CommandHelper.ExecuteCommandAsync(null, () =>
        {
            // Ensure the settings are loaded which creates a default settings file if necessary
            AppSettings.Load();

            try
            {
                Process.Start(new ProcessStartInfo(AppSettings.SettingsPath) { UseShellExecute = true });
            }
            catch (Exception)
            {
                Console.WriteLine(AppSettings.SettingsPath);
            }

            return Task.CompletedTask;
        });
    }
}
