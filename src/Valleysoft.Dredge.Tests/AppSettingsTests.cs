namespace Valleysoft.Dredge.Tests;

public class AppSettingsTests
{
    [Fact]
    public void GeneratedPropertyAccessors_SetAndGetNestedValues()
    {
        AppSettings settings = CreateSettings();

        settings.SetProperty(new Queue<string>(["platform", "arch"]), "arm64");
        settings.SetProperty(new Queue<string>(["fileCompareTool", "exePath"]), "compare.exe");

        Assert.Equal("arm64", settings.GetProperty(new Queue<string>(["platform", "arch"])));
        Assert.Equal("compare.exe", settings.GetProperty(new Queue<string>(["fileCompareTool", "exePath"])));
    }

    [Theory]
    [InlineData()]
    [InlineData("unknown")]
    [InlineData("platform", "unknown")]
    [InlineData("platform", "arch", "extra")]
    public void GeneratedPropertyAccessors_WhenPathIsInvalid_Throw(params string[] path)
    {
        AppSettings settings = CreateSettings();

        Assert.Throws<ArgumentException>(
            () => settings.GetProperty(new Queue<string>(path)));
        Assert.Throws<ArgumentException>(
            () => settings.SetProperty(new Queue<string>(path), "value"));
    }

    private static AppSettings CreateSettings() =>
        (AppSettings)Activator.CreateInstance(typeof(AppSettings), nonPublic: true)!;
}
