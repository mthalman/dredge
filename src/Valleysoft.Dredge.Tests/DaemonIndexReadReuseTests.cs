using System.Formats.Tar;

namespace Valleysoft.Dredge.Tests;

public sealed class DaemonIndexReadReuseTests : IAsyncDisposable
{
    private readonly string root = Path.GetFullPath(Path.Combine(
        ".test-daemon-read-reuse", Guid.NewGuid().ToString("N")));
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SequentialExtractionBuffersOnlyRawArchiveMembersAndRetainsReaderOwnership(bool gzip)
    {
        DockerArchiveFixture fixture = DockerArchiveFixture.Create(oci: true, gzip: gzip);
        await using DockerImageArchive archive = await OpenAsync(fixture);
        await using LayerStore store = new(Path.Combine(root, "cache"));
        string digest = archive.Resolved.Manifest.Layers[0].Digest!;
        using Stream blob = await store.OpenExtractionBlobAsync(archive, digest, fixture.StoredLayer.Length, Token);
        Assert.Equal(!gzip, blob is BufferedStream);
        await store.DisposeAsync();
        await archive.DisposeAsync();
        using ImageTarReader.LayerArchive tar = ImageTarReader.Open(blob);
        TarEntry entry = Assert.IsAssignableFrom<TarEntry>(await tar.Reader.GetNextEntryAsync(
            copyData: false, cancellationToken: Token));
        Assert.Equal("etc/example", entry.Name);
        using MemoryStream output = new();
        await entry.DataStream!.CopyToAsync(output, Token);
        Assert.Equal("fixture"u8.ToArray(), output.ToArray());
        Assert.Null(await tar.Reader.GetNextEntryAsync(copyData: false, cancellationToken: Token));
    }

    [Fact]
    public async Task WarmGzipIndexIsRetainedAndReusedWithoutReadingTheCacheAgain()
    {
        DockerArchiveFixture fixture = DockerArchiveFixture.Create(oci: true, gzip: true);
        await using DockerImageArchive prior = await OpenAsync(fixture);
        await using LayerStore store = new(Path.Combine(root, "cache"));
        string digest = prior.Resolved.Manifest.Layers[0].Digest!;
        await store.GetIndexAsync(prior, new(0, digest), fixture.StoredLayer.Length, Token);
        StoredLayerIndex? retained = null;
        await using DockerImageArchive archive = await DockerImageArchive.OpenAsync(
            new MemoryStream(fixture.Export), fixture.Id, Token, Path.Combine(root, "snapshots"),
            prepareFileSystem: true, hasLayerIndex: async (id, size, cancellationToken) =>
                retained = await store.GetValidIndexAsync(id, size, cancellationToken));
        Assert.NotNull(retained);
        Assert.Same(retained, archive.GetRetainedIndex(digest, fixture.StoredLayer.Length));
        Assert.Null(archive.GetPreparedIndex(digest, fixture.StoredLayer.Length));
        Assert.True(archive.TryGetVerifiedContentDigest(digest, fixture.StoredLayer.Length,
            prior.Configuration.RootFilesystem!.DiffIds[0], out _));
        string metadata = Assert.Single(Directory.GetFiles(Path.Combine(root, "cache"), "*.index",
            SearchOption.AllDirectories));
        File.Delete(metadata);

        List<long> progress = [];
        StoredLayerIndex reused = await store.GetIndexAsync(archive, new(0, digest),
            fixture.StoredLayer.Length, Token, new RecordingProgress(progress));
        Assert.Same(retained, reused);
        Assert.Equal([fixture.StoredLayer.Length], progress);
        Assert.False(File.Exists(metadata));
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.GetIndexAsync(
            archive, new(0, digest), fixture.StoredLayer.Length, cancellation.Token));
        await Assert.ThrowsAsync<InvalidDataException>(() => store.GetIndexAsync(
            archive, new(0, digest), fixture.StoredLayer.Length + 1, Token));
    }

    [Fact]
    public async Task GzipEntryValidationIsReusedOnlyForTheSameArchiveEntryAndPrefix()
    {
        DockerArchiveFixture fixture = DockerArchiveFixture.Create(oci: true, gzip: true);
        await using DockerImageArchive seed = await OpenAsync(fixture);
        await using LayerStore store = new(Path.Combine(root, "cache"));
        string digest = seed.Resolved.Manifest.Layers[0].Digest!;
        StoredLayerIndex index = await store.GetIndexAsync(seed, new(0, digest), fixture.StoredLayer.Length, Token);
        await using DockerImageArchive archive = await OpenAsync(fixture);
        ScannedEntry entry = Assert.Single(index.Changes.Entries);
        long prefix = entry.CompressedHighWaterMark;
        Assert.Single(archive.GetUnverifiedEntries(digest, index.Encoding, [entry], prefix));
        using (await store.OpenIndexedBlobAsync(archive, index, [entry.EntryIndex], Token)) { }
        Assert.Empty(archive.GetUnverifiedEntries(digest, index.Encoding, [entry], prefix));
        Assert.Single(archive.GetUnverifiedEntries(digest, index.Encoding, [entry], prefix - 1));
        Assert.Single(archive.GetUnverifiedEntries(digest, index.Encoding, [entry], prefix + 1));
        Assert.Single(archive.GetUnverifiedEntries(digest, "tar", [entry], prefix));
        Assert.Single(archive.GetUnverifiedEntries(digest, index.Encoding,
            [entry with { Size = entry.Size - 1 }], prefix));
        Assert.Single(archive.GetUnverifiedEntries(digest, index.Encoding,
            [entry with { UncompressedOffset = entry.UncompressedOffset + 1 }], prefix));
        ScannedEntry corrupt = entry with { ContentHash = new string('f', 64) };
        StoredLayerIndex invalid = new(digest, index.BlobLength, new([corrupt], [], []), index.Encoding);
        using (await store.OpenIndexedBlobAsync(archive, invalid, [entry.EntryIndex], Token)) { }
        Assert.Single(archive.GetUnverifiedEntries(digest, index.Encoding, [corrupt], prefix));
        await using DockerImageArchive other = await OpenAsync(fixture);
        Assert.Single(other.GetUnverifiedEntries(digest, index.Encoding, [entry], prefix));

        using Stream repeated = await store.OpenIndexedBlobAsync(archive, index, [entry.EntryIndex], Token);
        await store.DisposeAsync();
        await archive.DisposeAsync();
        using LayerContentReader reader = new(repeated, index.Encoding);
        using MemoryStream output = new();
        await reader.CopyToAsync(entry, output, Token);
        Assert.Equal("fixture"u8.ToArray(), output.ToArray());
    }

    [Fact]
    public async Task CanceledGzipSelectionDoesNotPublishAnEntryProof()
    {
        DockerArchiveFixture fixture = DockerArchiveFixture.Create(oci: true, gzip: true);
        await using DockerImageArchive seed = await OpenAsync(fixture);
        await using LayerStore store = new(Path.Combine(root, "cache"));
        string digest = seed.Resolved.Manifest.Layers[0].Digest!;
        StoredLayerIndex index = await store.GetIndexAsync(seed, new(0, digest), fixture.StoredLayer.Length, Token);
        await using DockerImageArchive archive = await OpenAsync(fixture);
        ScannedEntry entry = Assert.Single(index.Changes.Entries);
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            store.OpenIndexedBlobAsync(archive, index, [entry.EntryIndex], cancellation.Token));
        Assert.Single(archive.GetUnverifiedEntries(digest, index.Encoding, [entry], entry.CompressedHighWaterMark));
        Assert.False(File.Exists(store.GetBlobPath(digest)));
    }

    private Task<DockerImageArchive> OpenAsync(DockerArchiveFixture fixture) =>
        DockerImageArchive.OpenAsync(new MemoryStream(fixture.Export), fixture.Id, Token,
            Path.Combine(root, "snapshots"));

    public ValueTask DisposeAsync()
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
        return ValueTask.CompletedTask;
    }

    private sealed class RecordingProgress(List<long> values) : IProgress<long>
    {
        public void Report(long value) => values.Add(value);
    }
}
