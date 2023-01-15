using System.CommandLine;

namespace Valleysoft.Dredge.Commands.Settings;

public class ClearCacheCommand : Command
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
