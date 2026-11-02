using static Valleysoft.Dredge.Tests.WindowsImageTestData;

namespace Valleysoft.Dredge.Tests;

public sealed class WindowsPathRoundThreeTests : IAsyncDisposable
{
    private readonly LayerCacheTestContext cache = new();
    private CancellationToken Token => TestContext.Current.CancellationToken;
    public ValueTask DisposeAsync() => cache.DisposeAsync();

    [Theory]
    [InlineData(@"\??\D:\outside")]
    [InlineData(@"\??\C:\value")]
    [InlineData(@"\Device\HarddiskVolume1\outside")]
    [InlineData(@"\device\HarddiskVolume1\outside")]
    [InlineData(@"\DosDevices\C:\value")]
    [InlineData(@"\GLOBAL??\C:\value")]
    [InlineData(@"\GLOBALROOT\Device\HarddiskVolume1\outside")]
    [InlineData(@"\SystemRoot\value")]
    public async Task NativeNamespaceLinksAreVisiblyUnsupported(string target)
    {
        Mock<IDockerRegistryClient> client = Client(Layer(
            File("Files/value", "value"), Link("Files/alias", target, mountPoint: true)));
        await using ImageFileSystem files = await ImageFileSystem.CreateAsync(
            client.Object, Name, new(), Token, cache.Store);
        ImageFileSystemEntry link = Assert.Single(files.List("alias", false, false));
        Assert.Equal(ImageFileType.Other, link.Type);
        Assert.Contains("device", link.Windows!.UnsupportedReason, StringComparison.OrdinalIgnoreCase);
        await Assert.ThrowsAsync<NotSupportedException>(() =>
            files.CopyFileToAsync("alias", new MemoryStream(), Token));
        Assert.Contains(files.Diagnostics, static warning => warning.Path == "alias");
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            ImageFileSystem.CreateAsync(client.Object, Name, new(), Token, cache.Store, contentPath: target));
    }

    [Theory]
    [InlineData(@"\Windows\value")]
    [InlineData(@"C:\Windows\value")]
    public async Task OrdinaryRootedWindowsLinksRemainSupported(string target)
    {
        Mock<IDockerRegistryClient> client = Client(Layer(
            File("Files/Windows/value", "value"), Link("Files/alias", target)));
        await using ImageFileSystem files = await ImageFileSystem.CreateAsync(
            client.Object, Name, new(), Token, cache.Store);
        Assert.Equal(ImageFileType.SymbolicLink, Assert.Single(files.List("alias", false, false)).Type);
        using MemoryStream content = new();
        await files.CopyFileToAsync("alias", content, Token);
        Assert.Equal("value"u8.ToArray(), content.ToArray());
    }

    [Theory]
    [InlineData(@"Device\value", "Device/value")]
    [InlineData(@"GLOBALROOT\value", "GLOBALROOT/value")]
    [InlineData(@"C:\Device\value", "Device/value")]
    [InlineData(@"\Windows\value", "Windows/value")]
    [InlineData("", "")]
    public void OrdinaryPathsAreNotConfusedWithNativeNamespaces(string path, string expected) =>
        Assert.Equal(expected, ImageFileSystemSemantics.Windows.NormalizeRequested(path));

    [Fact]
    public void AlternateStreamPreflightDoesNotAllocatePerUnrelatedPath()
    {
        Dictionary<string, ImageFileSystemEntry> entries = new(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < 20_000; i++)
        {
            string path = $"value{i}";
            entries.Add(path, new() { Path = path, Type = ImageFileType.File, IntroducedLayer = new(0, "layer") });
        }
        ExtractionPlanner planner = new(new(entries, ImageFileSystemSemantics.Windows),
            entries, ImageFileSystemSemantics.Windows);
        string destination = Path.Combine(cache.Root, "output");
        planner.CreatePlan("value0", destination, false, entries["value0"], entries);
        long start = GC.GetAllocatedBytesForCurrentThread();
        ExtractionPlan plan = planner.CreatePlan("value0", destination, false, entries["value0"], entries);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - start;
        Assert.True(allocated < 100_000, $"Single-file planning allocated {allocated:N0} bytes.");
        Assert.Single(plan.Entries);
        Assert.False(System.IO.File.Exists(destination));
        entries["VALUE0:secret"] = new()
        {
            Path = "VALUE0:secret", Type = ImageFileType.Other, IntroducedLayer = new(0, "layer")
        };
        Assert.Throws<NotSupportedException>(() =>
            planner.CreatePlan("value0", destination, false, entries["value0"], entries));
    }
}
