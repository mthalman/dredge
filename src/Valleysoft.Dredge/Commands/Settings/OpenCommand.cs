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
            AppSettingsHelper.Load();

            try
            {
                Process.Start(new ProcessStartInfo(AppSettingsHelper.SettingsPath) { UseShellExecute = true });
            }
            catch (Exception)
            {
                Console.WriteLine(AppSettingsHelper.SettingsPath);
            }

            return Task.CompletedTask;
        });
    }
}
