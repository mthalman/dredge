using System.CommandLine;
using System.Diagnostics;

namespace Valleysoft.Dredge.Commands.Settings;

public class OpenCommand : Command
{
    public OpenCommand()
        : base("open", "Opens the Dredge settings file")
    {
        this.SetAction((parseResult, cancellationToken) => ExecuteAsync(cancellationToken));
    }

    private Task ExecuteAsync(CancellationToken cancellationToken)
    {
        return CommandHelper.ExecuteCommandAsync(null, cancellationToken, ct =>
        {
            ct.ThrowIfCancellationRequested();
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
