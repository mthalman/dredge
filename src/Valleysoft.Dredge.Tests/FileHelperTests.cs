namespace Valleysoft.Dredge.Tests;

public class FileHelperTests
{
    [Fact]
    public async Task CopyFileAsyncPreservesMetadata()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        string sourcePath = Path.Combine(tempDir, "source.txt");
        string destinationPath = Path.Combine(tempDir, "destination.txt");
        DateTime lastWriteTimeUtc = new(2020, 1, 2, 3, 4, 6, DateTimeKind.Utc);

        Directory.CreateDirectory(tempDir);

        try
        {
            await File.WriteAllTextAsync(
                sourcePath, "content", TestContext.Current.CancellationToken);
            File.SetLastWriteTimeUtc(sourcePath, lastWriteTimeUtc);
            File.SetAttributes(sourcePath, FileAttributes.ReadOnly);

            await FileHelper.CopyFileAsync(
                new FileInfo(sourcePath),
                destinationPath,
                overwrite: false,
                TestContext.Current.CancellationToken);

            Assert.Equal("content", await File.ReadAllTextAsync(
                destinationPath, TestContext.Current.CancellationToken));
            Assert.Equal(lastWriteTimeUtc, File.GetLastWriteTimeUtc(destinationPath));
            Assert.Equal(FileAttributes.ReadOnly, File.GetAttributes(destinationPath) & FileAttributes.ReadOnly);
        }
        finally
        {
            if (File.Exists(sourcePath))
            {
                File.SetAttributes(sourcePath, FileAttributes.Normal);
            }
            if (File.Exists(destinationPath))
            {
                File.SetAttributes(destinationPath, FileAttributes.Normal);
            }
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task CopyFileAsyncHonorsOverwrite()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        string sourcePath = Path.Combine(tempDir, "source.txt");
        string destinationPath = Path.Combine(tempDir, "destination.txt");

        Directory.CreateDirectory(tempDir);

        try
        {
            await File.WriteAllTextAsync(
                sourcePath, "new", TestContext.Current.CancellationToken);
            await File.WriteAllTextAsync(
                destinationPath, "old", TestContext.Current.CancellationToken);

            await Assert.ThrowsAsync<IOException>(() =>
                FileHelper.CopyFileAsync(
                    new FileInfo(sourcePath),
                    destinationPath,
                    overwrite: false,
                    TestContext.Current.CancellationToken));
            Assert.Equal("old", await File.ReadAllTextAsync(
                destinationPath, TestContext.Current.CancellationToken));

            await FileHelper.CopyFileAsync(
                new FileInfo(sourcePath),
                destinationPath,
                overwrite: true,
                TestContext.Current.CancellationToken);
            Assert.Equal("new", await File.ReadAllTextAsync(
                destinationPath, TestContext.Current.CancellationToken));
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task CopyFileAsyncPreservesUnixFileMode()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        string tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        string sourcePath = Path.Combine(tempDir, "source");
        string destinationPath = Path.Combine(tempDir, "destination");
        UnixFileMode mode =
            UnixFileMode.UserRead |
            UnixFileMode.UserExecute |
            UnixFileMode.GroupRead |
            UnixFileMode.OtherRead;

        Directory.CreateDirectory(tempDir);

        try
        {
            await File.WriteAllTextAsync(
                sourcePath, "content", TestContext.Current.CancellationToken);
            File.SetUnixFileMode(sourcePath, mode);

            await FileHelper.CopyFileAsync(
                new FileInfo(sourcePath),
                destinationPath,
                overwrite: false,
                TestContext.Current.CancellationToken);

            Assert.Equal(mode, File.GetUnixFileMode(destinationPath));
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }
}
