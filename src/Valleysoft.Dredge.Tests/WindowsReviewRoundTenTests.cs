using System.Formats.Tar;
using System.IO.Compression;
using System.Reflection;
using Valleysoft.DockerRegistryClient.Models.Manifests.Docker;
using Valleysoft.Dredge.Explorer;
using Valleysoft.Dredge.Explorer.Tui;
using static Valleysoft.Dredge.Tests.WindowsImageTestData;

namespace Valleysoft.Dredge.Tests;

public sealed class WindowsReviewRoundTenTests : IAsyncDisposable
{
    private readonly LayerCacheTestContext cache = new();
    private CancellationToken Token => TestContext.Current.CancellationToken;
    public ValueTask DisposeAsync() => cache.DisposeAsync();

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LongSAndAsciiSRemainSeparateAcrossViewsAndWhiteouts(bool separateLayers)
    {
        byte[] first = Layer(File("Files/S/value", "ASCII"));
        byte[] second = Layer(File("Files/\u017f/value", "LONG"));
        Mock<IDockerRegistryClient> client = separateLayers ? Client(first, second)
            : Client(Layer(File("Files/S/value", "ASCII"), File("Files/\u017f/value", "LONG")));
        await using ImageFileSystem files = await ImageFileSystem.CreateAsync(
            client.Object, Name, new(), Token, cache.Store, requireLayerIndexes: true);
        Assert.Equal(2, files.Analyze().LiveContents.Count);
        Assert.False(WindowsImagePathComparer.Instance.Equals("S", "\u017f"));
        Assert.False(WindowsImagePathComparer.StartsWith("S/value", "\u017f/"));
        Assert.Equal(2, new HashSet<string>(["S", "\u017f"], WindowsImagePathComparer.Instance).Count);
        await using ImageFileSystem restored = await ImageFileSystem.CreateAsync(client.Object, Name, new(), Token, cache.Store);
        await Check(files);
        await Check(restored);
        Mock<IDockerRegistryClient> deletedClient = Client(
            Layer(File("Files/S/value", "ASCII"), File("Files/\u017f/value", "LONG")),
            Layer(File("Files/.wh.S", "")));
        await using ImageFileSystem deleted = await ImageFileSystem.CreateAsync(
            deletedClient.Object, Name, new(), Token, cache.Store, requireLayerIndexes: true);
        Assert.Contains("\u017f/value", deleted.Analyze().LiveContents.Keys);
        Assert.DoesNotContain("S/value", deleted.Analyze().LiveContents.Keys);

        async Task Check(ImageFileSystem view)
        {
            using MemoryStream ascii = new(), other = new();
            await view.CopyFileToAsync("s/value", ascii, Token);
            await view.CopyFileToAsync("\u017f/value", other, Token);
            Assert.Equal("ASCII"u8.ToArray(), ascii.ToArray());
            Assert.Equal("LONG"u8.ToArray(), other.ToArray());
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AnalysisRejectsDirectoryHardLinkTargetsLikeTheFilesystem(bool windows)
    {
        byte[] layer = Layer(new PaxTarEntry(TarEntryType.Directory, "Files/Dir"),
            Link("Files/alias", "Files/Dir", hard: true));
        LayerChanges changes = await ImageLayerScanner.ScanAsync(new MemoryStream(layer), new(0, "layer"),
            Token, windows ? ImageFileSystemSemantics.Windows : ImageFileSystemSemantics.Linux);
        Assert.Throws<InvalidDataException>(() =>
            ImageAnalysis.Analyze([changes], windows ? ImageFileSystemSemantics.Windows : ImageFileSystemSemantics.Linux));
        Mock<IDockerRegistryClient> client = windows ? Client(layer) : Client("linux", "", layer);
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            ImageFileSystem.CreateAsync(client.Object, Name, new(), Token, cache.Store, requireLayerIndexes: true));
    }

    [Fact]
    public void AdditionalStreamNamesDoNotReconcileUnrelatedContent()
    {
        Dictionary<string, ImageFileSystemEntry> entries = new(WindowsImagePathComparer.Instance);
        for (int i = 0; i < 5000; i++)
        {
            string path = $"file{i}";
            entries.Add(path, new()
            {
                Path = path, Type = ImageFileType.File, ContentPath = path, ContentEntryIndex = i,
                IntroducedLayer = new(0, "base"), Windows = new(32, false, false, "", null)
            });
        }
        ImageFileSystemBuilder builder = Builder(entries);
        builder.ApplyLayer(new([Stream("file0:first")], [], []), new(1, "stream"), Token);
        WindowsAlternateStreams tracker = Assert.IsType<WindowsAlternateStreams>(typeof(ImageFileSystemBuilder)
            .GetField("alternateStreams", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(builder));
        FieldInfo field = typeof(WindowsAlternateStreams).GetField("streams", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var original = Assert.IsType<Dictionary<ImageContentId, HashSet<string>>>(field.GetValue(tracker));
        CountingContentComparer comparer = new();
        Dictionary<ImageContentId, HashSet<string>> instrumented = new(comparer);
        foreach (var item in original) instrumented.Add(item.Key, item.Value);
        field.SetValue(tracker, instrumented);
        comparer.Calls = 0;
        for (int i = 0; i < 10; i++)
        {
            builder.ApplyLayer(new([Stream($"file0:extra{i}")], [], []), new(i + 2, "extra"), Token);
        }
        Assert.True(comparer.Calls < 100, $"Stream-name changes performed {comparer.Calls} content hashes.");
        Assert.True(entries["file0"].Windows!.HasAlternateStreams);
    }

    [Fact]
    public async Task CrossImageStreamFlagChangesAreExplained()
    {
        (ExplorerSource source, ExplorerSession baseline) = await Load(Client(
            Layer(File("Files/value", "VALUE"), File("Files/value:secret", "SECRET"))));
        (_, ExplorerSession target) = await Load(Client(
            Layer(File("Files/value", "VALUE"), File("Files/value:secret", "SECRET")),
            Layer(File("Files/.wh.value:secret", ""))));
        await using ImageFileSystem before = baseline.Files;
        await using ImageFileSystem after = target.Files;
        ExplorerImage image = ExplorerImage.FromSource(source);
        image.SetSession(baseline, ExplorerInsightsResult.Empty);
        await using ExplorerHost host = new(Mock.Of<IDockerRegistryClient>(), Mock.Of<IDockerRegistryClientFactory>(),
            source, cache.Store, image, new ExplorerLayerIndexer(0, static (_, _, _) => throw new InvalidOperationException()),
            new(null, null, false, ClipboardMode.Off, "", ""), cache.Root) { Session = baseline };
        TextDiffContent diff = await host.DiffAsync(ExplorerSession.Compare(baseline, target), "value", Token);
        Assert.Contains("alternate streams present", diff.Message);
        Assert.Contains("alternate streams absent", diff.Message);
        Assert.All(diff.Lines!, line => Assert.Equal(DiffOp.Same, line.Op));
    }

    [Fact]
    public async Task HardLinkDiffHasOneTargetExplanation()
    {
        byte[] first = Layer(File("Files/a", "SAME"), File("Files/b", "SAME"), Link("Files/alias", "Files/a", hard: true));
        byte[] second = Layer(File("Files/a", "SAME"), File("Files/b", "SAME"), Link("Files/alias", "Files/b", hard: true));
        (ExplorerSource source, ExplorerSession baseline) = await Load(Client(first));
        (_, ExplorerSession target) = await Load(Client(second));
        await using ImageFileSystem before = baseline.Files;
        await using ImageFileSystem after = target.Files;
        ExplorerImage image = ExplorerImage.FromSource(source);
        image.SetSession(baseline, ExplorerInsightsResult.Empty);
        await using ExplorerHost host = new(Mock.Of<IDockerRegistryClient>(), Mock.Of<IDockerRegistryClientFactory>(),
            source, cache.Store, image, new ExplorerLayerIndexer(0, static (_, _, _) => throw new InvalidOperationException()),
            new(null, null, false, ClipboardMode.Off, "", ""), cache.Root) { Session = baseline };
        TextDiffContent diff = await host.DiffAsync(ExplorerSession.Compare(baseline, target), "alias", Token);
        Assert.Contains("target a", diff.Message);
        Assert.Contains("target b", diff.Message);
        Assert.DoesNotContain("Hard link target:", diff.Message);
    }

    [Fact]
    public async Task GlobalWindowsMetadataOverridesIgnoreUnrelatedKeys()
    {
        using MemoryStream output = new();
        using (GZipStream gzip = new(output, CompressionMode.Compress, leaveOpen: true))
        using (TarWriter tar = new(gzip, TarEntryFormat.Pax, leaveOpen: true))
        {
            Dictionary<string, string> global = new() { ["MSWINDOWS.fileattr"] = "32" };
            for (int i = 0; i < 1000; i++) global[$"vendor.ignored{i}"] = "irrelevant";
            tar.WriteEntry(new PaxGlobalExtendedAttributesTarEntry(global));
            tar.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, "Files/inherited"));
            tar.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, "Files/override",
                new Dictionary<string, string> { ["MSWINDOWS.fileattr"] = "33" }));
            tar.WriteEntry(new PaxGlobalExtendedAttributesTarEntry(new Dictionary<string, string> { ["MSWINDOWS.fileattr"] = "" }));
            tar.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, "Files/removed"));
        }
        LayerChanges changes = await ImageLayerScanner.ScanAsync(new MemoryStream(output.ToArray()),
            new(0, "layer"), Token, ImageFileSystemSemantics.Windows);
        Assert.Equal((uint)32, changes.Entries[0].Windows!.Attributes);
        Assert.Equal((uint)33, changes.Entries[1].Windows!.Attributes);
        Assert.Null(changes.Entries[2].Windows!.Attributes);
        Assert.All(changes.Entries, entry => Assert.Null(entry.Windows!.UnsupportedReason));
    }

    [Fact]
    public void IrrelevantGlobalMetadataIsDiscardedBeforeFileProcessing()
    {
        Dictionary<string, string> changes = new() { ["MSWINDOWS.fileattr"] = "32" };
        for (int i = 0; i < 1000; i++) changes[$"vendor.ignored{i}"] = "irrelevant";
        Dictionary<string, string> retained = new(StringComparer.Ordinal);
        MethodInfo apply = typeof(ImageLayerScanner).GetMethod("UpdateGlobalWindowsAttributes",
            BindingFlags.Static | BindingFlags.NonPublic)!;
        apply.Invoke(null, [retained, changes]);
        Assert.Equal("MSWINDOWS.fileattr", Assert.Single(retained).Key);
        Assert.Equal("32", retained["MSWINDOWS.fileattr"]);
        apply.Invoke(null, [retained, new Dictionary<string, string> { ["MSWINDOWS.fileattr"] = "" }]);
        Assert.Empty(retained);
    }

    [Fact]
    public void MissingStreamWhiteoutsUseBoundedPathRangesAndRetainTombstones()
    {
        Dictionary<string, ImageFileSystemEntry> entries = new(WindowsImagePathComparer.Instance);
        for (int i = 0; i < 5000; i++)
        {
            string path = $"value{i}";
            entries.Add(path, new()
            {
                Path = path, Type = ImageFileType.File, ContentPath = path, ContentEntryIndex = i,
                IntroducedLayer = new(0, "base"), Windows = new(32, false, false, "", null)
            });
        }
        Dictionary<string, ImageFileSystemEntry> deleted = new(WindowsImagePathComparer.Instance);
        ImageFileSystemBuilder builder = new(Mock.Of<IDockerRegistryClient>(), Name, new DockerManifest(),
            cache.Store, [], entries, deleted, ImageFileSystemSemantics.Windows);
        CountingPathComparer counted = new();
        SortedSet<string> instrumented = new(entries.Keys, counted);
        typeof(ImageFileSystemBuilder).GetField("paths", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(builder, instrumented);
        counted.Calls = 0;
        string[] whiteouts = [.. Enumerable.Range(0, 1000).Select(i => $"absent:stream{i}")];
        builder.ApplyLayer(new([], whiteouts, []), new(1, "whiteouts"), Token);
        Assert.True(counted.Calls < 100_000, $"Missing stream whiteouts made {counted.Calls} path comparisons.");
        Assert.Equal(5000, entries.Count);
        Assert.Equal(1000, deleted.Count);
        builder.ApplyLayer(new([], whiteouts, []), new(2, "again"), Token);
        Assert.All(deleted.Values, entry => Assert.Equal(2, entry.DeletedLayer!.Index));
        ScannedEntry owner = Stream("absent") with { Type = ImageFileType.File, Windows = new(32, false, false, "", null) };
        builder.ApplyLayer(new([owner], [], []), new(3, "owner"), Token);
        Assert.Contains("absent", instrumented);
        builder.ApplyLayer(new([], ["ABSENT"], []), new(4, "delete"), Token);
        Assert.DoesNotContain("absent", instrumented);
        Assert.DoesNotContain("absent", entries.Keys);
    }

    [Fact]
    public void UnrelatedOwnerWritesDoNotRetryPendingDirectoryStreams()
    {
        WindowsAlternateStreams tracker = new();
        for (int i = 0; i < 10000; i++) tracker.Track($"Directory{i}:secret", null);
        ImageContentId content = new(1, "new", 0);
        tracker.BindPending("unrelated", content);
        Assert.False(tracker.HasStreams(content));
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 1000; i++)
        {
            tracker.BindPending("unrelated", content);
            tracker.BindPending("Directory0", null);
        }
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.False(tracker.HasStreams(content));
        Assert.Empty(tracker.TakePresenceChanges());
        Assert.True(allocated < 100_000, $"Unrelated owner writes allocated {allocated:N0} bytes.");
        Assert.Equal(["Directory0:secret"], tracker.PathsForOwner("DIRECTORY0"));
        tracker.BindPending("Directory0", content);
        Assert.True(tracker.HasStreams(content));
        Assert.Equal([content], tracker.TakePresenceChanges());
        tracker.BindPending("Directory0", content);
        Assert.Empty(tracker.TakePresenceChanges());
        Assert.Equal(["Directory1:secret"], tracker.PathsForOwner("Directory1"));
        Assert.Equal(["Directory0:secret"], tracker.DeleteStream("alias:secret:$DATA", content));
        Assert.False(tracker.HasStreams(content));
        Assert.Equal([content], tracker.TakePresenceChanges());
    }

    [Fact]
    public void StreamPresenceChangesOnlyIdentifyFirstAdditionAndLastDeletion()
    {
        WindowsAlternateStreams tracker = new();
        ImageContentId content = new(0, "original", 0);
        tracker.Track("original:first", content);
        Assert.Equal([content], tracker.TakePresenceChanges());
        tracker.Track("original:second", content);
        tracker.Track("alias:FIRST:$DATA", content);
        Assert.Empty(tracker.TakePresenceChanges());
        foreach (string path in tracker.DeleteStream("alias:first", content)) tracker.Forget(path);
        Assert.Empty(tracker.TakePresenceChanges());
        foreach (string path in tracker.DeleteStream("original:second", content)) tracker.Forget(path);
        Assert.Equal([content], tracker.TakePresenceChanges());
        Assert.Empty(tracker.TakePresenceChanges());
    }

    private ImageFileSystemBuilder Builder(Dictionary<string, ImageFileSystemEntry> entries) =>
        new(Mock.Of<IDockerRegistryClient>(), Name, new DockerManifest(), cache.Store, [], entries,
            new(WindowsImagePathComparer.Instance), ImageFileSystemSemantics.Windows);

    private static ScannedEntry Stream(string path) => new(path, ImageFileType.Other, 0, 0, 0, 0,
        DateTime.UnixEpoch, null, 0, 0, 0, "stream")
        { Windows = new(32, false, false, "", "Unsupported stream.") };

    private async Task<(ExplorerSource, ExplorerSession)> Load(Mock<IDockerRegistryClient> client)
    {
        ExplorerSource source = await ExplorerSource.OpenAsync(client.Object,
            Mock.Of<IDockerRegistryClientFactory>(), Name, new(), null, Token);
        ExplorerSession session = await ExplorerSession.CreateAsync(client.Object, source, cache.Store, null, Token);
        await session.EnsurePackagesAsync(Token);
        return (source, session);
    }

    private sealed class CountingContentComparer : IEqualityComparer<ImageContentId>
    {
        public int Calls { get; set; }
        public bool Equals(ImageContentId x, ImageContentId y) => x == y;
        public int GetHashCode(ImageContentId value) { Calls++; return value.GetHashCode(); }
    }

    private sealed class CountingPathComparer : IComparer<string>
    {
        public int Calls { get; set; }
        public int Compare(string? x, string? y) { Calls++; return WindowsImagePathComparer.Instance.Compare(x, y); }
    }
}
