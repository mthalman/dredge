using System.Formats.Tar;
using System.IO.Compression;
using Valleysoft.Dredge.Explorer;
using static Valleysoft.Dredge.Tests.WindowsImageTestData;

namespace Valleysoft.Dredge.Tests;

[Collection(ExplorerUiCollection.Name)]
public sealed class WindowsReviewRoundThirteenTests : IAsyncDisposable
{
    private readonly LayerCacheTestContext cache = new();
    private CancellationToken Token => TestContext.Current.CancellationToken;
    public ValueTask DisposeAsync() => cache.DisposeAsync();

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ImplicitDirectoryPromotionStaysLiveInHistoryAndSearch(bool capturedAlias)
    {
        byte[] first = capturedAlias
            ? Layer(File("Files/parent", "OLD"), File("Files/parent:secret", "SECRET"),
                Link("Files/alias", "Files/parent", hard: true))
            : Layer(File("Files/parent", "OLD"));
        Mock<IDockerRegistryClient> client = Client(first,
            Layer(File("Files/PARENT/child", "CHILD")),
            Layer(File("Files/keep", "KEEP")));
        await using ImageFileSystem files = await ImageFileSystem.CreateAsync(
            client.Object, Name, new(), Token, cache.Store, requireLayerIndexes: true);
        ImageAnalysisResult analysis = files.Analyze();
        Assert.Equal(ImageFileType.Directory, analysis.LiveEntries["parent"].Type);
        Assert.DoesNotContain("parent", analysis.LiveContents.Keys);
        ExplorerImage image = new(Name.ToString(), "windows/amd64", "digest", ["first", "second", "third"], [1, 1, 1], null);
        image.SetAnalysis(analysis, ExplorerInsightsResult.Empty);
        Assert.Equal([(0, Change.Added), (1, Change.Added)], image.PathHistory("parent"));
        Node directory = Assert.IsType<Node>(image.Find(image.WholeTree(2)!, "parent"));
        Assert.Equal("PARENT", directory.Name);
        Assert.Equal(Kind.Dir, directory.Kind);
        ExplorerPresenter presenter = new(image, 100, 30);
        Assert.Contains(presenter.SearchResults(new() { SearchQuery = "parent", SearchIncludeDeleted = false }).Hits,
            hit => hit.Dir && hit.Path == "PARENT");
        Assert.Equal(capturedAlias ? 0 : 3, analysis.HiddenBytes);
        if (capturedAlias)
        {
            Assert.True(files.List(null, true, false).Single(entry => entry.Path == "alias").Windows!.HasAlternateStreams);
        }
        await using ImageFileSystem snapshot = files.CreateLayerSnapshot(1, Token);
        Assert.Equal(ImageFileType.Directory, snapshot.List(null, true, false).Single(entry => entry.Path == "PARENT").Type);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WhiteoutedParentsRecreatedByChildrenStayLive(bool explicitDirectory)
    {
        byte[] last = explicitDirectory
            ? Layer(File("Files/PARENT/child/data", "DATA"),
                new PaxTarEntry(TarEntryType.Directory, "Files/parent",
                    new Dictionary<string, string> { ["MSWINDOWS.fileattr"] = "16" }))
            : Layer(File("Files/PARENT/child/data", "DATA"));
        await using ImageFileSystem files = await ImageFileSystem.CreateAsync(
            Client(Layer(File("Files/parent", "OLD")), Layer(File("Files/.wh.parent", "")), last).Object,
            Name, new(), Token, cache.Store, requireLayerIndexes: true);
        ImageAnalysisResult analysis = files.Analyze();
        ScannedEntry parent = analysis.LiveEntries["parent"];
        Assert.Equal(ImageFileType.Directory, parent.Type);
        Assert.Equal(explicitDirectory ? "parent" : "PARENT", parent.Path);
        Assert.Equal(explicitDirectory ? 16u : (uint?)null, parent.Windows!.Attributes);
        Assert.Equal(ImageFileType.Directory, analysis.LiveEntries["parent/child"].Type);
        Assert.Equal(3, analysis.HiddenBytes);
        Assert.Equal(7, analysis.FileBytes);
        ExplorerImage image = new(Name.ToString(), "windows/amd64", "digest", ["first", "second", "third"], [1, 1, 1], null);
        image.SetAnalysis(analysis, ExplorerInsightsResult.Empty);
        Assert.Equal(explicitDirectory ? Change.Modified : Change.Added, image.PathHistory("parent")[^1].Change);
    }

    [Fact]
    public async Task RepeatedLocalOverridesHaveBoundedAllocation()
    {
        Dictionary<string, string> globals = new() { ["MSWINDOWS.fileattr"] = "33" };
        for (int i = 0; i < 4096; i++) globals[$"MSWINDOWS.xattr.key{i}"] = "AQ==";
        using MemoryStream output = new();
        using (GZipStream gzip = new(output, CompressionMode.Compress, leaveOpen: true))
        using (TarWriter tar = new(gzip, TarEntryFormat.Pax, leaveOpen: true))
        {
            tar.WriteEntry(new PaxGlobalExtendedAttributesTarEntry(globals));
            for (int i = 0; i < 2000; i++)
            {
                tar.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, $"Files/file{i}",
                    new Dictionary<string, string> { ["MSWINDOWS.fileattr"] = "32" }));
            }
        }
        byte[] bytes = output.ToArray();
        await ImageLayerScanner.ScanAsync(new MemoryStream(bytes), new(0, "warmup"), Token, ImageFileSystemSemantics.Windows);
        long before = GC.GetTotalAllocatedBytes(precise: true);
        LayerChanges changes = await ImageLayerScanner.ScanAsync(new MemoryStream(bytes),
            new(0, "layer"), Token, ImageFileSystemSemantics.Windows);
        long allocated = GC.GetTotalAllocatedBytes(precise: true) - before;
        Assert.True(allocated < 30_000_000, $"Repeated overrides allocated {allocated:N0} bytes.");
        Assert.All(changes.Entries, entry => Assert.Equal(32u, entry.Windows!.Attributes));
        Assert.All(changes.Entries, entry => Assert.Same(changes.Entries[0].Windows!.Fingerprint, entry.Windows!.Fingerprint));
    }

    [Fact]
    public async Task OverrideCacheSeparatesProfilesAndInvalidatesOnGlobalChanges()
    {
        using MemoryStream output = new();
        using (GZipStream gzip = new(output, CompressionMode.Compress, leaveOpen: true))
        using (TarWriter tar = new(gzip, TarEntryFormat.Pax, leaveOpen: true))
        {
            tar.WriteEntry(new PaxGlobalExtendedAttributesTarEntry(new Dictionary<string, string>
                { ["MSWINDOWS.fileattr"] = "33", ["MSWINDOWS.rawsd"] = "descriptor" }));
            Write("first", "32");
            Write("same", "32");
            Write("other", "34");
            Write("removed", "");
            tar.WriteEntry(new PaxGlobalExtendedAttributesTarEntry(new Dictionary<string, string>
                { ["MSWINDOWS.rawsd"] = "" }));
            Write("changed", "32");
            Write("changedSame", "32");

            void Write(string path, string value) => tar.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, "Files/" + path,
                new Dictionary<string, string> { ["MSWINDOWS.fileattr"] = value }));
        }
        LayerChanges changes = await ImageLayerScanner.ScanAsync(new MemoryStream(output.ToArray()),
            new(0, "layer"), Token, ImageFileSystemSemantics.Windows);
        Assert.Same(changes.Entries[0].Windows!.Fingerprint, changes.Entries[1].Windows!.Fingerprint);
        Assert.Equal(34u, changes.Entries[2].Windows!.Attributes);
        Assert.Null(changes.Entries[3].Windows!.Attributes);
        Assert.NotEqual(changes.Entries[0].Windows!.Fingerprint, changes.Entries[4].Windows!.Fingerprint);
        Assert.False(changes.Entries[4].Windows!.HasSecurityDescriptor);
        Assert.Same(changes.Entries[4].Windows!.Fingerprint, changes.Entries[5].Windows!.Fingerprint);
    }

    [Fact]
    public async Task CachedOverrideStillRejectsDirectoryExtendedAttributes()
    {
        using MemoryStream output = new();
        using (GZipStream gzip = new(output, CompressionMode.Compress, leaveOpen: true))
        using (TarWriter tar = new(gzip, TarEntryFormat.Pax, leaveOpen: true))
        {
            tar.WriteEntry(new PaxGlobalExtendedAttributesTarEntry(new Dictionary<string, string>
                { ["MSWINDOWS.fileattr"] = "33", ["MSWINDOWS.xattr.key"] = "AQ==" }));
            Dictionary<string, string> overrides = new() { ["MSWINDOWS.fileattr"] = "32" };
            tar.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, "Files/file", overrides));
            tar.WriteEntry(new PaxTarEntry(TarEntryType.Directory, "Files/directory", overrides));
        }
        NotSupportedException error = await Assert.ThrowsAsync<NotSupportedException>(() =>
            ImageLayerScanner.ScanAsync(new MemoryStream(output.ToArray()), new(0, "layer"), Token,
                ImageFileSystemSemantics.Windows));
        Assert.Contains("Directory extended attributes", error.Message);
    }

    [Fact]
    public async Task CachedOverrideStillValidatesMountpointRepresentationAndStreamPaths()
    {
        using MemoryStream output = new();
        using (GZipStream gzip = new(output, CompressionMode.Compress, leaveOpen: true))
        using (TarWriter tar = new(gzip, TarEntryFormat.Pax, leaveOpen: true))
        {
            tar.WriteEntry(new PaxGlobalExtendedAttributesTarEntry(new Dictionary<string, string>
                { ["MSWINDOWS.fileattr"] = "33", ["MSWINDOWS.mountpoint"] = "1" }));
            Dictionary<string, string> overrides = new() { ["MSWINDOWS.fileattr"] = "32" };
            tar.WriteEntry(new PaxTarEntry(TarEntryType.SymbolicLink, "Files/link", overrides) { LinkName = "target" });
            tar.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, "Files/notLink", overrides));
            Dictionary<string, string> removals = new() { ["MSWINDOWS.mountpoint"] = "" };
            tar.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, "Files/file", removals));
            tar.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, "Files/file:secret", removals));
        }
        LayerChanges changes = await ImageLayerScanner.ScanAsync(new MemoryStream(output.ToArray()),
            new(0, "layer"), Token, ImageFileSystemSemantics.Windows);
        Assert.Equal(ImageFileType.SymbolicLink, changes.Entries[0].Type);
        Assert.Equal(ImageFileType.Other, changes.Entries[1].Type);
        Assert.Contains("symbolic-link tar representation", changes.Entries[1].Windows!.UnsupportedReason);
        Assert.Equal(ImageFileType.File, changes.Entries[2].Type);
        Assert.Equal(ImageFileType.Other, changes.Entries[3].Type);
        Assert.Contains("Alternate data streams", changes.Entries[3].Windows!.UnsupportedReason);
    }
}
