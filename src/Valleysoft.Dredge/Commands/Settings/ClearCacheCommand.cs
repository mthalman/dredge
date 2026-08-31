using System.CommandLine;

namespace Valleysoft.Dredge.Commands.Settings;

public class ClearCacheCommand : Command
{
    public ClearCacheCommand()
        : base("clear-cache", "Deletes the cached files used by Dredge")
    {
        this.SetAction((parseResult, cancellationToken) => ExecuteAsync(cancellationToken));
    }

    private Task ExecuteAsync(CancellationToken cancellationToken)
    {
        return CommandHelper.ExecuteCommandAsync(null, cancellationToken, ct =>
        {
            ct.ThrowIfCancellationRequested();
            DirectoryInfo dredgeTempDir = new(DredgeState.DredgeTempPath);

            if (dredgeTempDir.Exists)
            {
                long dirSize = DirSize(dredgeTempDir, ct);
                ct.ThrowIfCancellationRequested();
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

    private static long DirSize(DirectoryInfo dir, CancellationToken cancellationToken)
    {
        long size = 0;
        foreach (FileInfo file in dir.EnumerateFiles())
        {
            cancellationToken.ThrowIfCancellationRequested();
            size += file.Length;
        }

        foreach (DirectoryInfo subDir in dir.EnumerateDirectories())
        {
            size += DirSize(subDir, cancellationToken);
        }

        return size;
    }
}
