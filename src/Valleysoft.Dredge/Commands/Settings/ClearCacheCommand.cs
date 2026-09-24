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
        return CommandHelper.ExecuteCommandAsync(null, cancellationToken, async ct =>
        {
            ct.ThrowIfCancellationRequested();
            bool found = false;
            string cachePath = pathProvider.CachePath;
            if (Directory.Exists(Path.Combine(cachePath, "layer-store")))
            {
                await using LayerStore store = new(cachePath);
                long removed = await store.ClearAsync(ct);
                output.WriteLine($"{removed:n0} bytes deleted from '{cachePath}'");
                found = true;
            }
            foreach (string legacyName in new[] { "layers", "compare" })
            {
                string legacyPath = Path.Combine(pathProvider.TempPath, legacyName);
                if (Directory.Exists(legacyPath))
                {
                    CacheFileSystem.ValidateNotLink(pathProvider.TempPath);
                    long removed = DeleteLegacyDirectory(new DirectoryInfo(legacyPath), ct);
                    output.WriteLine($"{removed:n0} bytes deleted from '{legacyPath}'");
                    found = true;
                }
            }
            if (!found)
            {
                output.WriteLine($"Nothing to do. No cached data found in '{cachePath}'.");
            }
        }, exit: processTerminator.Exit);
    }

    private static long DeleteLegacyDirectory(DirectoryInfo dir, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (dir.Attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            dir.Delete();
            return 0;
        }
        long size = 0;
        foreach (FileInfo file in dir.EnumerateFiles())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!file.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                size += file.Length;
            }
            file.Delete();
        }

        foreach (DirectoryInfo subDir in dir.EnumerateDirectories())
        {
            size += DeleteLegacyDirectory(subDir, cancellationToken);
        }
        cancellationToken.ThrowIfCancellationRequested();
        dir.Delete();
        return size;
    }
}
