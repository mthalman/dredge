using Valleysoft.DockerRegistryClient.Models.Images;
using Valleysoft.Dredge.Explorer;
using static Valleysoft.Dredge.Tests.ExplorerSamples;

namespace Valleysoft.Dredge.Tests;

public sealed class ExplorerDiveModelTests
{
    [Theory]
    [InlineData("mode")]
    [InlineData("uid")]
    [InlineData("gid")]
    public void ChangedNonemptyDirectoriesSurviveHidingUnchangedChildren(string field)
    {
        ScannedEntry directory = File("app", 0, "") with { Type = ImageFileType.Directory };
        ScannedEntry changed = field switch
        {
            "mode" => directory with { Mode = 0x1ED },
            "uid" => directory with { UserId = 1000 },
            _ => directory with { GroupId = 2000 }
        };
        ExplorerImage image = Custom([
            Layer([directory, File("app/file", 1, "same"), File("synthetic/file", 1, "same")]),
            Layer([changed])]);
        ExplorerPresenter presenter = new(image, 150, 42);
        ExplorerState state = new() { Layer = 1, WholeFilesystem = true };
        state.Expanded.UnionWith(["app", "synthetic"]);
        state.Hidden.Add(Change.None);

        FlatRow row = Assert.Single(presenter.Flatten(state));
        Assert.Equal("app", row.Path);
        Assert.Equal(Change.Modified, row.Node.Change);
        state.Hidden.Add(Change.Modified);
        Assert.Empty(presenter.Flatten(state));
    }

    [Theory]
    [Trait("Upstream", "wagoodman/dive#124")]
    [Trait("Upstream", "wagoodman/dive#160")]
    [Trait("Upstream", "wagoodman/dive#316")]
    [InlineData("mode", false)]
    [InlineData("uid", false)]
    [InlineData("gid", false)]
    [InlineData("mode", true)]
    [InlineData("uid", true)]
    [InlineData("gid", true)]
    public void MetadataOnlyChangesRemainVisibleAndDoNotMutateEarlierTrees(string field, bool directory)
    {
        ScannedEntry original = File("app/item", directory ? 0 : 42, "same") with
        {
            Type = directory ? ImageFileType.Directory : ImageFileType.File
        };
        ScannedEntry changed = field switch
        {
            "mode" => original with { Mode = 0x1ED },
            "uid" => original with { UserId = 1000 },
            _ => original with { GroupId = 2000 },
        };
        ExplorerImage image = Custom(
        [
            Layer([original]),
            Layer([changed]),
            Layer([File("unrelated", 1, "other")])
        ]);
        Node early = ExplorerImage.Find(image.WholeTree(0)!, original.Path)!;
        Assert.Equal(LayerChangeKind.Modified, Assert.Single(image.Analysis!.Layers[1].Changes).Kind);

        foreach (List<Node> tree in new[] { image.LayerTree(1), image.WholeTree(1)!, image.WholeTree(2)! })
        {
            Node node = ExplorerImage.Find(tree, original.Path)!;
            Assert.Equal(field == "mode" ? (directory ? "drwxr-xr-x" : "-rwxr-xr-x")
                : (directory ? "drw-r--r--" : "-rw-r--r--"), node.Mode);
            Assert.Equal($"{changed.UserId}:{changed.GroupId}", node.Owner);
        }
        Assert.Equal(Change.Modified, ExplorerImage.Find(image.LayerTree(1), original.Path)!.Change);
        Assert.Equal(directory ? "drw-r--r--" : "-rw-r--r--", early.Mode);
        Assert.Equal("0:0", early.Owner);
        Assert.Equal(directory ? 0 : 42, image.TotalReclaimable);
    }

    [Theory]
    [Trait("Upstream", "wagoodman/dive#524")]
    [InlineData(0x9ED, false, "-rwsr-xr-x")]
    [InlineData(0x9A4, false, "-rwSr--r--")]
    [InlineData(0x5ED, false, "-rwxr-sr-x")]
    [InlineData(0x5A4, false, "-rw-r-Sr--")]
    [InlineData(0x3FF, true, "drwxrwxrwt")]
    [InlineData(0x3B6, true, "drw-rw-rwT")]
    [InlineData(0xFFF, false, "-rwsrwsrwt")]
    [InlineData(0xE00, false, "---S--S--T")]
    public void SpecialPermissionBitsAreRenderedWithTheirExecuteState(int mode, bool directory, string expected)
    {
        ScannedEntry entry = File("entry", 0, "empty") with
        {
            Type = directory ? ImageFileType.Directory : ImageFileType.File,
            Mode = mode,
        };
        ExplorerImage image = Custom([Layer([entry])]);
        Assert.Equal(expected, Assert.Single(image.LayerTree(0)).Mode);
        Assert.Equal(expected, Assert.Single(image.WholeTree(0)!).Mode);
    }

    [Fact]
    [Trait("Upstream", "wagoodman/dive#273")]
    [Trait("Upstream", "wagoodman/dive#302")]
    [Trait("Upstream", "wagoodman/dive#592")]
    public void OutOfOrderIndexesAndEmptyHistoryNeverShiftLayerContents()
    {
        LayerHistory[] history =
        [
            new() { CreatedBy = "ENV FIRST=1", IsEmptyLayer = true },
            new() { CreatedBy = "COPY first /app/value" },
            new() { CreatedBy = "LABEL middle=yes", IsEmptyLayer = true },
            new() { CreatedBy = "COPY second /app/value" },
            new() { CreatedBy = "COPY third /app/value" },
            new() { CreatedBy = "CMD app", IsEmptyLayer = true }
        ];
        ExplorerImage image = new("app:1", "linux/amd64", "sha256:image", ["z", "a", "m"],
            [30, 10, 20], history);
        LayerChanges[] layers = [.. Enumerable.Range(0, 3).Select(static i => Layer([File("app/value", i + 1, $"content-{i}")]))];
        image.SetIndexed(2, layers[2]);
        Assert.Null(image.IndexedPrefix());
        Assert.Null(image.WholeTree(2));
        Assert.Equal(3, ExplorerImage.Find(image.LayerTree(2), "app/value")!.Size);
        image.SetIndexed(0, layers[0]);
        Assert.Equal(layers.Take(1), image.IndexedPrefix()!);
        image.SetIndexed(1, layers[1]);
        Assert.Equal(layers, image.IndexedPrefix()!);
        ImageAnalysisResult analysis = ImageAnalysis.Analyze(image.IndexedPrefix()!);
        image.SetAnalysis(analysis, ExplorerInsights.Build(analysis, image.Instructions, null));

        Assert.Equal([null, 0, null, 1, 2, null], image.History.Select(static row => row.Layer));
        Assert.Equal(["COPY first /app/value", "COPY second /app/value", "COPY third /app/value"], image.Instructions);
        for (int i = 0; i < layers.Length; i++)
        {
            Node node = ExplorerImage.Find(image.WholeTree(i)!, "app/value")!;
            Assert.Equal(i + 1, node.Size);
            Assert.Equal($"content-{i}", node.Entry!.ContentHash);
            Assert.Equal(i == 0 ? Change.Added : Change.Modified, node.Change);
        }
    }

    [Theory]
    [Trait("Upstream", "wagoodman/dive#9")]
    [Trait("Upstream", "wagoodman/dive#17")]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(3)]
    public void InconsistentHistoryFallsBackWithoutInventingLayerAssociations(int historyCount)
    {
        ExplorerImage image = new("app:1", "linux/amd64", "sha256:image", ["first", "last"], [7, 11],
            [.. Enumerable.Range(0, historyCount).Select(static i => new LayerHistory { CreatedBy = $"COPY {i} /" })]);

        Assert.Equal(["Layer 0", "Layer 1"], image.Instructions);
        Assert.Equal([0, 1], image.History.Select(static row => row.Layer!.Value));
        Assert.Equal([7L, 11L], image.History.Select(static row => row.Download));
        Assert.All(image.History, static row => Assert.Equal("(no history for this layer)", row.Instruction));
    }

    [Fact]
    [Trait("Upstream", "wagoodman/dive#44")]
    [Trait("Upstream", "wagoodman/dive#293")]
    [Trait("Upstream", "wagoodman/dive#592")]
    public async Task ConcurrentDownloadsKeepManifestIndexesWhenTheyFinishInReverseOrder()
    {
        TaskCompletionSource[] gates = [.. Enumerable.Range(0, 3).Select(_ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously))];
        TaskCompletionSource[] indexed = [.. Enumerable.Range(0, 3).Select(_ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously))];
        TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource<IReadOnlyDictionary<int, StoredLayerIndex>> complete =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        int running = 0;
        ExplorerLayerIndexer indexer = new(3, async (index, _, token) =>
        {
            if (Interlocked.Increment(ref running) == 3)
            {
                started.TrySetResult();
            }
            await gates[index].Task.WaitAsync(token);
            return new StoredLayerIndex($"digest-{index}", index + 1,
                Layer([File($"file{index}", index + 1, $"content-{index}")]));
        }, concurrency: 3);
        indexer.Indexed += (layer, _) => indexed[layer].TrySetResult();
        indexer.Completed += indexes => complete.TrySetResult(indexes);
        try
        {
            indexer.Start(TestContext.Current.CancellationToken);
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            for (int i = 2; i >= 0; i--)
            {
                gates[i].TrySetResult();
                await indexed[i].Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
                IReadOnlyDictionary<int, StoredLayerIndex> snapshot = indexer.Snapshot();
                Assert.Equal(3 - i, snapshot.Count);
                Assert.Equal($"digest-{i}", snapshot[i].Digest);
                Assert.Equal($"file{i}", Assert.Single(snapshot[i].Changes.Entries).Path);
            }
            IReadOnlyDictionary<int, StoredLayerIndex> final = await complete.Task.WaitAsync(
                TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            Assert.Equal([0, 1, 2], final.Keys.Order());
            Assert.All(Enumerable.Range(0, 3), i => Assert.False(indexer.Retry(i)));
        }
        finally
        {
            await indexer.StopAsync();
        }
    }
}
