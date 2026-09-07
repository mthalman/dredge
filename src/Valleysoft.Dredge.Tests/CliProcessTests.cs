using System.Diagnostics;

namespace Valleysoft.Dredge.Tests;

public sealed class CliProcessTests
{
    [Fact]
    public async Task Help_ListsTopLevelCommands()
    {
        ProcessResult result = await InvokeDredgeProcessAsync("--help");

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("image", result.StandardOutput);
        Assert.Contains("manifest", result.StandardOutput);
        Assert.Contains("referrer", result.StandardOutput);
        Assert.Contains("settings", result.StandardOutput);
    }

    [Fact]
    public async Task UnknownCommand_ReturnsParseFailure()
    {
        ProcessResult result = await InvokeDredgeProcessAsync("not-a-command");

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("not-a-command", result.StandardError);
    }

    [Fact]
    public async Task InvalidImageReference_ReturnsComponentErrorAndAcceptedForms()
    {
        ProcessResult result = await InvokeDredgeProcessAsync(
            "manifest",
            "digest",
            "Invalid/Repo");

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("Invalid repository", result.StandardError);
        Assert.Contains(
            "Expected <image>, <image>:<tag>, or <image>@<digest>.",
            result.StandardError);
    }

    private static async Task<ProcessResult> InvokeDredgeProcessAsync(params string[] args)
    {
        ProcessStartInfo startInfo = new("dotnet")
        {
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false
        };
        startInfo.ArgumentList.Add(GetDredgeAssemblyPath());
        foreach (string arg in args)
        {
            startInfo.ArgumentList.Add(arg);
        }

        using Process process = Process.Start(startInfo)!;
        Task<string> standardOutput = process.StandardOutput.ReadToEndAsync(
            TestContext.Current.CancellationToken);
        Task<string> standardError = process.StandardError.ReadToEndAsync(
            TestContext.Current.CancellationToken);
        try
        {
            await process.WaitForExitAsync(TestContext.Current.CancellationToken);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw;
        }

        return new(process.ExitCode, await standardOutput, await standardError);
    }

    private static string GetDredgeAssemblyPath()
    {
        DirectoryInfo targetFrameworkDirectory = new(AppContext.BaseDirectory);
        string targetFramework = targetFrameworkDirectory.Name;
        DirectoryInfo configurationDirectory = targetFrameworkDirectory.Parent!;
        string configuration = configurationDirectory.Name;
        DirectoryInfo testProjectDirectory = configurationDirectory.Parent!.Parent!;
        string path = Path.Combine(
            testProjectDirectory.Parent!.FullName,
            "Valleysoft.Dredge",
            "bin",
            configuration,
            targetFramework,
            "dredge.dll");
        Assert.True(File.Exists(path), $"Dredge assembly was not found at '{path}'.");
        return path;
    }

    private sealed record ProcessResult(
        int ExitCode,
        string StandardOutput,
        string StandardError);
}
