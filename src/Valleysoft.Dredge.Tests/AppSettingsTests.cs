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
}
