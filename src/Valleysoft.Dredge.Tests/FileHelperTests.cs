namespace Valleysoft.Dredge.Tests;

public class FileHelperTests
{
    [Fact]
    public void CopyDirectory_CopiesNestedFiles()
    {
        string root = Path.Combine(Path.GetTempPath(), $"dredge-tests-{Guid.NewGuid():N}");
        string source = Path.Combine(root, "source");
        string destination = Path.Combine(root, "destination");
        Directory.CreateDirectory(Path.Combine(source, "nested"));
        File.WriteAllText(Path.Combine(source, "root.txt"), "root");
        File.WriteAllText(Path.Combine(source, "nested", "child.txt"), "child");

        try
        {
            FileHelper.CopyDirectory(source, destination);

            Assert.Equal("root", File.ReadAllText(Path.Combine(destination, "root.txt")));
            Assert.Equal("child", File.ReadAllText(Path.Combine(destination, "nested", "child.txt")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void CopyDirectory_WhenSourceDoesNotExist_Throws()
    {
        string source = Path.Combine(Path.GetTempPath(), $"dredge-missing-{Guid.NewGuid():N}");

        DirectoryNotFoundException exception = Assert.Throws<DirectoryNotFoundException>(
            () => FileHelper.CopyDirectory(source, Path.Combine(source, "destination")));

        Assert.Contains(Path.GetFullPath(source), exception.Message);
    }
}
