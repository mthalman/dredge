using System.Diagnostics;
using Spectre.Console.Rendering;
using System.Text;

namespace Valleysoft.Dredge.Tests;

public static class TestHelper
{
    public static string GetString(IEnumerable<Segment> segments)
    {
        StringBuilder builder = new();
        foreach (Segment segment in segments)
        {
            builder.Append(segment.Text);
        }
        return builder.ToString();
    }

    public static string Normalize(string val) =>
        val.Replace("\r", string.Empty).TrimEnd();
}

internal sealed class TestProcessTerminator : IProcessTerminator
{
    public void Exit(int exitCode) =>
        throw new InvalidOperationException($"Command requested process exit code {exitCode}.");
}

internal sealed class RecordingProcessTerminator : IProcessTerminator
{
    public int? ExitCode { get; private set; }

    public void Exit(int exitCode)
    {
        ExitCode = exitCode;
    }
}

internal sealed class TestAppSettingsStore(
    AppSettings settings,
    string settingsPath = "settings.json") : IAppSettingsStore
{
    public string SettingsPath { get; } = settingsPath;

    public AppSettings Load() => settings;
}

internal sealed class TestDredgePathProvider(string tempPath) : IDredgePathProvider
{
    public string TempPath { get; } = tempPath;
}

internal sealed class TestProcessLauncher : IProcessLauncher
{
    public ProcessStartInfo? StartInfo { get; private set; }

    public void Start(ProcessStartInfo startInfo)
    {
        StartInfo = startInfo;
    }
}

internal sealed class ThrowingProcessLauncher(Exception exception) : IProcessLauncher
{
    public void Start(ProcessStartInfo startInfo) => throw exception;
}
