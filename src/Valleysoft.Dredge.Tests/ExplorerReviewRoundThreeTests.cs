using System.Formats.Tar;
using System.Text;
using Valleysoft.Dredge.Explorer;
using Valleysoft.Dredge.Explorer.Tui;
using static Valleysoft.Dredge.Tests.WindowsImageTestData;

namespace Valleysoft.Dredge.Tests;

[Collection(ExplorerUiCollection.Name)]
public sealed class ExplorerReviewRoundThreeTests : IAsyncDisposable
{
    private readonly List<LayerCacheTestContext> caches = [];
    private readonly List<ExplorerHost> hosts = [];
    private CancellationToken Token => TestContext.Current.CancellationToken;

    public async ValueTask DisposeAsync()
    {
        foreach (ExplorerHost host in hosts) await host.DisposeAsync();
        foreach (LayerCacheTestContext cache in caches) await cache.DisposeAsync();
    }

    private async Task<(ExplorerImage Image, ExplorerHost Host)> Load(params byte[][] layers)
    {
        LayerCacheTestContext cache = new();
        caches.Add(cache);
        Mock<IDockerRegistryClient> client = Client(layers);
        ExplorerSource source = await ExplorerSource.OpenAsync(client.Object,
            Mock.Of<IDockerRegistryClientFactory>(), Name, new(), null, Token);
        ExplorerSession session = await ExplorerSession.CreateAsync(client.Object, source, cache.Store, null, Token);
        await session.EnsurePackagesAsync(Token);
        ExplorerImage image = ExplorerImage.FromSource(source);
        image.SetSession(session, ExplorerInsightsResult.Empty);
        ExplorerHost host = new(client.Object, Mock.Of<IDockerRegistryClientFactory>(), source, cache.Store,
            image, new ExplorerLayerIndexer(0, static (_, _, _) => throw new InvalidOperationException()),
            new(null, null, false, ClipboardMode.Off, "", ""), cache.Root) { Session = session };
        hosts.Add(host);
        return (image, host);
    }

    [Fact]
    public void SearchesAcrossManyVersionsPreserveCaseAndLayerFilters()
    {
        const int versions = 100;
        ExplorerImage image = new(Name.ToString(), "windows/amd64", "digest",
            [.. Enumerable.Range(0, versions).Select(static i => $"layer{i}")],
            [.. Enumerable.Repeat(1L, versions)], null);
        for (int i = 0; i < versions; i++)
        {
            string path = i % 2 == 0 ? "App/value" : "app/VALUE";
            image.SetIndexed(i, new([new(path, ImageFileType.File, 0, 0, 0, 1,
                DateTime.UnixEpoch, null, 0, 0, 0, "hash")], [], []));
        }
        ExplorerPresenter presenter = new(image, 150, 42);
        ExplorerState state = new() { SearchQuery = "vAlUe" };
        SearchHit latest = Assert.Single(presenter.SearchResults(state).Hits);
        Assert.Equal("app/VALUE", latest.Path);
        Assert.Equal(versions, latest.Layers.Length);
        state.SearchExactCase = true;
        Assert.Empty(presenter.SearchResults(state).Hits);
        state.SearchQuery = "value";
        Assert.Equal("App/value", Assert.Single(presenter.SearchResults(state).Hits).Path);
        state.SearchLayerOnly = true;
        state.Layer = versions - 1;
        Assert.Empty(presenter.SearchResults(state).Hits);
        state.SearchExactCase = false;
        Assert.Equal("app/VALUE", Assert.Single(presenter.SearchResults(state).Hits).Path);
        state.Layer = 0;
        Assert.Equal("App/value", Assert.Single(presenter.SearchResults(state).Hits).Path);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExactCaseSearchRetainsEachLayersSpellingWithoutSplittingWindowsIdentity(bool analyzed)
    {
        byte[][] layers =
        [
            Layer(File("Files/App/value", "first")),
            Layer(File("Files/app/VALUE", "second"))
        ];
        var (image, _) = await Load(layers);
        if (!analyzed)
        {
            image = new(Name.ToString(), "windows/amd64", "digest", ["first", "second"], [1, 1], null);
            for (int i = 0; i < layers.Length; i++)
            {
                image.SetIndexed(i, await ImageLayerScanner.ScanAsync(new MemoryStream(layers[i]),
                    new(i, "layer"), Token, ImageFileSystemSemantics.Windows));
            }
        }
        ExplorerPresenter presenter = new(image, 150, 42);
        ExplorerState state = new()
        {
            Layer = 1, SearchQuery = "VALUE", SearchExactCase = true, SearchLayerOnly = true
        };
        SearchHit upper = Assert.Single(presenter.SearchResults(state).Hits);
        Assert.Equal("app/VALUE", upper.Path);
        Assert.Equal([0, 1], upper.Layers.Select(static change => change.Layer));
        Assert.Equal(2, image.PathHistory("APP/value").Count);
        Assert.Equal(analyzed ? 2 : 1, image.SearchIndex.Count);
        if (analyzed) Assert.Single(image.SearchIndex["App"]);
        state.Layer = 0;
        Assert.Empty(presenter.SearchResults(state).Hits);
        state.SearchQuery = "value";
        Assert.Equal("App/value", Assert.Single(presenter.SearchResults(state).Hits).Path);
        state.SearchLayerOnly = false;
        Assert.Equal("App/value", Assert.Single(presenter.SearchResults(state).Hits).Path);
        state.SearchQuery = "VALUE";
        Assert.Equal("app/VALUE", Assert.Single(presenter.SearchResults(state).Hits).Path);
        state.SearchExactCase = false;
        state.SearchQuery = "vAlUe";
        Assert.Equal("app/VALUE", Assert.Single(presenter.SearchResults(state).Hits).Path);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CrossImageDiffExplainsWindowsMetadataOnlyChanges(bool fingerprint)
    {
        var (before, host) = await Load(Layer(File("Files/value", "same")));
        var (after, _) = await Load(Layer(File("Files/value", "same", fingerprint
            ? new() { ["MSWINDOWS.fileattr"] = "32", ["MSWINDOWS.rawsd"] = "changed" }
            : new() { ["MSWINDOWS.fileattr"] = "33" })));
        ExplorerComparison comparison = ExplorerSession.Compare(before.Session!, after.Session!);
        Assert.Equal(LayerChangeKind.Modified, Assert.Single(comparison.Files).Kind);
        TextDiffContent cross = await host.DiffAsync(comparison, "value", Token);
        Assert.Equal(DiffOp.Same, Assert.Single(cross.Lines!).Op);
        Assert.Contains("Metadata:", cross.Message);
        Assert.Contains(fingerprint ? "metadata fingerprint" : "attributes 0x21", cross.Message);
        Assert.DoesNotContain("mode ", cross.Message);

        var (_, historicalHost) = await Load(Layer(File("Files/value", "same")),
            Layer(File("Files/value", "same", fingerprint
                ? new() { ["MSWINDOWS.fileattr"] = "32", ["MSWINDOWS.rawsd"] = "changed" }
                : new() { ["MSWINDOWS.fileattr"] = "33" })));
        TextDiffContent historical = await historicalHost.DiffVersionAsync("value", 0, 1, Token);
        Assert.StartsWith(cross.Message!, historical.Message);
    }

    [Theory]
    [InlineData("endianness")]
    [InlineData("encoding")]
    [InlineData("newlines")]
    public async Task EqualDecodedTextDoesNotAssertEqualFileBytes(string change)
    {
        UnicodeEncoding little = new(false, true, true);
        UnicodeEncoding big = new(true, true, true);
        byte[] before = change == "newlines" ? "AB\r\n"u8.ToArray()
            : [.. little.GetPreamble(), .. little.GetBytes("AB")];
        byte[] after = change == "endianness" ? [.. big.GetPreamble(), .. big.GetBytes("AB")]
            : change == "encoding" ? "AB"u8.ToArray() : "AB\n"u8.ToArray();
        PaxTarEntry oldEntry = File("Files/value", "");
        oldEntry.DataStream!.Dispose();
        oldEntry.DataStream = new MemoryStream(before);
        PaxTarEntry newEntry = File("Files/value", "");
        newEntry.DataStream!.Dispose();
        newEntry.DataStream = new MemoryStream(after);
        var (_, host) = await Load(Layer(oldEntry), Layer(newEntry));
        TextDiffContent diff = await host.DiffVersionAsync("value", 0, 1, Token);
        Assert.Equal(DiffOp.Same, Assert.Single(diff.Lines!).Op);
        Assert.Contains("No text differences", diff.Message);
        Assert.DoesNotContain("content is identical", diff.Message);
    }
}
