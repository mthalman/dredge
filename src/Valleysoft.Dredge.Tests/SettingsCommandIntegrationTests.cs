using System.CommandLine;
using System.Diagnostics;
using System.Text.Json.Nodes;
using ClearCacheCommand = Valleysoft.Dredge.Commands.Settings.ClearCacheCommand;
using SettingsGetCommand = Valleysoft.Dredge.Commands.Settings.GetCommand;
using SettingsOpenCommand = Valleysoft.Dredge.Commands.Settings.OpenCommand;
using SettingsSetCommand = Valleysoft.Dredge.Commands.Settings.SetCommand;

namespace Valleysoft.Dredge.Tests;

internal sealed class SettingsCommandIntegrationScenarios
{
    public async Task SetAndGetCommands_RoundTripIsolatedSettings()
    {
        string tempRoot = GetTempPath();
        string settingsPath = Path.Combine(tempRoot, "settings.json");
        using StringWriter output = new();
        try
        {
            int setExitCode = await InvokeAsync(
                new SettingsSetCommand(new AppSettingsStore(settingsPath)),
                "platform.arch",
                "arm64");
            int getExitCode = await InvokeAsync(
                new SettingsGetCommand(new AppSettingsStore(settingsPath), output),
                "platform.arch");

            Assert.Equal(0, setExitCode);
            Assert.Equal(0, getExitCode);
            Assert.Equal("arm64", output.ToString().Trim());
            JsonObject persisted = JsonNode.Parse(await File.ReadAllTextAsync(
                settingsPath,
                TestContext.Current.CancellationToken))!.AsObject();
            Assert.Equal("arm64", persisted["platform"]?["arch"]?.GetValue<string>());
        }
        finally
        {
            DeleteDirectory(tempRoot);
        }
    }

    public async Task ClearCacheCommand_DeletesIsolatedCache()
    {
        string cachePath = GetTempPath();
        Directory.CreateDirectory(Path.Combine(cachePath, "nested"));
        await File.WriteAllBytesAsync(
            Path.Combine(cachePath, "nested", "cache.bin"),
            [1, 2, 3, 4],
            TestContext.Current.CancellationToken);
        using StringWriter output = new();

        int exitCode = await InvokeAsync(
            new ClearCacheCommand(
                new TestDredgePathProvider(cachePath),
                output,
                new TestProcessTerminator()));

        Assert.Equal(0, exitCode);
        Assert.False(Directory.Exists(cachePath));
        Assert.Contains("4 bytes deleted", output.ToString());
    }

    public async Task ClearCacheCommand_WhenCacheDoesNotExistReportsNoWork()
    {
        string cachePath = GetTempPath();
        using StringWriter output = new();

        int exitCode = await InvokeAsync(
            new ClearCacheCommand(
                new TestDredgePathProvider(cachePath),
                output,
                new TestProcessTerminator()));

        Assert.Equal(0, exitCode);
        Assert.False(Directory.Exists(cachePath));
        Assert.Equal(
            $"Nothing to do. Cache directory '{cachePath}' does not exist.{Environment.NewLine}",
            output.ToString());
    }

    public async Task OpenCommand_CreatesAndLaunchesIsolatedSettingsFile()
    {
        string tempRoot = GetTempPath();
        string settingsPath = Path.Combine(tempRoot, "settings.json");
        ProcessStartInfo? launched = null;
        using StringWriter output = new();
        try
        {
            TestProcessLauncher processLauncher = new();
            SettingsOpenCommand command = new(
                new AppSettingsStore(settingsPath),
                processLauncher,
                new TestProcessTerminator(),
                output);

            int exitCode = await InvokeAsync(command);
            launched = processLauncher.StartInfo;

            Assert.Equal(0, exitCode);
            Assert.True(File.Exists(settingsPath));
            Assert.Equal(settingsPath, launched?.FileName);
            Assert.True(launched?.UseShellExecute);
            Assert.Empty(output.ToString());
        }
        finally
        {
            DeleteDirectory(tempRoot);
        }
    }

    public async Task OpenCommand_WhenShellLaunchFailsWritesSettingsPath()
    {
        string tempRoot = GetTempPath();
        string settingsPath = Path.Combine(tempRoot, "settings.json");
        using StringWriter output = new();
        try
        {
            SettingsOpenCommand command = new(
                new AppSettingsStore(settingsPath),
                new ThrowingProcessLauncher(new InvalidOperationException("No shell association.")),
                new TestProcessTerminator(),
                output);

            int exitCode = await InvokeAsync(command);

            Assert.Equal(0, exitCode);
            Assert.True(File.Exists(settingsPath));
            Assert.Equal($"{settingsPath}{Environment.NewLine}", output.ToString());
        }
        finally
        {
            DeleteDirectory(tempRoot);
        }
    }

    private static Task<int> InvokeAsync(Command command, params string[] args) =>
        command
            .Parse(args)
            .InvokeAsync(
                new InvocationConfiguration(),
                TestContext.Current.CancellationToken);

    private static string GetTempPath() =>
        Path.Combine(Path.GetTempPath(), $"dredge-settings-{Guid.NewGuid():N}");

    private static void DeleteDirectory(string path)
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }
    }
}

[Trait("Category", "Integration")]
public sealed class SettingsRoundTripIntegrationTests
{
    [Fact]
    public Task SetAndGetCommands_RoundTripIsolatedSettings() =>
        new SettingsCommandIntegrationScenarios().SetAndGetCommands_RoundTripIsolatedSettings();
}

[Trait("Category", "Integration")]
public sealed class SettingsClearCacheIntegrationTests
{
    [Fact]
    public Task ClearCacheCommand_DeletesIsolatedCache() =>
        new SettingsCommandIntegrationScenarios().ClearCacheCommand_DeletesIsolatedCache();
}

[Trait("Category", "Integration")]
public sealed class SettingsClearCacheMissingIntegrationTests
{
    [Fact]
    public Task ClearCacheCommand_WhenCacheDoesNotExistReportsNoWork() =>
        new SettingsCommandIntegrationScenarios().ClearCacheCommand_WhenCacheDoesNotExistReportsNoWork();
}

[Trait("Category", "Integration")]
public sealed class SettingsOpenIntegrationTests
{
    [Fact]
    public Task OpenCommand_CreatesAndLaunchesIsolatedSettingsFile() =>
        new SettingsCommandIntegrationScenarios().OpenCommand_CreatesAndLaunchesIsolatedSettingsFile();
}

[Trait("Category", "Integration")]
public sealed class SettingsOpenFallbackIntegrationTests
{
    [Fact]
    public Task OpenCommand_WhenShellLaunchFailsWritesSettingsPath() =>
        new SettingsCommandIntegrationScenarios().OpenCommand_WhenShellLaunchFailsWritesSettingsPath();
}
