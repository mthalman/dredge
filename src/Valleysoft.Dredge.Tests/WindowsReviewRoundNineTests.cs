using Valleysoft.DockerRegistryClient.Models.Manifests;
using Valleysoft.Dredge.Explorer;
using static Valleysoft.Dredge.Tests.WindowsImageTestData;

namespace Valleysoft.Dredge.Tests;

public sealed class WindowsReviewRoundNineTests : IAsyncDisposable
{
    private readonly LayerCacheTestContext cache = new();
    private CancellationToken Token => TestContext.Current.CancellationToken;
    public ValueTask DisposeAsync() => cache.DisposeAsync();

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task WhiteoutsRemoveHighUnicodeDescendantsFromAnalysisAndHistory(bool windows, bool opaque)
    {
        string path = "Dir/\ufffffile";
        byte[] first = Layer(File("Files/" + path, "12345"), File("Files/Dir0/keep", "KEEP"));
        byte[] second = Layer(File(opaque ? "Files/Dir/.wh..wh..opq" : "Files/.wh.Dir", ""));
        Mock<IDockerRegistryClient> client = windows ? Client(first, second)
            : Client("linux", "", first, second);
        await using ImageFileSystem files = await ImageFileSystem.CreateAsync(
            client.Object, Name, new(), Token, cache.Store, requireLayerIndexes: true);
        string effectivePath = windows ? path : "Files/" + path;
        ImageAnalysisResult analysis = files.Analyze();
        Assert.DoesNotContain(effectivePath, analysis.LiveEntries.Keys);
        Assert.Equal(5, analysis.HiddenBytes);
        Assert.Equal(5, analysis.Layers[0].HiddenBytes);
        Assert.Contains(analysis.Layers[1].Changes, change =>
            change.Path == effectivePath && change.Kind == LayerChangeKind.Deleted);
        Assert.Contains(windows ? "Dir0/keep" : "Files/Dir0/keep", analysis.LiveEntries.Keys);
        Assert.DoesNotContain(files.List(null, true, false), entry => entry.Path == effectivePath);
        ExplorerImage image = new(Name.ToString(), windows ? "windows/amd64" : "linux/amd64",
            "digest", ["0", "1", "2"], [1, 1, 1], null);
        image.SetAnalysis(analysis, ExplorerInsightsResult.Empty);
        Assert.Equal([(0, Change.Added), (1, Change.Removed)], image.PathHistory(effectivePath));
        ExplorerPresenter presenter = new(image, 150, 42);
        Assert.Empty(presenter.SearchResults(new() { SearchQuery = "\ufffffile", SearchIncludeDeleted = false }).Hits);
        await using ImageFileSystem snapshot = files.CreateLayerSnapshot(0, Token);
        Assert.Contains(snapshot.List(null, true, false), entry => entry.Path == effectivePath);
        await using ImageFileSystem restored = await ImageFileSystem.CreateAsync(
            client.Object, Name, new(), Token, cache.Store, requireLayerIndexes: true);
        Assert.DoesNotContain(effectivePath, restored.Analyze().LiveEntries.Keys);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void DeletingManyStreamsHasBoundedAllocations(bool bound, bool singleOwner)
    {
        Measure(32, bound, singleOwner);
        long allocated = Measure(1000, bound, singleOwner);
        Assert.True(allocated < 600_000, $"Deleting streams allocated {allocated:N0} bytes.");

        static long Measure(int count, bool bound, bool singleOwner)
        {
            WindowsAlternateStreams tracker = new();
            ImageContentId content = new(0, "file", 0);
            string[] paths = [.. Enumerable.Range(0, count).Select(i =>
                singleOwner ? $"file:stream{i}" : $"file{i}:secret")];
            ImageContentId?[] owners = [.. Enumerable.Range(0, count).Select(i =>
                bound ? (ImageContentId?)(singleOwner ? content : new(0, $"file{i}", i)) : null)];
            for (int i = 0; i < count; i++) tracker.Track(paths[i], owners[i]);
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < count; i++)
            {
                foreach (string deleted in tracker.DeleteStream(paths[i], owners[i]))
                {
                    tracker.Forget(deleted);
                }
            }
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            Assert.Empty(tracker.PathsForOwner("file"));
            Assert.False(tracker.HasStreams(content));
            return allocated;
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MissingStreamDeletionDoesNotScanRetainedNames(bool bound)
    {
        WindowsAlternateStreams tracker = new();
        ImageContentId? owner = bound ? new(0, "file", 0) : null;
        for (int i = 0; i < 1000; i++) tracker.Track($"file:stream{i}", owner);
        tracker.DeleteStream("file:missing", owner);
        tracker.TakePresenceChanges();
        int deleted = 0;
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 1000; i++)
        {
            deleted += tracker.DeleteStream("file:missing", owner).Length;
        }
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(0, deleted);
        Assert.Empty(tracker.TakePresenceChanges());
        Assert.True(allocated < 400_000, $"Missing stream whiteouts allocated {allocated:N0} bytes.");
        Assert.Equal(1000, tracker.PathsForOwner("FILE").Length);
        if (owner is ImageContentId content) Assert.True(tracker.HasStreams(content));
    }

    [Fact]
    public void StreamDeletionIndexFollowsPendingBindingRebindingAndCapturedAliases()
    {
        WindowsAlternateStreams tracker = new();
        ImageContentId first = new(0, "original", 0), second = new(1, "original", 0);
        tracker.Track("original:secret:$DATA", null);
        tracker.BindPending("original", first);
        Assert.True(tracker.HasStreams(first));
        tracker.Track("alias:SECRET", first);
        tracker.Track("original:public", first);
        tracker.Forget("original:secret:$DATA");
        tracker.Track("original:secret", second);
        string[] removed = tracker.DeleteStream("ALIAS:secret:$data", first);
        Assert.Equal(["alias:SECRET"], removed);
        foreach (string path in removed) tracker.Forget(path);
        Assert.True(tracker.HasStreams(first));
        Assert.True(tracker.HasStreams(second));
        tracker.Track("original:public", second);
        Assert.Empty(tracker.DeleteStream("old-alias:public", first));
        Assert.False(tracker.HasStreams(first));
        Assert.Equal(2, tracker.PathsForOwner("ORIGINAL").Length);
        tracker.Forget("original:secret");
        Assert.Empty(tracker.DeleteStream("survivor:secret", second));
        Assert.True(tracker.HasStreams(second));
        removed = tracker.DeleteStream("original:PUBLIC:$DATA", second);
        Assert.Equal(["original:public"], removed);
        foreach (string path in removed) tracker.Forget(path);
        Assert.False(tracker.HasStreams(second));
        Assert.Empty(tracker.PathsForOwner("original"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MetadataIsSharedUnlessTheCapturedStreamFlagChanges(bool suppliedFlag)
    {
        LayerChanges changes = await ImageLayerScanner.ScanAsync(
            new MemoryStream(Layer(File("Files/value", "VALUE"))), new(0, "layer"), Token,
            ImageFileSystemSemantics.Windows);
        ScannedEntry scanned = Assert.Single(changes.Entries);
        if (suppliedFlag)
        {
            scanned = scanned with { Windows = scanned.Windows! with { HasAlternateStreams = true } };
        }
        Dictionary<string, ImageFileSystemEntry> entries = new(WindowsImagePathComparer.Instance);
        ImageFileSystemBuilder builder = new(Client().Object, Name, Mock.Of<IImageManifest>(),
            cache.Store, [], entries, new(WindowsImagePathComparer.Instance), ImageFileSystemSemantics.Windows);
        builder.ApplyLayer(new([scanned], [], []), new(0, "layer"), Token);
        Assert.False(entries["value"].Windows!.HasAlternateStreams);
        if (suppliedFlag)
        {
            Assert.NotSame(scanned.Windows, entries["value"].Windows);
            Assert.True(scanned.Windows!.HasAlternateStreams);
        }
        else
        {
            Assert.Same(scanned.Windows, entries["value"].Windows);
        }
        LayerChanges stream = await ImageLayerScanner.ScanAsync(
            new MemoryStream(Layer(File("Files/value:secret", "SECRET"))), new(1, "stream"), Token,
            ImageFileSystemSemantics.Windows);
        builder.ApplyLayer(stream, new(1, "stream"), Token);
        Assert.True(entries["value"].Windows!.HasAlternateStreams);
        Assert.Equal(suppliedFlag, scanned.Windows!.HasAlternateStreams);
        builder.ApplyLayer(new([], ["value:secret"], []), new(2, "deleted"), Token);
        Assert.False(entries["value"].Windows!.HasAlternateStreams);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task WindowsInsightsUseShellNeutralGuidanceAndPreserveAccounting(bool windows, bool partial)
    {
        byte[] first = Layer(File("Files/cache/value", new string('a', 1_000_000)),
            File("Files/root/.npm/_cacache/live", "LIVE"));
        byte[] second = Layer(File("Files/.wh.cache", ""));
        Mock<IDockerRegistryClient> client = windows ? Client(first, second) : Client("linux", "", first, second);
        ExplorerSource source = await ExplorerSource.OpenAsync(client.Object,
            Mock.Of<IDockerRegistryClientFactory>(), Name, new(), null, Token);
        ExplorerSession session = await ExplorerSession.CreateAsync(client.Object, source, cache.Store, null, Token);
        await using ImageFileSystem files = session.Files;
        ImageAnalysisResult analysis = session.Analysis;
        ImageFileSystemSemantics semantics = windows ? ImageFileSystemSemantics.Windows : ImageFileSystemSemantics.Linux;
        ExplorerInsightsResult insights = ExplorerInsights.Build(analysis,
            ["RUN create", "RUN cleanup"], 0, includePotential: !partial, semantics: semantics);
        Assert.Equal(1_000_000, insights.HiddenBytes);
        Assert.Equal(windows || partial ? 0 : 4, insights.PotentialBytes);
        ExplorerFinding deletion = Assert.Single(insights.Findings, finding => finding.Kind == ExplorerFindingKind.Deleted);
        Assert.Equal([0, 1], deletion.Layers);
        Assert.Equal("Delete in the same RUN that created them", deletion.FixLabel);
        Assert.Equal(!windows, deletion.FixIsDockerfile);
        if (windows)
        {
            Assert.DoesNotContain("rm -rf", deletion.Fix);
            Assert.Contains("configured shell", deletion.Fix);
        }
        else
        {
            Assert.Equal("RUN ... && rm -rf <paths>", deletion.Fix);
        }
        ExplorerImage image = ExplorerImage.FromSource(source);
        image.SetSession(session, insights);
        ExplorerPresenter presenter = new(image, 150, 42);
        Assert.Contains(deletion, presenter.VisibleFindings(new()));
        string pane = string.Join("\n", presenter.InsightsPane(new()).Lines.Select(line => line.ToString()));
        Assert.Contains(deletion.FixLabel, pane);
        Assert.Equal(windows, pane.Contains("configured shell", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void WindowsReplacementInsightsDoNotAssumeShellOperators(bool identical)
    {
        ScannedEntry entry = new("value", ImageFileType.File, 0, 0, 0, 1_000_000,
            DateTime.UnixEpoch, null, 0, 0, 0, "first");
        ImageAnalysisResult analysis = ImageAnalysis.Analyze(
            [new([entry], [], []), new([entry with { ContentHash = identical ? "first" : "second" }], [], [])],
            ImageFileSystemSemantics.Windows);
        ExplorerFinding finding = Assert.Single(ExplorerInsights.Build(analysis,
            ["RUN first", "RUN second"], null, semantics: ImageFileSystemSemantics.Windows).Findings);
        Assert.False(finding.FixIsDockerfile);
        Assert.DoesNotContain("&&", finding.Fix);
        Assert.Contains("configured shell", finding.Fix);
        Assert.Equal(1_000_000, finding.Bytes);
    }
}
