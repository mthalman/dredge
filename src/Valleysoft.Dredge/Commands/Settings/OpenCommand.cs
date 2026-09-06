using System.CommandLine;
using System.Diagnostics;

namespace Valleysoft.Dredge.Commands.Settings;

public class OpenCommand : Command
{
    private readonly IAppSettingsStore settingsStore;
    private readonly IProcessLauncher processLauncher;
    private readonly IProcessTerminator processTerminator;
    private readonly TextWriter output;

    public OpenCommand()
        : this(
            new AppSettingsStore(),
            new ProcessLauncher(),
            new ProcessTerminator(),
            Console.Out)
    {
    }

    internal OpenCommand(
        IAppSettingsStore settingsStore,
        IProcessLauncher processLauncher,
        IProcessTerminator processTerminator,
        TextWriter output)
        : base("open", "Opens the Dredge settings file")
    {
        this.settingsStore = settingsStore;
        this.processLauncher = processLauncher;
        this.processTerminator = processTerminator;
        this.output = output;
        this.SetAction((parseResult, cancellationToken) => ExecuteAsync(cancellationToken));
    }

    private Task ExecuteAsync(CancellationToken cancellationToken)
    {
        return CommandHelper.ExecuteCommandAsync(null, cancellationToken, ct =>
        {
            ct.ThrowIfCancellationRequested();
            // Ensure the settings are loaded which creates a default settings file if necessary
            settingsStore.Load();

            try
            {
                processLauncher.Start(
                    new ProcessStartInfo(settingsStore.SettingsPath) { UseShellExecute = true });
            }
            catch (Exception)
            {
                output.WriteLine(settingsStore.SettingsPath);
            }

            return Task.CompletedTask;
        }, exit: processTerminator.Exit);
    }
}
