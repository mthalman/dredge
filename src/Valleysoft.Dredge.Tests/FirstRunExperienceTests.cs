namespace Valleysoft.Dredge.Tests;

public sealed class FirstRunExperienceTests
{
    [Theory]
    [InlineData()]
    [InlineData("--help")]
    [InlineData("-h")]
    [InlineData("-?")]
    public void ShowIfNeeded_WelcomeInvocationShowsLogoAndGuidance(params string[] args)
    {
        string stateDirectory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        using StringWriter output = new();

        try
        {
            FirstRunExperience.ShowIfNeeded(args, output, stateDirectory);

            Assert.Contains("____  ____  _____", output.ToString());
            Assert.Contains("Welcome to Dredge", output.ToString());
            Assert.Contains("dredge image explore <image>", output.ToString());
            Assert.True(File.Exists(Path.Combine(stateDirectory, ".first-run-complete")));
        }
        finally
        {
            if (Directory.Exists(stateDirectory))
            {
                Directory.Delete(stateDirectory, recursive: true);
            }
        }
    }

    [Fact]
    public void ShowIfNeeded_ShowsWelcomeOnlyOnce()
    {
        string stateDirectory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        using StringWriter firstOutput = new();
        using StringWriter secondOutput = new();

        try
        {
            FirstRunExperience.ShowIfNeeded([], firstOutput, stateDirectory);
            FirstRunExperience.ShowIfNeeded(["--help"], secondOutput, stateDirectory);

            Assert.NotEmpty(firstOutput.ToString());
            Assert.Empty(secondOutput.ToString());
        }
        finally
        {
            if (Directory.Exists(stateDirectory))
            {
                Directory.Delete(stateDirectory, recursive: true);
            }
        }
    }

    [Fact]
    public void ShowIfNeeded_DoesNotShowOrMarkRegularCommand()
    {
        string stateDirectory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        using StringWriter output = new();

        FirstRunExperience.ShowIfNeeded(["image", "inspect"], output, stateDirectory);

        Assert.Empty(output.ToString());
        Assert.False(Directory.Exists(stateDirectory));
    }
}
