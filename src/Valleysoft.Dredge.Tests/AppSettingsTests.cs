namespace Valleysoft.Dredge.Tests;

using Newtonsoft.Json;

public class AppSettingsTests
{
    [Fact]
    public async Task ConcurrentLoadCreatesValidSettingsFile()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        string settingsPath = Path.Combine(tempDir, "settings.json");
        TaskCompletionSource start = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<AppSettings>[] loadTasks = Enumerable.Range(0, 32)
            .Select(_ => Task.Run(async () =>
            {
                await start.Task;
                return AppSettings.Load(settingsPath);
            }))
            .ToArray();

        try
        {
            start.SetResult();
            AppSettings[] settings = await Task.WhenAll(loadTasks);

            Assert.All(settings, value => Assert.NotNull(value.Platform));
            string persistedContent = await File.ReadAllTextAsync(
                settingsPath, TestContext.Current.CancellationToken);
            AppSettings? persistedSettings =
                JsonConvert.DeserializeObject<AppSettings>(persistedContent);
            Assert.NotNull(persistedSettings);
            Assert.NotNull(persistedSettings.Platform);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }

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
