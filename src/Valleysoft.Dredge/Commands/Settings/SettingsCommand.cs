﻿using System.CommandLine;

namespace Valleysoft.Dredge.Commands.Settings;

internal class SettingsCommand : Command
{
    public SettingsCommand()
        : base("settings", "Commands related to Dredge settings")
    {
        AddCommand(new OpenCommand());
        AddCommand(new GetCommand());
        AddCommand(new SetCommand());
        AddCommand(new ClearCacheCommand());
    }
}
