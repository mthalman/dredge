using System.Formats.Tar;
using System.IO.Compression;
using System.Reflection;
using Valleysoft.DockerRegistryClient.Models.Manifests.Docker;
using Valleysoft.Dredge.Explorer;
using static Valleysoft.Dredge.Tests.WindowsImageTestData;

namespace Valleysoft.Dredge.Tests;

public sealed class WindowsReviewRoundElevenTests : IAsyncDisposable
{
    private readonly LayerCacheTestContext cache = new();
    private CancellationToken Token => TestContext.Current.CancellationToken;
    public ValueTask DisposeAsync() => cache.DisposeAsync();

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CapturedSymbolicLinkChainsReadTheFinalContent(bool windows)
    {
        byte[] layer = Layer(File("Files/target", "CONTENT"),
            Link("Files/link2", "target"),
            Link("Files/hard2", "Files/link2", hard: true),
            Link("Files/link1", "hard2"),
            Link("Files/hard1", "Files/link1", hard: true));
        Mock<IDockerRegistryClient> client = windows ? Client(layer) : Client("linux", "", layer);
        await using ImageFileSystem files = await ImageFileSystem.CreateAsync(
            client.Object, Name, new(), Token, cache.Store, requireLayerIndexes: true);
        await Check(files);
        await using ImageFileSystem restored = await ImageFileSystem.CreateAsync(client.Object, Name, new(), Token, cache.Store);
        await Check(restored);

        async Task Check(ImageFileSystem view)
        {
            using MemoryStream output = new();
            await view.CopyFileToAsync(windows ? "hard1" : "Files/hard1", output, Token);
            Assert.Equal("CONTENT"u8.ToArray(), output.ToArray());
        }
    }

    [Fact]
    public async Task CapturedSymbolicLinkCyclesFailExplicitly()
    {
        Mock<IDockerRegistryClient> client = Client(Layer(
            Link("Files/link", "hard"),
            Link("Files/hard", "Files/link", hard: true)));
        await using ImageFileSystem files = await ImageFileSystem.CreateAsync(
            client.Object, Name, new(), Token, cache.Store, requireLayerIndexes: true);
        using MemoryStream output = new();
        InvalidDataException error = await Assert.ThrowsAsync<InvalidDataException>(
            () => files.CopyFileToAsync("hard", output, Token));
        Assert.Contains("40 hops", error.Message);
        Assert.Empty(output.ToArray());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ReaddedLayerNodesUseCurrentSpelling(bool directory)
    {
        LayerChanges first = new([Entry("Value")], [], []);
        LayerChanges second = new([Entry("value", directory ? ImageFileType.Directory : ImageFileType.File)],
            ["Value"], []);
        ExplorerImage image = Image(2);
        image.SetIndexed(0, first);
        image.SetIndexed(1, second);
        image.SetAnalysis(ImageAnalysis.Analyze([first, second], ImageFileSystemSemantics.Windows),
            ExplorerInsightsResult.Empty);
        Node node = Assert.Single(image.LayerTree(1));
        Assert.Equal("value", node.Name);
        Assert.Equal("value", node.Path);
        Assert.Equal(Change.Added, node.Change);
        Assert.Equal(directory ? Kind.Dir : Kind.File, node.Kind);
    }

    [Fact]
    public void RawReadditionIsNotOverwrittenByItsWhiteout()
    {
        ExplorerImage image = Image(1);
        image.SetIndexed(0, new([Entry("value")], ["Value"], []));
        Node node = Assert.Single(image.LayerTree(0));
        Assert.Equal("value", node.Name);
        Assert.Equal(Change.None, node.Change);
        Assert.NotNull(node.Entry);
        Assert.Equal(5, node.Size);
    }

    [Fact]
    public async Task StreamPresenceTransitionsUpdateHistoryAndProvenanceWithoutChangingPayload()
    {
        Mock<IDockerRegistryClient> client = Client(
            Layer(File("Files/value", "VALUE"), Link("Files/alias", "Files/value", hard: true)),
            Layer(File("Files/value:secret", "SECRET")),
            Layer(File("Files/.wh.alias:secret", "")));
        await using ImageFileSystem files = await ImageFileSystem.CreateAsync(
            client.Object, Name, new(), Token, cache.Store, requireLayerIndexes: true);
        ImageAnalysisResult analysis = files.Analyze();
        ExplorerImage image = Image(3);
        image.SetAnalysis(analysis, ExplorerInsightsResult.Empty);
        foreach (string path in new[] { "value", "alias" })
        {
            Assert.Equal([(0, Change.Added), (1, Change.Modified), (2, Change.Modified)], image.PathHistory(path));
            Assert.True(analysis.Layers[1].Changes.Single(change => change.Path == path).Entry!.Windows!.HasAlternateStreams);
            Assert.False(analysis.Layers[2].Changes.Single(change => change.Path == path).Entry!.Windows!.HasAlternateStreams);
            Assert.Equal(2, files.List(null, true, false).Single(entry => entry.Path == path).ModifiedLayer!.Index);
            await using ImageFileSystem added = files.CreateLayerSnapshot(1, Token);
            Assert.Equal(1, added.List(null, true, false).Single(entry => entry.Path == path).ModifiedLayer!.Index);
        }
        Assert.Equal(5, analysis.FileBytes);
        Assert.Equal(0, analysis.HiddenBytes);
        await using ImageFileSystem restored = await ImageFileSystem.CreateAsync(client.Object, Name, new(), Token, cache.Store);
        Assert.All(restored.List(null, true, false).Where(entry => entry.Type is ImageFileType.File or ImageFileType.HardLink),
            entry => Assert.Equal(2, entry.ModifiedLayer!.Index));
    }

    [Fact]
    public async Task SameLayerStreamsPreserveAddedHistory()
    {
        Mock<IDockerRegistryClient> client = Client(Layer(File("Files/value", "VALUE"),
            Link("Files/alias", "Files/value", hard: true), File("Files/value:secret", "SECRET")));
        await using ImageFileSystem files = await ImageFileSystem.CreateAsync(
            client.Object, Name, new(), Token, cache.Store, requireLayerIndexes: true);
        foreach (string path in new[] { "value", "alias" })
        {
            LayerFileChange change = Assert.Single(files.Analyze().Layers[0].Changes, change => change.Path == path);
            Assert.Equal(LayerChangeKind.Added, change.Kind);
            Assert.True(change.Entry!.Windows!.HasAlternateStreams);
            Assert.Null(files.List(null, true, false).Single(entry => entry.Path == path).ModifiedLayer);
        }
    }

    [Fact]
    public async Task DeletingOneOfSeveralStreamsDoesNotModifyTheOwnersHistory()
    {
        Mock<IDockerRegistryClient> client = Client(
            Layer(File("Files/value", "VALUE"), File("Files/value:first", "FIRST"), File("Files/value:second", "SECOND")),
            Layer(File("Files/.wh.value:first", "")));
        await using ImageFileSystem files = await ImageFileSystem.CreateAsync(
            client.Object, Name, new(), Token, cache.Store, requireLayerIndexes: true);
        ExplorerImage image = Image(2);
        image.SetAnalysis(files.Analyze(), ExplorerInsightsResult.Empty);
        Assert.Equal([(0, Change.Added)], image.PathHistory("value"));
        Assert.True(files.List(null, true, false).Single(entry => entry.Path == "value").Windows!.HasAlternateStreams);
        Assert.Null(files.List(null, true, false).Single(entry => entry.Path == "value").ModifiedLayer);
    }

    [Fact]
    public async Task SelfReplacementRetainsItsCapturedStreamRestriction()
    {
        Mock<IDockerRegistryClient> client = Client(
            Layer(File("Files/value", "VALUE"), File("Files/value:secret", "SECRET")),
            Layer(Link("Files/value", "Files/value", hard: true)));
        await using ImageFileSystem files = await ImageFileSystem.CreateAsync(
            client.Object, Name, new(), Token, cache.Store, requireLayerIndexes: true);
        Assert.True(files.List(null, true, false).Single(entry => entry.Path == "value").Windows!.HasAlternateStreams);
        Assert.Equal(0, files.Analyze().HiddenBytes);
        await Assert.ThrowsAsync<NotSupportedException>(() =>
            files.ExtractAsync("value", Path.Combine(cache.Root, "self-replaced"), Token));
    }

    [Fact]
    public async Task PrecedingStreamRetainsTheDocumentedReplacementBehavior()
    {
        Mock<IDockerRegistryClient> client = Client(
            Layer(File("Files/value", "OLD")),
            Layer(File("Files/value:secret", "SECRET"), File("Files/value", "NEW")));
        await using ImageFileSystem files = await ImageFileSystem.CreateAsync(
            client.Object, Name, new(), Token, cache.Store, requireLayerIndexes: true);
        Assert.DoesNotContain(files.List(null, true, false), entry => entry.Path.Contains(':'));
        Assert.False(files.List(null, true, false).Single(entry => entry.Path == "value").Windows!.HasAlternateStreams);
        using MemoryStream output = new();
        await files.CopyFileToAsync("value", output, Token);
        Assert.Equal("NEW"u8.ToArray(), output.ToArray());
        Assert.DoesNotContain(files.Analyze().Layers[1].Changes,
            change => change.Path == "value" && change.Entry!.Windows!.HasAlternateStreams);
    }

    [Fact]
    public void ReplacingUnaliasedContentReleasesCapturedStreamNames()
    {
        Dictionary<string, ImageFileSystemEntry> entries = new(WindowsImagePathComparer.Instance);
        ImageFileSystemBuilder builder = new(Mock.Of<IDockerRegistryClient>(), Name, new DockerManifest(),
            cache.Store, [], entries, new(WindowsImagePathComparer.Instance), ImageFileSystemSemantics.Windows);
        for (int layer = 0; layer < 100; layer++)
        {
            builder.ApplyLayer(new([Entry("value"), Entry("value:secret", ImageFileType.Other)], [], []),
                new(layer, $"layer{layer}"), Token);
        }
        WindowsAlternateStreams tracker = Assert.IsType<WindowsAlternateStreams>(typeof(ImageFileSystemBuilder)
            .GetField("alternateStreams", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(builder));
        var retained = Assert.IsType<Dictionary<ImageContentId, HashSet<string>>>(typeof(WindowsAlternateStreams)
            .GetField("streams", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(tracker));
        Assert.Single(retained);
        Assert.True(entries["value"].Windows!.HasAlternateStreams);
    }

    [Fact]
    public async Task InheritedGlobalMetadataReusesItsFingerprintUntilRelevantChanges()
    {
        using MemoryStream output = new();
        using (GZipStream gzip = new(output, CompressionMode.Compress, leaveOpen: true))
        using (TarWriter tar = new(gzip, TarEntryFormat.Pax, leaveOpen: true))
        {
            tar.WriteEntry(new PaxGlobalExtendedAttributesTarEntry(new Dictionary<string, string>
                { ["MSWINDOWS.rawsd"] = new('A', 64 * 1024), ["MSWINDOWS.fileattr"] = "32" }));
            tar.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, "Files/first"));
            tar.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, "Files/local",
                new Dictionary<string, string> { ["MSWINDOWS.fileattr"] = "33" }));
            tar.WriteEntry(new PaxGlobalExtendedAttributesTarEntry(new Dictionary<string, string>
                { ["vendor.ignored"] = "irrelevant", ["MSWINDOWS.fileattr"] = "32" }));
            tar.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, "Files/last"));
            tar.WriteEntry(new PaxGlobalExtendedAttributesTarEntry(new Dictionary<string, string>
                { ["MSWINDOWS.rawsd"] = "" }));
            tar.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, "Files/changed"));
        }
        LayerChanges changes = await ImageLayerScanner.ScanAsync(new MemoryStream(output.ToArray()),
            new(0, "layer"), Token, ImageFileSystemSemantics.Windows);
        Assert.Same(changes.Entries[0].Windows!.Fingerprint, changes.Entries[2].Windows!.Fingerprint);
        Assert.NotEqual(changes.Entries[0].Windows!.Fingerprint, changes.Entries[1].Windows!.Fingerprint);
        Assert.NotEqual(changes.Entries[0].Windows!.Fingerprint, changes.Entries[3].Windows!.Fingerprint);
        Assert.False(changes.Entries[3].Windows!.HasSecurityDescriptor);
    }

    [Theory]
    [InlineData("case")]
    [InlineData("attributes")]
    public async Task InvalidHeadersAreRejectedBeforePayloadConsumption(string invalid)
    {
        byte[] payload = new byte[128 * 1024];
        new Random(17).NextBytes(payload);
        PaxTarEntry entry = File("Files/value", "", invalid == "attributes"
            ? new() { ["MSWINDOWS.fileattr"] = "invalid" } : null);
        entry.DataStream = new MemoryStream(payload);
        byte[] bytes = invalid == "case" ? Layer(File("Files/VALUE", "first"), entry) : Layer(entry);
        using HeaderProbeStream blob = new(bytes);
        Exception error = await Assert.ThrowsAnyAsync<Exception>(() => ImageLayerScanner.ScanAsync(
            blob, new(0, "layer"), Token, ImageFileSystemSemantics.Windows));
        Assert.IsType(invalid == "case" ? typeof(NotSupportedException) : typeof(InvalidDataException), error);
        Assert.True(blob.Position < 64 * 1024);
    }

    private static ExplorerImage Image(int layers) => new(Name.ToString(), "windows/amd64", "digest",
        [.. Enumerable.Range(0, layers).Select(layer => $"layer{layer}")],
        [.. Enumerable.Repeat(1L, layers)], null);

    private static ScannedEntry Entry(string path, ImageFileType type = ImageFileType.File) =>
        new(path, type, 0, 0, 0, type == ImageFileType.File ? 5 : 0, DateTime.UnixEpoch,
            null, 0, 0, 0, "hash") { Windows = new(32, false, false, "", null) };

    private sealed class HeaderProbeStream(byte[] bytes) : MemoryStream(bytes, false)
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (Position >= 64 * 1024) throw new IOException("Payload consumed before validation.");
            return base.ReadAsync(buffer, cancellationToken);
        }
        public override int Read(byte[] buffer, int offset, int count)
        {
            if (Position >= 64 * 1024) throw new IOException("Payload consumed before validation.");
            return base.Read(buffer, offset, count);
        }
    }
}
