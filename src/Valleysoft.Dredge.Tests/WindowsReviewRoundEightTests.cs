using System.Formats.Tar;
using Valleysoft.Dredge.Explorer;
using static Valleysoft.Dredge.Tests.WindowsImageTestData;

namespace Valleysoft.Dredge.Tests;

public sealed class WindowsReviewRoundEightTests : IAsyncDisposable
{
    private readonly LayerCacheTestContext cache = new();
    private CancellationToken Token => TestContext.Current.CancellationToken;
    public ValueTask DisposeAsync() => cache.DisposeAsync();

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task FileToDirectoryReplacementRemovesVisibleStreamsButPreservesOldAliases(bool explicitDirectory, bool alias)
    {
        byte[] first = alias
            ? Layer(File("Files/parent", "OLD"), File("Files/parent:secret", "SECRET"),
                Link("Files/alias", "Files/parent", hard: true))
            : Layer(File("Files/parent", "OLD"), File("Files/parent:secret", "SECRET"));
        byte[] second = explicitDirectory
            ? Layer(new PaxTarEntry(TarEntryType.Directory, "Files/PARENT"), File("Files/PARENT/child", "CHILD"))
            : Layer(File("Files/PARENT/child", "CHILD"));
        Mock<IDockerRegistryClient> client = Client(first, second, Layer(File("Files/keep", "KEEP")));
        ExplorerSource source = await ExplorerSource.OpenAsync(client.Object,
            Mock.Of<IDockerRegistryClientFactory>(), Name, new(), null, Token);
        ExplorerSession session = await ExplorerSession.CreateAsync(client.Object, source, cache.Store, null, Token);
        await using ImageFileSystem files = session.Files;
        ExplorerImage image = ExplorerImage.FromSource(source);
        image.SetSession(session, ExplorerInsightsResult.Empty);
        Assert.DoesNotContain("parent:secret", session.Analysis.LiveEntries.Keys);
        Assert.Contains(session.Analysis.Layers[1].Changes, change =>
            change.Path == "parent:secret" && change.Kind == LayerChangeKind.Deleted);
        Assert.Equal([(0, Change.Added), (1, Change.Removed)], image.PathHistory("PARENT:secret"));
        Assert.Null(image.Find(image.WholeTree(2)!, "parent:secret"));
        await Check(files, "fresh");
        await using ImageFileSystem snapshot = files.CreateLayerSnapshot(0, Token);
        Assert.Contains(snapshot.List(null, true, false), entry => entry.Path == "parent:secret");
        await using ImageFileSystem restored = await ImageFileSystem.CreateAsync(client.Object, Name, new(), Token, cache.Store);
        await Check(restored, "restored");

        async Task Check(ImageFileSystem view, string label)
        {
            Assert.DoesNotContain(view.List(null, true, false), entry => entry.Path.Contains(':'));
            Assert.Contains(view.List(null, true, true), entry =>
                entry.Path == "parent:secret" && entry.DeletedLayer?.Index == 1);
            string destination = Path.Combine(cache.Root, label);
            await view.ExtractAsync("parent", destination, Token);
            Assert.Equal("CHILD", await System.IO.File.ReadAllTextAsync(Path.Combine(destination, "child"), Token));
            if (alias)
            {
                Assert.True(Assert.Single(view.List("alias", false, false)).Windows!.HasAlternateStreams);
                await Assert.ThrowsAsync<NotSupportedException>(() =>
                    view.ExtractAsync("alias", Path.Combine(cache.Root, label + "-alias"), Token));
            }
        }
    }

    [Fact]
    public async Task DirectoryMetadataUpdatesPreserveDirectoryStreams()
    {
        Mock<IDockerRegistryClient> client = Client(
            Layer(new PaxTarEntry(TarEntryType.Directory, "Files/Directory"),
                File("Files/Directory:secret", "SECRET"), File("Files/Directory/value", "VALUE")),
            Layer(new PaxTarEntry(TarEntryType.Directory, "Files/DIRECTORY")));
        await using ImageFileSystem files = await ImageFileSystem.CreateAsync(
            client.Object, Name, new(), Token, cache.Store, requireLayerIndexes: true);
        Assert.Contains("Directory:secret", files.Analyze().LiveEntries.Keys);
        Assert.Contains(files.List(null, true, false), entry => entry.Path == "Directory:secret");
        await Assert.ThrowsAsync<NotSupportedException>(() =>
            files.ExtractAsync("Directory", Path.Combine(cache.Root, "output"), Token));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UnrelatedOwnerLookupsDoNotScanOrAllocatePerTrackedStream(bool unbound)
    {
        WindowsAlternateStreams tracker = new();
        for (int i = 0; i < 1_000; i++)
        {
            tracker.Track($"file{i}:secret", unbound ? null : new ImageContentId(0, $"file{i}", i));
        }
        Assert.Empty(tracker.PathsForOwner("unrelated"));
        int matches = 0;
        long start = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 1_000; i++)
        {
            matches += tracker.PathsForOwner("unrelated").Length;
        }
        long allocated = GC.GetAllocatedBytesForCurrentThread() - start;
        Assert.Equal(0, matches);
        Assert.True(allocated < 100_000, $"Unrelated lookups allocated {allocated:N0} bytes.");
        Assert.Equal(["file42:secret"], tracker.PathsForOwner("FILE42"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RemovingAllOwnersDoesNotAllocateQuadratically(bool unbound)
    {
        WindowsAlternateStreams tracker = new();
        string[] paths = [.. Enumerable.Range(0, 1_000).Select(static i => $"file{i}")];
        foreach (string path in paths)
        {
            tracker.Track(path + ":secret", unbound ? null : new ImageContentId(0, path, 0));
        }
        tracker.PathsForOwner("unrelated");
        long start = GC.GetAllocatedBytesForCurrentThread();
        foreach (string path in paths)
        {
            foreach (string stream in tracker.PathsForOwner(path)) tracker.Forget(stream);
        }
        long allocated = GC.GetAllocatedBytesForCurrentThread() - start;
        Assert.True(allocated < 250_000, $"Owner removals allocated {allocated:N0} bytes.");
        foreach (string path in paths) Assert.Empty(tracker.PathsForOwner(path));
    }

    [Fact]
    public void OwnerBucketsFollowBindingRebindingAndForgetWithoutLosingCapturedState()
    {
        WindowsAlternateStreams tracker = new();
        ImageContentId first = new(0, "original", 0), second = new(1, "original", 0);
        tracker.Track("App/value:secret", null);
        Assert.False(tracker.HasStreams(first));
        Assert.Equal(["App/value:secret"], tracker.PathsForOwner("APP/VALUE"));
        tracker.BindPending("APP/VALUE", first);
        Assert.True(tracker.HasStreams(first));
        Assert.Equal(["App/value:secret"], tracker.PathsForOwner("app/value"));
        tracker.Track("app/VALUE:secret", second);
        Assert.True(tracker.HasStreams(second));
        Assert.Equal(["app/VALUE:secret"], tracker.PathsForOwner("App/Value"));
        Assert.Empty(tracker.DeleteStream("alias:secret:$DATA", first));
        Assert.False(tracker.HasStreams(first));
        Assert.True(tracker.HasStreams(second));
        tracker.Forget("APP/value:secret");
        Assert.Empty(tracker.PathsForOwner("app/value"));
        Assert.True(tracker.HasStreams(second));
        tracker.Track("app/value:secret", null);
        tracker.Forget("APP/VALUE:SECRET");
        Assert.Empty(tracker.PathsForOwner("app/value"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OwnerBucketsRetainOtherStreamsWhenCanonicalAliasesAreDeleted(bool unbound)
    {
        WindowsAlternateStreams tracker = new();
        ImageContentId? owner = unbound ? null : new(0, "original", 0);
        tracker.Track("App/value:secret", owner);
        tracker.Track("app/VALUE:SECRET:$DATA", owner);
        tracker.Track("App/value:public", owner);
        Assert.Equal(3, tracker.PathsForOwner("APP/value").Length);
        string[] deleted = tracker.DeleteStream("app/value:secret", owner);
        Assert.Equal(2, deleted.Length);
        foreach (string path in deleted) tracker.Forget(path);
        Assert.Equal(["App/value:public"], tracker.PathsForOwner("APP/VALUE"));
        tracker.Forget("App/value:public");
        Assert.Empty(tracker.PathsForOwner("APP/VALUE"));
        if (owner is ImageContentId content) Assert.True(tracker.HasStreams(content));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void ExactCaseSearchRetainsHistoryAndLayerFilteringAcrossRepeatedSpellings(bool windows, bool analyzed)
    {
        const int versions = 120;
        ExplorerImage image = new(Name.ToString(), windows ? "windows/amd64" : "linux/amd64", "digest",
            [.. Enumerable.Range(0, versions).Select(static i => $"layer{i}")],
            [.. Enumerable.Repeat(1L, versions)], null);
        for (int i = 0; i < versions; i++)
        {
            string path = windows && i == versions / 2 ? "app/VALUE" : "App/value";
            image.SetIndexed(i, new([Entry(path)], [], []));
        }
        if (analyzed)
        {
            image.SetAnalysis(ImageAnalysis.Analyze(image.IndexedPrefix()!,
                windows ? ImageFileSystemSemantics.Windows : ImageFileSystemSemantics.Linux), ExplorerInsightsResult.Empty);
        }
        IReadOnlyDictionary<string, string[]> candidates = image.ExactCaseSearchPaths;
        Assert.Equal(windows && analyzed ? 2 : 1, candidates.Count);
        Assert.Equal(windows ? ["App/value", "app/VALUE"] : ["App/value"], candidates["App/value"]);
        Assert.Equal(versions, image.SearchIndex["App/value"].Count);
        if (windows && analyzed) Assert.Equal(["App"], candidates["App"]);
        ExplorerPresenter presenter = new(image, 150, 42);
        ExplorerState state = new() { SearchQuery = "missing", SearchExactCase = true };
        Assert.Empty(presenter.SearchResults(state).Hits);
        state.SearchQuery = "value";
        SearchHit latest = Assert.Single(presenter.SearchResults(state).Hits);
        Assert.Equal("App/value", latest.Path);
        Assert.Equal(versions, latest.Layers.Length);
        state.SearchQuery = "VALUE";
        Assert.Equal(windows ? 1 : 0, presenter.SearchResults(state).Total);
        state.SearchQuery = "pp/";
        Assert.Equal("App/value", Assert.Single(presenter.SearchResults(state).Hits).Path);
        Assert.Same(candidates, image.ExactCaseSearchPaths);
        Assert.Same(candidates["App/value"], image.ExactCaseSearchPaths["App/value"]);
        state.SearchQuery = "VALUE";
        state.SearchLayerOnly = true;
        state.Layer = versions - 1;
        Assert.Empty(presenter.SearchResults(state).Hits);
        state.Layer = versions / 2;
        Assert.Equal(windows ? 1 : 0, presenter.SearchResults(state).Total);
    }

    [Fact]
    public void DistinctSearchSpellingsFollowIndexAndAnalysisChangesAndDeletionFilters()
    {
        ExplorerImage image = new(Name.ToString(), "windows/amd64", "digest", ["0", "1", "2"], [1, 1, 1], null);
        LayerChanges first = new([Entry("App/value")], [], []);
        LayerChanges second = new([Entry("app/VALUE")], [], []);
        image.SetIndexed(0, first);
        image.SetIndexed(1, second);
        IReadOnlyDictionary<string, string[]> initial = image.ExactCaseSearchPaths;
        Assert.Equal(["app/VALUE", "App/value"], Assert.Single(initial).Value);
        ExplorerPresenter presenter = new(image, 150, 42);
        ExplorerState state = new() { SearchQuery = "APP", SearchExactCase = true };
        Assert.Empty(presenter.SearchResults(state).Hits);
        image.SetIndexed(2, new([Entry("APP/Value")], [], []));
        IReadOnlyDictionary<string, string[]> indexed = image.ExactCaseSearchPaths;
        Assert.NotSame(initial, indexed);
        Assert.Equal(["APP/Value", "app/VALUE", "App/value"], Assert.Single(indexed).Value);
        Assert.Equal("APP/Value", Assert.Single(presenter.SearchResults(state).Hits).Path);
        image.SetAnalysis(ImageAnalysis.Analyze([first, second, new([], ["app/VALUE"], [])],
            ImageFileSystemSemantics.Windows), ExplorerInsightsResult.Empty);
        IReadOnlyDictionary<string, string[]> analyzed = image.ExactCaseSearchPaths;
        Assert.NotSame(indexed, analyzed);
        Assert.Equal(2, analyzed.Count);
        Assert.Equal(["app/VALUE", "App/value"], analyzed["App/value"]);
        Assert.Equal(["App"], analyzed["App"]);
        Assert.Empty(presenter.SearchResults(state).Hits);
        state.SearchQuery = "VALUE";
        Assert.Equal("app/VALUE", Assert.Single(presenter.SearchResults(state).Hits).Path);
        state.SearchIncludeDeleted = false;
        Assert.Empty(presenter.SearchResults(state).Hits);
        state.SearchIncludeDeleted = true;
        state.SearchLayerOnly = true;
        state.Layer = 0;
        state.SearchQuery = "value";
        Assert.Equal("App/value", Assert.Single(presenter.SearchResults(state).Hits).Path);
        state.Layer = 2;
        Assert.Empty(presenter.SearchResults(state).Hits);
        state.SearchQuery = "VALUE";
        Assert.Equal("app/VALUE", Assert.Single(presenter.SearchResults(state).Hits).Path);
        Assert.Same(analyzed, image.ExactCaseSearchPaths);
    }

    private static ScannedEntry Entry(string path) =>
        new(path, ImageFileType.File, 0, 0, 0, 1, DateTime.UnixEpoch, null, 0, 0, 0, "hash");
}
