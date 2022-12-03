using System.CommandLine;
using System.Diagnostics;

namespace Valleysoft.Dredge;

internal class SettingsCommand : Command
{
    public SettingsCommand()
        : base("settings", "Opens the Dredge settings file")
    {
        this.SetHandler(Execute);
    }

    private void Execute()
    {
        // Ensure the settings are loaded which creates a default settings file if necessary
        Settings.Load();

        try
        {
            Process.Start(new ProcessStartInfo(Settings.SettingsPath) { UseShellExecute = true });
        }
        catch(Exception)
        {
            Console.WriteLine(Settings.SettingsPath);
        }
    }
}
