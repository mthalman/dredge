namespace Valleysoft.Dredge.Tests;

using System.Formats.Tar;
using System.IO.Compression;
using System.Text;

public sealed class ExtractionCaseSensitivityTests : IAsyncDisposable
{
    private readonly string root = Path.Combine(
        Directory.GetCurrentDirectory(), $".extraction-case-{Guid.NewGuid():N}");
    private readonly LayerStore store;
    private CancellationToken Token => TestContext.Current.CancellationToken;

    public ExtractionCaseSensitivityTests()
    {
        Directory.CreateDirectory(root);
        store = new(Path.Combine(root, "cache"));
    }

    public async ValueTask DisposeAsync()
    {
        await store.DisposeAsync();
        Directory.Delete(root, recursive: true);
    }

    [Fact]
    public void PlannerDoesNotGuessDestinationCaseSensitivityFromOperatingSystem()
    {
        Dictionary<string, ImageFileSystemEntry> entries = new(StringComparer.Ordinal)
        {
            ["A"] = Entry("A"),
            ["a"] = Entry("a")
        };
        ExtractionPlanner planner = new(new(entries), entries);
        string destination = Path.Combine(root, "output");

        ExtractionPlan plan = planner.CreatePlan("", destination, true, null, entries);

        Assert.Equal(2, plan.Destinations.Count);
        Assert.False(Directory.Exists(destination));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExtractionPreservesCaseDistinctPathsOrRejectsWithoutMerging(bool directories)
    {
        string first = directories ? "A/first" : "A";
        string second = directories ? "a/second" : "a";
        Mock<IDockerRegistryClient> client = WindowsImageTestData.Client("linux", "", CreateLayer(first, second));
        await using ImageFileSystem files = await ImageFileSystem.CreateAsync(
            client.Object, WindowsImageTestData.Name, new(), Token, store);
        string destination = Path.Combine(root, "output");
        string probe = Path.Combine(root, "case-probe");
        await File.WriteAllTextAsync(probe, "", Token);
        bool caseSensitive = !File.Exists(Path.Combine(root, "CASE-PROBE"));
        File.Delete(probe);

        if (caseSensitive)
        {
            await files.ExtractAsync("", destination, Token);
            Assert.Equal("first", await File.ReadAllTextAsync(
                Path.Combine(destination, first), Token));
            Assert.Equal("second", await File.ReadAllTextAsync(
                Path.Combine(destination, second), Token));
            Assert.Equal(2, Directory.GetFileSystemEntries(destination).Length);
            await Assert.ThrowsAsync<IOException>(() => files.ExtractAsync("", destination, Token));
            Assert.Equal("first", await File.ReadAllTextAsync(
                Path.Combine(destination, first), Token));
        }
        else
        {
            await Assert.ThrowsAsync<IOException>(() => files.ExtractAsync("", destination, Token));
            Assert.False(Directory.Exists(destination));
        }
    }

    [Fact]
    public void UnsupportedSubtreeIsRejectedBeforeOutputCreation()
    {
        Dictionary<string, ImageFileSystemEntry> entries = new(StringComparer.Ordinal)
        {
            ["A"] = Entry("A"),
            ["a"] = Entry("a"),
            ["unsupported"] = Entry("unsupported") with { Type = ImageFileType.Other }
        };
        ExtractionPlanner planner = new(new(entries), entries);
        string destination = Path.Combine(root, "missing", "output");

        Assert.Throws<NotSupportedException>(() =>
            planner.CreatePlan("", destination, true, null, entries));

        Assert.False(Directory.Exists(Path.GetDirectoryName(destination)));
    }

    private static ImageFileSystemEntry Entry(string path) => new()
    {
        Path = path,
        Type = ImageFileType.File,
        IntroducedLayer = new(0, "sha256:layer")
    };

    private static byte[] CreateLayer(string first, string second)
    {
        using MemoryStream buffer = new();
        using (GZipStream gzip = new(buffer, CompressionMode.Compress, leaveOpen: true))
        using (TarWriter writer = new(gzip, leaveOpen: true))
        {
            foreach ((string path, string content) in new[] { (first, "first"), (second, "second") })
            {
                using MemoryStream data = new(Encoding.UTF8.GetBytes(content));
                writer.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, path)
                {
                    DataStream = data,
                    Mode = UnixFileMode.UserRead | UnixFileMode.UserWrite
                });
            }
        }
        return buffer.ToArray();
    }
}
