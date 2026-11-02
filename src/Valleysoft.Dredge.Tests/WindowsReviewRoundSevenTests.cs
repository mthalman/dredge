using System.Reflection;
using System.Text;
using Valleysoft.DockerRegistryClient.Models.Manifests.Docker;
using Valleysoft.Dredge.Explorer;
using static Valleysoft.Dredge.Tests.WindowsImageTestData;

namespace Valleysoft.Dredge.Tests;

public sealed class WindowsReviewRoundSevenTests : IAsyncDisposable
{
    private readonly LayerCacheTestContext cache = new();
    private CancellationToken Token => TestContext.Current.CancellationToken;
    public ValueTask DisposeAsync() => cache.DisposeAsync();

    [Theory]
    [InlineData("a")]
    [InlineData("C")]
    [InlineData("file")]
    [InlineData("sub/a")]
    public async Task ExplorerAnalyzesArchiveStreamHardLinkTargets(string basename)
    {
        Mock<IDockerRegistryClient> client = Client(Layer(
            File($"Files/{basename}:secret", "SECRET"),
            Link("Files/alias", $"Files/{basename}:secret", hard: true),
            Link("Files/second-alias", "Files/alias", hard: true),
            File("Files/value", "VALUE")));
        (ExplorerImage image, ExplorerSession session) = await Load(client);
        await using ImageFileSystem files = session.Files;
        Assert.Equal(ImageFileType.Other, session.Analysis.LiveEntries["alias"].Type);
        Assert.Equal(ImageFileType.Other, session.Analysis.LiveEntries["second-alias"].Type);
        Assert.NotNull(image.Find(image.WholeTree(0)!, "value"));
        using MemoryStream content = new();
        await files.CopyFileToAsync("value", content, Token);
        Assert.Equal("VALUE", Encoding.UTF8.GetString(content.ToArray()));
    }

    [Theory]
    [InlineData("secret", "secret", false)]
    [InlineData("secret", "secret", true)]
    [InlineData("secret:$DATA", "SECRET", false)]
    [InlineData("secret:$DATA", "SECRET", true)]
    [InlineData("secret", "SECRET:$data", false)]
    [InlineData("secret", "SECRET:$data", true)]
    [InlineData("secret:$DATA", "SECRET:$data", true)]
    public async Task StreamDeletionAgreesAcrossFilesAnalysisHistoryAndRestoredViews(
        string recordedStream, string deletedStream, bool throughAlias)
    {
        string owner = throughAlias ? "alias" : "original";
        string streamPath = "sub/original:" + recordedStream;
        Mock<IDockerRegistryClient> client = Client(
            Layer(File("Files/sub/original", "BASE"), File("Files/" + streamPath, "SECRET"),
                Link("Files/sub/alias", "Files/sub/original", hard: true)),
            Layer(File($"Files/sub/.wh.{owner}:{deletedStream}", "")),
            Layer(File("Files/keep", "KEEP")));
        (ExplorerImage image, ExplorerSession session) = await Load(client);
        await using ImageFileSystem files = session.Files;
        Assert.DoesNotContain(streamPath, session.Analysis.LiveEntries.Keys);
        LayerFileChange deletion = Assert.Single(session.Analysis.Layers[1].Changes,
            change => change.Path == streamPath);
        Assert.Equal(LayerChangeKind.Deleted, deletion.Kind);
        Assert.Equal([(0, Change.Added), (1, Change.Removed)], image.PathHistory(streamPath));
        Assert.Equal(Change.Removed, image.Find(image.WholeTree(1)!, streamPath)!.Change);
        Assert.Null(image.Find(image.WholeTree(2)!, streamPath));
        Assert.Equal(Change.Removed, image.SearchIndex[streamPath][^1].Change);
        await Check(files, "fresh");
        await using ImageFileSystem snapshot = files.CreateLayerSnapshot(0, Token);
        Assert.Contains(snapshot.List(null, true, false), entry => entry.Path == streamPath);
        await Assert.ThrowsAsync<NotSupportedException>(() =>
            snapshot.ExtractAsync("sub", Path.Combine(cache.Root, "snapshot"), Token));
        await using ImageFileSystem restored = await ImageFileSystem.CreateAsync(client.Object, Name, new(), Token, cache.Store);
        await Check(restored, "restored");

        async Task Check(ImageFileSystem view, string label)
        {
            Assert.DoesNotContain(view.List(null, true, false), entry => entry.Path.Contains(':'));
            Assert.Contains(view.List(null, true, true), entry =>
                entry.Path == streamPath && entry.DeletedLayer?.Index == 1);
            string destination = Path.Combine(cache.Root, label);
            await view.ExtractAsync("sub", destination, Token);
            Assert.Equal("BASE", await System.IO.File.ReadAllTextAsync(Path.Combine(destination, "original"), Token));
            Assert.Equal("BASE", await System.IO.File.ReadAllTextAsync(Path.Combine(destination, "alias"), Token));
        }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task RemovingOrReplacingOwnerRemovesItsVisibleStreamsButPreservesCapturedAliasState(
        bool alias, bool replace)
    {
        byte[] first = alias
            ? Layer(File("Files/original", "OLD"), File("Files/original:secret", "SECRET"),
                Link("Files/alias", "Files/original", hard: true), File("Files/keep", "KEEP"))
            : Layer(File("Files/original", "OLD"), File("Files/original:secret", "SECRET"), File("Files/keep", "KEEP"));
        Mock<IDockerRegistryClient> client = Client(first,
            replace ? Layer(File("Files/original", "NEW")) : Layer(File("Files/.wh.original", "")));
        (ExplorerImage _, ExplorerSession session) = await Load(client);
        await using ImageFileSystem files = session.Files;
        Assert.DoesNotContain("original:secret", session.Analysis.LiveEntries.Keys);
        Assert.Contains(session.Analysis.Layers[1].Changes, change =>
            change.Path == "original:secret" && change.Kind == LayerChangeKind.Deleted);
        await Check(files, "fresh");
        await using ImageFileSystem restored = await ImageFileSystem.CreateAsync(client.Object, Name, new(), Token, cache.Store);
        await Check(restored, "restored");

        async Task Check(ImageFileSystem view, string label)
        {
            Assert.DoesNotContain(view.List(null, true, false), entry => entry.Path.Contains(':'));
            if (alias)
            {
                Assert.True(Assert.Single(view.List("alias", false, false)).Windows!.HasAlternateStreams);
                await Assert.ThrowsAsync<NotSupportedException>(() =>
                    view.ExtractAsync("alias", Path.Combine(cache.Root, label + "-alias"), Token));
            }
            else
            {
                string destination = Path.Combine(cache.Root, label + "-tree");
                await view.ExtractAsync("", destination, Token);
                Assert.Equal("KEEP", await System.IO.File.ReadAllTextAsync(Path.Combine(destination, "keep"), Token));
            }
            if (replace)
            {
                string destination = Path.Combine(cache.Root, label + "-original");
                await view.ExtractAsync("original", destination, Token);
                Assert.Equal("NEW", await System.IO.File.ReadAllTextAsync(destination, Token));
            }
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReplacementStreamIsNotDeletedThroughAnOldAlias(bool removeNewStream)
    {
        List<byte[]> layers =
        [
            Layer(File("Files/original", "OLD"), File("Files/original:secret:$DATA", "OLD STREAM"),
                Link("Files/alias", "Files/original", hard: true)),
            Layer(File("Files/original", "NEW"), File("Files/original:secret", "NEW STREAM")),
            Layer(File("Files/.wh.alias:secret", ""))
        ];
        if (removeNewStream) layers.Add(Layer(File("Files/.wh.original:secret:$DATA", "")));
        Mock<IDockerRegistryClient> client = Client([.. layers]);
        (_, ExplorerSession session) = await Load(client);
        await using ImageFileSystem files = session.Files;
        Assert.Equal(!removeNewStream, session.Analysis.LiveEntries.ContainsKey("original:secret"));
        Assert.DoesNotContain("original:secret:$DATA", session.Analysis.LiveEntries.Keys);
        Assert.DoesNotContain(session.Analysis.Layers[2].Changes, change => change.Path == "original:secret");
        string destination = Path.Combine(cache.Root, "alias");
        await files.ExtractAsync("alias", destination, Token);
        Assert.Equal("OLD", await System.IO.File.ReadAllTextAsync(destination, Token));
        Assert.Equal(!removeNewStream, Assert.Single(files.List("original", false, false)).Windows!.HasAlternateStreams);
    }

    [Fact]
    public async Task DirectoryStreamDeletionRecognizesTheOptionalDataType()
    {
        Mock<IDockerRegistryClient> client = Client(
            Layer(File("Files/Directory/value", "VALUE"), File("Files/Directory:secret:$DATA", "SECRET")),
            Layer(File("Files/.wh.Directory:secret", "")));
        (_, ExplorerSession session) = await Load(client);
        await using ImageFileSystem files = session.Files;
        Assert.DoesNotContain("Directory:secret:$DATA", session.Analysis.LiveEntries.Keys);
        await files.ExtractAsync("Directory", Path.Combine(cache.Root, "directory"), Token);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DirectoryWhiteoutRemovesItsStreamsButOpaqueWhiteoutPreservesThem(bool opaque)
    {
        Mock<IDockerRegistryClient> client = Client(
            Layer(File("Files/Directory/value", "VALUE"), File("Files/Directory:secret", "SECRET")),
            Layer(File(opaque ? "Files/Directory/.wh..wh..opq" : "Files/.wh.Directory", "")));
        (_, ExplorerSession session) = await Load(client);
        await using ImageFileSystem files = session.Files;
        Assert.Equal(opaque, session.Analysis.LiveEntries.ContainsKey("Directory:secret"));
        Assert.Equal(opaque, files.List(null, true, false).Any(entry => entry.Path == "Directory:secret"));
        Assert.DoesNotContain("Directory/value", session.Analysis.LiveEntries.Keys);
        if (!opaque) await files.ExtractAsync("", Path.Combine(cache.Root, "output"), Token);
    }

    [Fact]
    public async Task StreamCanBeDeletedThroughSurvivingAliasAfterOriginalNameWasRemoved()
    {
        Mock<IDockerRegistryClient> client = Client(
            Layer(File("Files/original", "BASE"), File("Files/original:secret:$DATA", "SECRET"),
                Link("Files/alias", "Files/original", hard: true)),
            Layer(File("Files/.wh.original", "")),
            Layer(File("Files/.wh.alias:secret", "")));
        (_, ExplorerSession session) = await Load(client);
        await using ImageFileSystem files = session.Files;
        await using ImageFileSystem snapshot = files.CreateLayerSnapshot(1, Token);
        Assert.True(Assert.Single(snapshot.List("alias", false, false)).Windows!.HasAlternateStreams);
        Assert.DoesNotContain(snapshot.List(null, true, false), entry => entry.Path.Contains(':'));
        Assert.False(Assert.Single(files.List("alias", false, false)).Windows!.HasAlternateStreams);
        Assert.DoesNotContain("original:secret:$DATA", session.Analysis.LiveEntries.Keys);
        await files.ExtractAsync("", Path.Combine(cache.Root, "output"), Token);
    }

    [Fact]
    public async Task DataStreamDeletionDoesNotRemoveOtherStreamTypesOrNames()
    {
        Mock<IDockerRegistryClient> client = Client(
            Layer(File("Files/original", "BASE"), File("Files/original:secret:$DATA", "SECRET"),
                File("Files/original:secret:$OTHER", "OTHER"), File("Files/original:other", "OTHER NAME")),
            Layer(File("Files/.wh.original:secret", "")));
        (_, ExplorerSession session) = await Load(client);
        await using ImageFileSystem files = session.Files;
        Assert.DoesNotContain("original:secret:$DATA", session.Analysis.LiveEntries.Keys);
        Assert.Contains("original:secret:$OTHER", session.Analysis.LiveEntries.Keys);
        Assert.Contains("original:other", session.Analysis.LiveEntries.Keys);
        Assert.True(Assert.Single(files.List("original", false, false)).Windows!.HasAlternateStreams);
        await Assert.ThrowsAsync<NotSupportedException>(() =>
            files.ExtractAsync("original", Path.Combine(cache.Root, "output"), Token));
    }

    [Fact]
    public void UnrelatedFileLayersDoNotRescanExistingContentIdentities()
    {
        Dictionary<string, ImageFileSystemEntry> entries = new(ImageFileSystemSemantics.Windows.Comparer);
        for (int i = 0; i < 10_000; i++)
        {
            string path = $"value{i}";
            entries.Add(path, new()
            {
                Path = path, Type = ImageFileType.File, IntroducedLayer = new(0, "base"),
                ContentPath = path, ContentEntryIndex = i, Windows = new(32, false, false, "", null)
            });
        }
        ImageFileSystemBuilder builder = new(Mock.Of<IDockerRegistryClient>(), Name, new DockerManifest(),
            cache.Store, [], entries, new(ImageFileSystemSemantics.Windows.Comparer), ImageFileSystemSemantics.Windows);
        ScannedEntry stream = new("value0:secret", ImageFileType.Other, 0, 0, 0, 0, DateTime.UnixEpoch,
            null, entries.Count, 0, 0, "hash") { Windows = new(32, false, false, "", "Unsupported stream.") };
        builder.ApplyLayer(new([stream], [], []), new(1, "stream"), Token);
        FieldInfo field = typeof(ImageFileSystemBuilder).GetField("alternateStreams", BindingFlags.Instance | BindingFlags.NonPublic)!;
        WindowsAlternateStreams tracker = Assert.IsType<WindowsAlternateStreams>(field.GetValue(builder));
        FieldInfo streamsField = typeof(WindowsAlternateStreams).GetField("streams", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var tracking = Assert.IsType<Dictionary<ImageContentId, HashSet<string>>>(streamsField.GetValue(tracker));
        CountingContentComparer counted = new();
        Dictionary<ImageContentId, HashSet<string>> instrumented = new(1, counted);
        foreach (var pair in tracking) instrumented.Add(pair.Key, pair.Value);
        streamsField.SetValue(tracker, instrumented);
        counted.HashCalls = 0;
        for (int i = 0; i < 10; i++)
        {
            ScannedEntry added = stream with
            {
                Path = $"new{i}", Type = ImageFileType.File, EntryIndex = i,
                Windows = new(32, false, false, "", null)
            };
            builder.ApplyLayer(new([added], [], []), new(i + 2, "added"), Token);
        }
        Assert.Equal(10, counted.HashCalls);
        Assert.True(entries["value0"].Windows!.HasAlternateStreams);
    }

    private async Task<(ExplorerImage Image, ExplorerSession Session)> Load(Mock<IDockerRegistryClient> client)
    {
        ExplorerSource source = await ExplorerSource.OpenAsync(client.Object,
            Mock.Of<IDockerRegistryClientFactory>(), Name, new(), null, Token);
        ExplorerSession session = await ExplorerSession.CreateAsync(client.Object, source, cache.Store, null, Token);
        ExplorerImage image = ExplorerImage.FromSource(source);
        image.SetSession(session, ExplorerInsightsResult.Empty);
        return (image, session);
    }

    private sealed class CountingContentComparer : IEqualityComparer<ImageContentId>
    {
        public long HashCalls { get; set; }
        public bool Equals(ImageContentId first, ImageContentId second) => first == second;
        public int GetHashCode(ImageContentId value)
        {
            HashCalls++;
            return value.GetHashCode();
        }
    }
}
