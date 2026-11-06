using System.Formats.Tar;
using System.IO.Compression;
using System.Text;
using static Valleysoft.Dredge.Tests.WindowsImageTestData;

namespace Valleysoft.Dredge.Tests;

public sealed class WindowsFileSystemRoundTwoTests : IAsyncDisposable
{
    private readonly LayerCacheTestContext cache = new();
    private CancellationToken Token => TestContext.Current.CancellationToken;
    public ValueTask DisposeAsync() => cache.DisposeAsync();

    [Theory]
    [InlineData("windows", @"C:\")]
    [InlineData("windows", "/")]
    [InlineData("windows", @"\")]
    [InlineData("linux", "/")]
    public async Task RootLinksResolveParentComponentsForListingAndExtraction(string os, string target)
    {
        string prefix = os == "windows" ? "Files/" : "";
        Mock<IDockerRegistryClient> client = Client(os, "", Layer(
            File(prefix + "value", "root"), File(prefix + "Dir/child", "child"),
            Link(prefix + "App/root", target)));
        await using ImageFileSystem files = await ImageFileSystem.CreateAsync(
            client.Object, Name, new(), Token, cache.Store, requireLayerIndexes: true);
        Assert.Equal("App/root/value", Assert.Single(files.List("App/root/value", false, false)).Path);
        Assert.Equal("App/root/Dir/child", Assert.Single(files.List("App/root/Dir", false, false)).Path);
        string output = Path.Combine(cache.Root, "output");
        await files.ExtractAsync("App/root/value", output, Token);
        Assert.Equal("root", await System.IO.File.ReadAllTextAsync(output, Token));
        using MemoryStream content = new();
        await files.CopyFileToAsync("App/root/value", content, Token);
        Assert.Equal("root", Encoding.UTF8.GetString(content.ToArray()));
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            files.CopyFileToAsync("App/root", new MemoryStream(), Token));
        Assert.DoesNotContain(files.List(null, true, false), static entry => entry.Path.Length == 0);
    }

    [Fact]
    public async Task HardLinkTargetsCanTraverseAnImplicitRootDirectory()
    {
        Mock<IDockerRegistryClient> client = Client(Layer(
            File("Files/value", "original"), Link("Files/App/root", @"C:\"),
            Link("Files/saved", "Files/App/root/value", hard: true)));
        await using ImageFileSystem files = await ImageFileSystem.CreateAsync(
            client.Object, Name, new(), Token, cache.Store);
        using MemoryStream content = new();
        await files.CopyFileToAsync("saved", content, Token);
        Assert.Equal("original", Encoding.UTF8.GetString(content.ToArray()));
    }

    [Fact]
    public void LinuxExtractionRecognizesRootLinksAsDirectoryLinks()
    {
        Dictionary<string, ImageFileSystemEntry> entries = new(StringComparer.Ordinal)
        {
            ["root"] = new()
            {
                Path = "root", Type = ImageFileType.SymbolicLink, LinkTarget = "/",
                IntroducedLayer = new(0, "layer")
            }
        };
        ExtractionPlanner planner = new(new(entries), entries);
        ExtractionPlan plan = planner.CreatePlan("root", Path.Combine(cache.Root, "output"),
            extractingRoot: false, entries["root"], entries);
        Assert.True(plan.SymbolicLinkDirectoryTargets["root"]);
    }

    [Theory]
    [InlineData("Files/App./.wh.value")]
    [InlineData("Files/App /.wh.value")]
    [InlineData("Files/App/.wh.value.")]
    [InlineData("Files/App/.wh.value ")]
    [InlineData("Files/App./.wh..wh..opq")]
    [InlineData("Files/App /.wh..wh..opq")]
    public async Task WhiteoutPathsUseWindowsValidation(string marker)
    {
        Mock<IDockerRegistryClient> client = Client(
            Layer(File("Files/App/value", "lower")),
            Layer(File(marker, "")));
        await Assert.ThrowsAsync<NotSupportedException>(() => ImageFileSystem.CreateAsync(
            client.Object, Name, new(), Token, cache.Store));
    }

    [Theory]
    [InlineData("MSWINDOWS.fileattr", false)]
    [InlineData("MSWINDOWS.fileattr", true)]
    [InlineData("MSWINDOWS.mountpoint", false)]
    [InlineData("MSWINDOWS.rawsd", false)]
    [InlineData("MSWINDOWS.unknown", false)]
    public async Task EmptyLocalPaxValuesDeleteAttributesWithoutGlobalDefaults(string key, bool global)
    {
        using MemoryStream blob = new();
        using (GZipStream gzip = new(blob, CompressionMode.Compress, leaveOpen: true))
        using (TarWriter writer = new(gzip, TarEntryFormat.Pax, leaveOpen: true))
        {
            if (global)
            {
                writer.WriteEntry(new PaxGlobalExtendedAttributesTarEntry(
                    new Dictionary<string, string> { ["comment"] = "metadata" }));
            }
            PaxTarEntry entry = File("Files/value", "value", new() { [key] = "" });
            writer.WriteEntry(entry);
            entry.DataStream!.Dispose();
        }
        Mock<IDockerRegistryClient> client = Client(blob.ToArray());
        await using ImageFileSystem files = await ImageFileSystem.CreateAsync(
            client.Object, Name, new(), Token, cache.Store);
        ImageFileSystemEntry result = Assert.Single(files.List("value", false, false));
        Assert.Equal(ImageFileType.File, result.Type);
        Assert.Null(result.Windows!.Attributes);
        Assert.Null(result.Windows.UnsupportedReason);
        Assert.False(result.Windows.IsMountPoint);
        Assert.False(result.Windows.HasSecurityDescriptor);
        using MemoryStream content = new();
        await files.CopyFileToAsync("value", content, Token);
        Assert.Equal("value", Encoding.UTF8.GetString(content.ToArray()));
    }

    [Fact]
    public void WindowsExtractionOmitsUnusedLinkPlanning()
    {
        Dictionary<string, ImageFileSystemEntry> entries = new(StringComparer.OrdinalIgnoreCase)
        {
            ["saved"] = new()
            {
                Path = "saved", Type = ImageFileType.HardLink, LinkTarget = "original",
                IntroducedLayer = new(0, "layer"), ContentPath = "original"
            }
        };
        ExtractionPlanner planner = new(new(entries, ImageFileSystemSemantics.Windows),
            entries, ImageFileSystemSemantics.Windows);
        ExtractionPlan plan = planner.CreatePlan("saved", Path.Combine(cache.Root, "output"),
            extractingRoot: false, entries["saved"], entries);
        Assert.Empty(plan.HardLinkTargets);
        Assert.Empty(plan.PreservableHardLinks);
        Assert.Empty(plan.SymbolicLinkDirectoryTargets);
        Assert.Equal("original", Assert.Single(ExtractionPlanner.GetContentExtractionRequests(plan)).Entry.ContentPath);
    }

    [Fact]
    public async Task DiagnosticCountsMatchIndexedSnapshotAndRestoredWarningsWithoutAllocating()
    {
        Mock<IDockerRegistryClient> client = Client(
            Layer(File("Files/value", "value"), File("Hives/Software", "hive")),
            Layer(File("Files/other", "other")));
        await using ImageFileSystem indexed = await ImageFileSystem.CreateAsync(
            client.Object, Name, new(), Token, cache.Store, requireLayerIndexes: true);
        await using ImageFileSystem snapshot = indexed.CreateLayerSnapshot(0, Token);
        await using ImageFileSystem restored = await ImageFileSystem.CreateAsync(
            client.Object, Name, new(), Token, cache.Store);
        foreach (ImageFileSystem files in new[] { indexed, snapshot, restored })
        {
            int expected = files.Diagnostics.Count;
            Assert.True(expected > 0);
            Assert.Equal(expected, files.DiagnosticCount);
            long before = GC.GetAllocatedBytesForCurrentThread();
            int count = files.DiagnosticCount;
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            Assert.Equal(expected, count);
            Assert.Equal(0, allocated);
        }
    }

    [Fact]
    public async Task RepeatedExactSpellingsStillReplaceEntriesWithoutCaseCollisions()
    {
        Mock<IDockerRegistryClient> client = Client(Layer(
            File("Files/value", "first"), File("Files/value", "second")));
        await using ImageFileSystem files = await ImageFileSystem.CreateAsync(
            client.Object, Name, new(), Token, cache.Store);
        Assert.Single(files.List(null, true, false));
        using MemoryStream content = new();
        await files.CopyFileToAsync("value", content, Token);
        Assert.Equal("second", Encoding.UTF8.GetString(content.ToArray()));
    }

    [Theory]
    [InlineData("windows-v1.view")]
    [InlineData("windows-v2.view")]
    [InlineData("windows-v3.view")]
    [InlineData("windows-v4.view")]
    [InlineData("windows-v5.view")]
    [InlineData("windows-v6.view")]
    [InlineData("windows-v7.view")]
    [InlineData("windows-v8.view")]
    [InlineData("windows-v9.view")]
    [InlineData("windows-v10.view")]
    [InlineData("windows-v11.view")]
    [InlineData("windows-v12.view")]
    public async Task OldWindowsViewsAreNotRestoredButVerifiedBlobsRemainReusable(string previousSuffix)
    {
        byte[] layer = Layer(File("Files/value", "value"));
        Mock<IDockerRegistryClient> client = Client(layer);
        await using (ImageFileSystem initial = await ImageFileSystem.CreateAsync(
            client.Object, Name, new(), Token, cache.Store))
        {
            Assert.Single(initial.List(null, true, false));
        }
        string blob = cache.Store.GetBlobPath(LayerCacheTestContext.Digest(layer));
        string directory = Path.GetDirectoryName(blob)!;
        string current = Assert.Single(Directory.GetFiles(directory, "*.view"));
        string previous = Path.ChangeExtension(Path.ChangeExtension(current, null), previousSuffix);
        System.IO.File.Move(current, previous);
        await using ImageFileSystem rebuilt = await ImageFileSystem.CreateAsync(
            client.Object, Name, new(), Token, cache.Store);
        Assert.Single(rebuilt.Analyze().Layers);
        Assert.True(System.IO.File.Exists(previous));
        Assert.Equal(2, Directory.GetFiles(directory, "*.view").Length);
        client.Verify(item => item.Blobs.GetRangeAsync(Name.Repo, LayerCacheTestContext.Digest(layer),
            0, It.IsAny<long?>(), It.IsAny<CancellationToken>()), Times.Once);
    }
}
