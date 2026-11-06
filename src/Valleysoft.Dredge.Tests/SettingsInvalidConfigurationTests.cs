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
        AssertInvalidSettingsCommand(commandName, content, (settingsPath, error) =>
        {
            Assert.Contains(settingsPath, error);
            Assert.Contains("JSON object", error);
            Assert.Contains("{} for defaults", error);
        });
    }

    [Theory]
    [InlineData("get")]
    [InlineData("set")]
    public void Command_MalformedSettingsFailsWithoutChangingFile(string commandName)
    {
        AssertInvalidSettingsCommand(commandName, "{ invalid json", (_, error) =>
            Assert.NotEmpty(error));
    }

    private static void AssertInvalidSettingsCommand(
        string commandName, string content, Action<string, string> assertError)
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
            Assert.DoesNotContain("Object reference", error.ToString());
            Assert.Equal(originalBytes, File.ReadAllBytes(settingsPath));
            assertError(settingsPath, error.ToString());
        }
        finally
        {
            File.Delete(settingsPath);
        }
    }
}
