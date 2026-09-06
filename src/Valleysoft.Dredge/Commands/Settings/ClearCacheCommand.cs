using System.CommandLine;

namespace Valleysoft.Dredge.Commands.Settings;

public class ClearCacheCommand : Command
{
    private readonly IDredgePathProvider pathProvider;
    private readonly TextWriter output;
    private readonly IProcessTerminator processTerminator;

    public ClearCacheCommand()
        : this(new DredgePathProvider(), Console.Out, new ProcessTerminator())
    {
    }

    internal ClearCacheCommand(
        IDredgePathProvider pathProvider,
        TextWriter output,
        IProcessTerminator processTerminator)
        : base("clear-cache", "Deletes the cached files used by Dredge")
    {
        this.pathProvider = pathProvider;
        this.output = output;
        this.processTerminator = processTerminator;
        this.SetAction((parseResult, cancellationToken) => ExecuteAsync(cancellationToken));
    }

    private Task ExecuteAsync(CancellationToken cancellationToken)
    {
        return CommandHelper.ExecuteCommandAsync(null, cancellationToken, ct =>
        {
            ct.ThrowIfCancellationRequested();
            string cachePath = pathProvider.TempPath;
            DirectoryInfo dredgeTempDir = new(cachePath);

            if (dredgeTempDir.Exists)
            {
                long dirSize = DirSize(dredgeTempDir, ct);
                ct.ThrowIfCancellationRequested();
                dredgeTempDir.Delete(recursive: true);

                output.WriteLine($"{dirSize:n0} bytes deleted from '{cachePath}'");
            }
            else
            {
                output.WriteLine($"Nothing to do. Cache directory '{cachePath}' does not exist.");
            }

            return Task.CompletedTask;
        }, exit: processTerminator.Exit);
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
