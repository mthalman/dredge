using System.CommandLine;
using SettingsGetCommand = Valleysoft.Dredge.Commands.Settings.GetCommand;
using SettingsSetCommand = Valleysoft.Dredge.Commands.Settings.SetCommand;

namespace Valleysoft.Dredge.Tests;

public class SettingsInvalidConfigurationTests
{
    [Theory]
    [InlineData("get", "")]
    [InlineData("get", " ")]
    [InlineData("get", "\t\r\n ")]
    [InlineData("get", "null")]
    [InlineData("set", "")]
    [InlineData("set", " ")]
    [InlineData("set", "\t\r\n ")]
    [InlineData("set", "null")]
    public void Command_EmptyOrNullSettingsFailsWithActionableErrorAndPreservesFile(
        string commandName, string content)
    {
        string settingsPath = Path.GetTempFileName();
        using StringWriter output = new();
        using StringWriter error = new();
        try
        {
            File.WriteAllText(settingsPath, content);
            byte[] originalBytes = File.ReadAllBytes(settingsPath);
            AppSettingsStore store = new(settingsPath);
            Command command = commandName == "get"
                ? new SettingsGetCommand(store, output)
                : new SettingsSetCommand(store);
            string[] args = commandName == "get"
                ? ["platform.arch"]
                : ["platform.arch", "arm64"];

            int exitCode = CommandHelper.InvokeRootCommand(
                command.Parse(args),
                new InvocationConfiguration { Output = output, Error = error });

            Assert.Equal(1, exitCode);
            Assert.Empty(output.ToString());
            Assert.Contains(settingsPath, error.ToString());
            Assert.Contains("JSON object", error.ToString());
            Assert.Contains("{} for defaults", error.ToString());
            Assert.DoesNotContain("Object reference", error.ToString());
            Assert.Equal(originalBytes, File.ReadAllBytes(settingsPath));
        }
        finally
        {
            File.Delete(settingsPath);
        }
    }

    [Theory]
    [InlineData("get")]
    [InlineData("set")]
    public void Command_MalformedSettingsFailsWithoutChangingFile(string commandName)
    {
        string settingsPath = Path.GetTempFileName();
        using StringWriter output = new();
        using StringWriter error = new();
        try
        {
            const string content = "{ invalid json";
            File.WriteAllText(settingsPath, content);
            AppSettingsStore store = new(settingsPath);
            Command command = commandName == "get"
                ? new SettingsGetCommand(store, output)
                : new SettingsSetCommand(store);
            string[] args = commandName == "get"
                ? ["platform.arch"]
                : ["platform.arch", "arm64"];

            int exitCode = CommandHelper.InvokeRootCommand(
                command.Parse(args),
                new InvocationConfiguration { Output = output, Error = error });

            Assert.Equal(1, exitCode);
            Assert.Empty(output.ToString());
            Assert.NotEmpty(error.ToString());
            Assert.DoesNotContain("Object reference", error.ToString());
            Assert.Equal(content, File.ReadAllText(settingsPath));
        }
        finally
        {
            File.Delete(settingsPath);
        }
    }
}
