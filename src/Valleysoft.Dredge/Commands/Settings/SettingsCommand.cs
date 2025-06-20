using System.CommandLine;

namespace Valleysoft.Dredge.Commands.Settings;

internal class SettingsCommand : Command
{
    public SettingsCommand()
        : base("settings", "Commands related to Dredge settings")
    {
        Subcommands.Add(new OpenCommand());
        Subcommands.Add(new GetCommand());
        Subcommands.Add(new SetCommand());
        Subcommands.Add(new ClearCacheCommand());
    }
}
