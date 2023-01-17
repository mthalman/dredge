using System.CommandLine;

namespace Valleysoft.Dredge.Commands.Settings;

internal class SettingsCommand : Command
{
    public SettingsCommand(IDockerRegistryClientFactory dockerRegistryClientFactory)
        : base("settings", "Commands related to Dredge settings")
    {
        AddCommand(new OpenCommand());
        AddCommand(new ClearCacheCommand());
        AddCommand(new SetCommand());
        AddCommand(new GetCommand());
    }
}
