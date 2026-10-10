namespace Valleysoft.Dredge;

internal static class FirstRunExperience
{
    private const string CompletionFileName = ".first-run-complete";
    private const string Logo = """
      ____  ____  _____ ____   ____ _____
     |  _ \|  _ \| ____|  _ \ / ___| ____|
     | | | | |_) |  _| | | | | |  _|  _|
     | |_| |  _ <| |___| |_| | |_| | |___
     |____/|_| \_\_____|____/ \____|_____|
    """;

    internal static void ShowIfNeeded(
        string[] args,
        TextWriter output,
        string? stateDirectory = null)
    {
        if (!IsWelcomeInvocation(args))
        {
            return;
        }

        stateDirectory ??= Path.GetDirectoryName(AppSettings.SettingsPath)!;
        Directory.CreateDirectory(stateDirectory);
        string completionPath = Path.Combine(stateDirectory, CompletionFileName);

        try
        {
            using FileStream _ = new(completionPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        }
        catch (IOException) when (File.Exists(completionPath))
        {
            return;
        }

        output.WriteLine(Logo);
        output.WriteLine();
        output.WriteLine("Welcome to Dredge, a CLI for exploring container registries.");
        output.WriteLine();
        output.WriteLine("Get started:");
        output.WriteLine("  dredge image explore <image>  Explore an image's files and packages");
        output.WriteLine("  dredge tag list <repository>  List tags in a repository");
        output.WriteLine("  dredge --help                 See all commands");
        output.WriteLine();
    }

    private static bool IsWelcomeInvocation(string[] args) =>
        args.Length == 0 || args.Any(arg => arg is "--help" or "-h" or "-?");
}
