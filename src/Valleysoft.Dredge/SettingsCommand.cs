using System.CommandLine;
using System.Diagnostics;

namespace Valleysoft.Dredge;

internal class SettingsCommand : Command
{
    public SettingsCommand()
        : base("settings", "Commands related to Dredge settings")
    {
        AddCommand(new OpenSettingsCommand());
        AddCommand(new ClearCacheCommand());
    }

    private class OpenSettingsCommand : Command
    {
        public OpenSettingsCommand()
            : base("open", "Opens the Dredge settings file")
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

    private class ClearCacheCommand : Command
    {
        public ClearCacheCommand()
            : base("clear-cache", "Deletes the cached files used by Dredge")
        {
            this.SetHandler(ExecuteAsync);
        }

        private Task ExecuteAsync()
        {
            return CommandHelper.ExecuteCommandAsync(null, () =>
            {
                DirectoryInfo dredgeTempDir = new(DredgeState.DredgeTempPath);

                if (dredgeTempDir.Exists)
                {
                    long dirSize = DirSize(dredgeTempDir);
                    dredgeTempDir.Delete(recursive: true);

                    Console.WriteLine($"{dirSize:n0} bytes deleted from '{DredgeState.DredgeTempPath}'");
                }
                else
                {
                    Console.WriteLine($"Nothing to do. Cache directory '{DredgeState.DredgeTempPath}' does not exist.");
                }

                return Task.CompletedTask;
            });
        }

        private static long DirSize(DirectoryInfo dir)
        {
            long size = 0;
            FileInfo[] files = dir.GetFiles();
            foreach (FileInfo file in files)
            {
                size += file.Length;
            }

            DirectoryInfo[] subDirs = dir.GetDirectories();
            foreach (DirectoryInfo subDir in subDirs)
            {
                size += DirSize(subDir);
            }

            return size;
        }
    }
}
