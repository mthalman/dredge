using System.Net;
using System.Text.Json;
using Moq;
using Valleysoft.DockerRegistryClient;

namespace Valleysoft.Dredge.Tests;

public sealed class IndexedRawRangeTests : IAsyncDisposable
{
    private readonly string root = Path.Combine(Environment.CurrentDirectory, $".raw-ranges-{Guid.NewGuid():N}");
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task IndexedContentKeepsAnImmutableMetadataSnapshot()
    {
        byte[] raw = DockerArchiveFixture.BuildTar(("file", new byte[] { 7 }));
        ArraySource source = new(raw);
        await using LayerStore store = new(root);
        using (await store.OpenBlobAsync(source, LayerCacheTestContext.Digest(raw), raw.Length, Token)) { }
        ScannedEntry[] entries =
        [
            new("file", ImageFileType.File, 0, 0, 0, 1, DateTime.UnixEpoch, null, 0,
                512, 513, LayerCacheTestContext.Digest([7])[7..])
        ];
        string[] whiteouts = ["removed"];
        string[] opaque = ["directory"];
        StoredLayerIndex index = new(LayerCacheTestContext.Digest(raw), raw.Length,
            new(entries, whiteouts, opaque), "tar");
        entries[0] = entries[0] with { Path = "../invalid", Size = long.MaxValue };
        whiteouts[0] = "../invalid";
        opaque[0] = "../invalid";

        for (int i = 0; i < 10; i++)
        {
            using Stream blob = await store.OpenIndexedBlobAsync(source, index, [0], Token);
            Assert.Equal(raw.Length, blob.Length);
        }
        Assert.Equal("file", index.Changes.Entries[0].Path);
        Assert.Equal("removed", index.Changes.Whiteouts[0]);
        Assert.Equal("directory", index.Changes.OpaqueDirectories[0]);
        Assert.False(index.Changes.Entries is ScannedEntry[]);
        Assert.Throws<NotSupportedException>(() =>
            ((IList<ScannedEntry>)index.Changes.Entries)[0] = entries[0]);
    }

    [Theory]
    [InlineData(128, 128, false)]
    [InlineData(129, 1, false)]
    [InlineData(10_000, 1, false)]
    [InlineData(129, 1, true)]
    public async Task FragmentedSubstantialSelectionsBoundRequestCost(int count, int requestCount, bool corruptFull)
    {
        const int fileSize = 1024;
        const int spacing = fileSize + 8192;
        byte[] raw = new byte[count * spacing];
        byte[] file = Enumerable.Repeat((byte)7, fileSize).ToArray();
        string hash = LayerCacheTestContext.Digest(file)[7..];
        ScannedEntry[] entries = [.. Enumerable.Range(0, count).Select(i =>
            new ScannedEntry($"file{i}", ImageFileType.File, 0, 0, 0, fileSize, DateTime.UnixEpoch,
                null, i, (long)i * spacing, (long)i * spacing + fileSize, hash))];
        foreach (ScannedEntry entry in entries)
        {
            file.CopyTo(raw, checked((int)entry.UncompressedOffset));
        }
        StoredLayerIndex index = new(LayerCacheTestContext.Digest(raw), raw.Length,
            new(entries, [], []), "tar");
        ArraySource content = new(raw) { CorruptFull = corruptFull };
        RecordingSource source = new(content);
        await using LayerStore store = new(root);
        if (corruptFull)
        {
            await Assert.ThrowsAsync<InvalidDataException>(() =>
                store.OpenIndexedBlobAsync(source, index, Enumerable.Range(0, count), Token));
            Assert.Equal((0L, (long?)null), Assert.Single(source.Requests));
            Assert.False(File.Exists(store.GetBlobPath(index.Digest)));
            Assert.All(content.Streams, stream => Assert.True(stream.Disposed));
            return;
        }
        using Stream blob = await store.OpenIndexedBlobAsync(source, index, Enumerable.Range(0, count), Token);

        Assert.Equal(requestCount, source.Requests.Count);
        Assert.Equal(count == requestCount ? (long)count * fileSize : raw.Length, source.BytesRead);
        Assert.Equal(count != requestCount, File.Exists(store.GetBlobPath(index.Digest)));
        if (count != requestCount)
        {
            Assert.Equal((0L, (long?)null), Assert.Single(source.Requests));
        }
        using LayerContentReader reader = new(blob, "tar");
        using MemoryStream output = new();
        await reader.CopyToAsync(entries[^1], output, Token);
        Assert.Equal(file, output.ToArray());
    }

    [Fact]
    public async Task HighlyFragmentedTinySelectionRetainsSparseRanges()
    {
        const int count = 129;
        const int spacing = 1024 * 1024;
        string hash = LayerCacheTestContext.Digest([7])[7..];
        ScannedEntry[] entries = [.. Enumerable.Range(0, count).Select(i =>
            new ScannedEntry($"file{i}", ImageFileType.File, 0, 0, 0, 1, DateTime.UnixEpoch,
                null, i, (long)i * spacing, (long)i * spacing + 1, hash))];
        StoredLayerIndex index = new(LayerCacheTestContext.Digest([42]), (long)count * spacing,
            new(entries, [], []), "tar");
        long bytesRead = 0;
        List<(long Start, long? Length)> requests = [];
        GeneratedSource source = new(index, requests, read => Interlocked.Add(ref bytesRead, read), spacing);
        await using LayerStore store = new(root);
        using Stream blob = await store.OpenIndexedBlobAsync(source, index, Enumerable.Range(0, count), Token);
        Assert.Equal(count, requests.Count);
        Assert.Equal(count, bytesRead);
        Assert.False(File.Exists(store.GetBlobPath(index.Digest)));
    }

    [Fact]
    public void RepeatedImmutableIndexValidationDoesNotRescanPaths()
    {
        ScannedEntry[] entries = [.. Enumerable.Range(0, 10_000).Select(i =>
            new ScannedEntry($"directory/child/file{i}", ImageFileType.File, 0, 0, 0, 0,
                DateTime.UnixEpoch, null, i, 0, 1, LayerCacheTestContext.Digest([])[7..]))];
        StoredLayerIndex index = new(LayerCacheTestContext.Digest([42]), 1024,
            new(entries, [], []), "tar");
        Assert.True(index.HasValidMetadata);
        long allocated = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 100; i++)
        {
            if (!index.HasValidMetadata)
            {
                throw new InvalidOperationException("The immutable index changed after validation.");
            }
        }
        Assert.InRange(GC.GetAllocatedBytesForCurrentThread() - allocated, 0, 1024);
    }

    [Fact]
    public async Task OneHundredThousandNearbyFilesUseBoundedCoalescedRanges()
    {
        const int count = 100_000;
        const int spacing = 1024;
        byte[] selected = [7];
        string hash = LayerCacheTestContext.Digest(selected)[7..];
        ScannedEntry[] entries = [.. Enumerable.Range(0, count).Select(i =>
            new ScannedEntry($"file{i}", ImageFileType.File, 0, 0, 0, 1, DateTime.UnixEpoch,
                null, i, (long)i * spacing, (long)i * spacing + 1, hash))];
        StoredLayerIndex index = new(LayerCacheTestContext.Digest([42]), (long)count * spacing,
            new(entries, [], []), "tar");
        long bytesRead = 0;
        List<(long Start, long? Length)> requests = [];
        GeneratedSource source = new(index, requests, read => Interlocked.Add(ref bytesRead, read), spacing);
        await using LayerStore store = new(root);
        using Stream blob = await store.OpenIndexedBlobAsync(source, index, Enumerable.Range(0, count), Token);

        Assert.Equal(98, requests.Count);
        Assert.All(requests, request => Assert.InRange(request.Length!.Value, 1, 1024 * 1024));
        Assert.Equal((long)(count - requests.Count) * spacing + requests.Count, bytesRead);
        Assert.Equal(count, new FileInfo(Assert.Single(
            Directory.GetFiles(Path.Combine(root, "layer-store", "data"), "*.tmp"))).Length);
        using LayerContentReader reader = new(blob, "tar");
        using MemoryStream output = new();
        await reader.CopyToAsync(entries[^1], output, Token);
        Assert.Equal(selected, output.ToArray());
    }

    [Theory]
    [InlineData(4096, 1)]
    [InlineData(4097, 2)]
    public async Task CoalescingGapBoundaryBoundsExtraDownloadedBytes(int gap, int requestCount)
    {
        ScannedEntry[] entries =
        [
            new("first", ImageFileType.File, 0, 0, 0, 1, DateTime.UnixEpoch, null, 0,
                0, 1, LayerCacheTestContext.Digest([7])[7..]),
            new("second", ImageFileType.File, 0, 0, 0, 1, DateTime.UnixEpoch, null, 1,
                gap + 1, gap + 2, LayerCacheTestContext.Digest([7])[7..])
        ];
        StoredLayerIndex index = new(LayerCacheTestContext.Digest([42]), gap + 2,
            new(entries, [], []), "tar");
        long bytesRead = 0;
        List<(long Start, long? Length)> requests = [];
        GeneratedSource source = new(index, requests, read => Interlocked.Add(ref bytesRead, read), gap + 1);
        await using LayerStore store = new(root);
        using Stream blob = await store.OpenIndexedBlobAsync(source, index, [0, 1], Token);
        Assert.Equal(requestCount, requests.Count);
        Assert.Equal(requestCount == 1 ? gap + 2 : 2, bytesRead);
    }

    [Fact]
    public async Task CachedIndexAtEightGiBDownloadsAndSpoolsOnlySelectedOneKiB()
    {
        const long offset = 8L * 1024 * 1024 * 1024;
        byte[] selected = Enumerable.Range(0, 1024).Select(value => (byte)value).ToArray();
        string digest = LayerCacheTestContext.Digest([42]);
        ScannedEntry entry = new("file", ImageFileType.File, 0, 0, 0, selected.Length,
            DateTime.UnixEpoch, null, 0, offset, offset + selected.Length,
            LayerCacheTestContext.Digest(selected)[7..]);
        StoredLayerIndex index = new(digest, offset + 2048, new([entry], [], []), "tar");
        ExtentSource source = new(index, selected);
        await using LayerStore store = new(root);
        await store.WriteMetadataAsync(digest, ImageFileSystemSemantics.LinuxIndexKind, index, Token);
        StoredLayerIndex restored = await store.GetIndexAsync(source, new(0, digest), index.BlobLength, Token);
        using (Stream blob = await store.OpenIndexedBlobAsync(source, restored, [0], Token))
        {
            Assert.Equal((offset, (long?)selected.Length), Assert.Single(source.Requests));
            Assert.Equal(1024, source.BytesRead);
            Assert.Equal(1024, new FileInfo(Assert.Single(
                Directory.GetFiles(Path.Combine(root, "layer-store", "data"), "*.tmp"))).Length);
            using (LayerContentReader reader = new(blob, "tar"))
            using (MemoryStream output = new())
            {
                await reader.CopyToAsync(entry, output, Token);
                Assert.Equal(selected, output.ToArray());
            }
            Assert.True(blob.CanRead);
            Assert.True(source.Disposed);
            Assert.False(File.Exists(store.GetBlobPath(digest)));
        }
        Assert.Empty(Directory.GetFiles(Path.Combine(root, "layer-store", "data"), "*.tmp"));
    }

    [Fact]
    public async Task MultipleSelectedFilesDownloadAndSpoolTheirExtentsWithoutEightGiBGap()
    {
        const long offset = 8L * 1024 * 1024 * 1024;
        byte[] first = [7, 8, 9];
        byte[] second = new byte[1024];
        ScannedEntry[] entries =
        [
            new("first", ImageFileType.File, 0, 0, 0, first.Length, DateTime.UnixEpoch, null, 0,
                512, 512 + first.Length, LayerCacheTestContext.Digest(first)[7..]),
            new("second", ImageFileType.File, 0, 0, 0, second.Length, DateTime.UnixEpoch, null, 1,
                offset, offset + second.Length, LayerCacheTestContext.Digest(second)[7..])
        ];
        StoredLayerIndex index = new(LayerCacheTestContext.Digest([42]), offset + 2048,
            new(entries, [], []), "tar");
        ExtentSource source = new(index, [], new Dictionary<long, byte[]>
        {
            [512] = first, [offset] = second
        });
        await using LayerStore store = new(root);
        using (Stream blob = await store.OpenIndexedBlobAsync(source, index, [0, 1], Token))
        {
            Assert.Equal(new (long, long?)[] { (512, first.Length), (offset, second.Length) }, source.Requests);
            Assert.Equal(first.Length + second.Length, source.BytesRead);
            Assert.Equal(first.Length + second.Length, new FileInfo(Assert.Single(
                Directory.GetFiles(Path.Combine(root, "layer-store", "data"), "*.tmp"))).Length);
            Assert.Throws<InvalidDataException>(() => blob.Seek(4096, SeekOrigin.Begin));
            using LayerContentReader reader = new(blob, "tar");
            using MemoryStream output = new();
            await reader.CopyToAsync(entries[1], output, Token);
            Assert.Equal(second, output.ToArray());
            output.SetLength(0);
            await reader.CopyToAsync(entries[0], output, Token);
            Assert.Equal(first, output.ToArray());
        }
        Assert.True(source.Disposed);
        Assert.Empty(Directory.GetFiles(Path.Combine(root, "layer-store", "data"), "*.tmp"));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(5)]
    public async Task EmptyRawFileValidatesHashWithoutMakingZeroLengthRequest(int count)
    {
        ScannedEntry[] entries = [.. Enumerable.Range(0, count).Select(i =>
            new ScannedEntry($"empty{i}", ImageFileType.File, 0, 0, 0, 0, DateTime.UnixEpoch,
                null, i, (i + 1) * 512, (i + 1) * 512, LayerCacheTestContext.Digest([])[7..]))];
        StoredLayerIndex index = new(LayerCacheTestContext.Digest([42]), (count + 1) * 512, new(entries, [], []), "tar");
        ExtentSource source = new(index, []);
        await using LayerStore store = new(root);
        using Stream blob = await store.OpenIndexedBlobAsync(source, index, Enumerable.Range(0, count), Token);
        using LayerContentReader reader = new(blob, "tar");
        using MemoryStream output = new();
        foreach (ScannedEntry entry in entries)
        {
            await reader.CopyToAsync(entry, output, Token);
        }
        Assert.Empty(source.Requests);
        Assert.Empty(output.ToArray());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RawRegistryAndDaemonAdaptersReturnVerifiedSelectedExtent(bool daemon)
    {
        CacheFileSystem.CreateDirectory(root);
        byte[] selected = [1, 2, 3, 4];
        byte[] raw = DockerArchiveFixture.BuildTar(("file", selected));
        string digest = LayerCacheTestContext.Digest(raw);
        byte[] configuration = JsonSerializer.SerializeToUtf8Bytes(new
        {
            os = "linux", architecture = "amd64",
            rootfs = new { type = "layers", diff_ids = new[] { digest } }
        });
        byte[] manifest = JsonSerializer.SerializeToUtf8Bytes(new[]
        {
            new { Config = "config.json", Layers = new[] { "layer.tar" } }
        });
        using MemoryStream export = new(DockerArchiveFixture.BuildTar(
            ("config.json", configuration), ("layer.tar", raw), ("manifest.json", manifest)));
        await using DockerImageArchive? archive = daemon
            ? await DockerImageArchive.OpenAsync(export, LayerCacheTestContext.Digest(configuration),
                Token, scratchRoot: Path.Combine(root, "snapshots"))
            : null;
        Mock<IDockerRegistryClient> client = new() { DefaultValue = DefaultValue.Mock };
        client.Setup(value => value.Blobs.GetRangeAsync(
            "image", digest, It.IsAny<long>(), It.IsAny<long?>(), It.IsAny<CancellationToken>()))
            .Returns((string _, string _, long start, long? length, CancellationToken cancellationToken) =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                long count = length ?? raw.Length - start;
                return Task.FromResult(new BlobDownloadResult(
                    new MemoryStream(raw, checked((int)start), checked((int)count)),
                    true, start, start + count - 1, raw.Length));
            });
        RecordingSource source = new(archive is not null ? archive :
            new RegistryImageContentSource(client.Object, ImageName.Parse("registry.test/image:tag")));
        StoredLayerIndex index;
        string path;
        await using (LayerStore seed = new(root))
        {
            index = await seed.GetIndexAsync(source, new(0, digest), raw.Length, Token);
            path = seed.GetBlobPath(digest);
        }
        File.Delete(path);
        source.Requests.Clear();
        source.BytesRead = 0;
        await using LayerStore store = new(root);
        ScannedEntry entry = Assert.Single(index.Changes.Entries);
        using Stream blob = await store.OpenIndexedBlobAsync(source, index, [entry.EntryIndex], Token);
        using LayerContentReader reader = new(blob, "tar");
        using MemoryStream output = new();
        await reader.CopyToAsync(entry, output, Token);
        Assert.Equal(selected, output.ToArray());
        Assert.Equal((entry.UncompressedOffset, (long?)selected.Length), Assert.Single(source.Requests));
        Assert.Equal(selected.Length, source.BytesRead);
        Assert.False(File.Exists(path));
    }

    [Theory]
    [InlineData("corrupt")]
    [InlineData("truncated")]
    [InlineData("excess")]
    [InlineData("wrong-start")]
    [InlineData("wrong-end")]
    [InlineData("wrong-total")]
    [InlineData("ignored")]
    [InlineData("unsatisfiable")]
    public async Task InvalidOrIgnoredRawRangeFallsBackToDigestVerifiedFullBlob(string response)
    {
        byte[] selected = [1, 2, 3, 4];
        byte[] raw = DockerArchiveFixture.BuildTar(("file", selected));
        ArraySource source = new(raw) { Response = response };
        StoredLayerIndex index;
        string path;
        await using (LayerStore seed = new(root))
        {
            index = await seed.GetIndexAsync(source, new(0, LayerCacheTestContext.Digest(raw)), raw.Length, Token);
            path = seed.GetBlobPath(index.Digest);
        }
        File.Delete(path);
        source.FullDownloads = 0;
        await using LayerStore store = new(root);
        ScannedEntry entry = Assert.Single(index.Changes.Entries);
        using Stream blob = await store.OpenIndexedBlobAsync(source, index, [entry.EntryIndex], Token);
        using LayerContentReader reader = new(blob, "tar");
        using MemoryStream output = new();
        await reader.CopyToAsync(entry, output, Token);
        Assert.Equal(selected, output.ToArray());
        Assert.Equal(response == "ignored" ? 0 : 1, source.FullDownloads);
        Assert.Equal(raw, await File.ReadAllBytesAsync(path, Token));
        Assert.All(source.Streams, stream => Assert.True(stream.Disposed));
        Assert.Empty(Directory.GetFiles(Path.Combine(root, "layer-store", "data"), "*.tmp"));
    }

    [Fact]
    public async Task CorruptRangeAndCorruptFallbackNeverReturnUnverifiedContent()
    {
        byte[] raw = DockerArchiveFixture.BuildTar(("file", new byte[] { 1, 2, 3 }));
        ArraySource source = new(raw);
        StoredLayerIndex index;
        string path;
        await using (LayerStore seed = new(root))
        {
            index = await seed.GetIndexAsync(source, new(0, LayerCacheTestContext.Digest(raw)), raw.Length, Token);
            path = seed.GetBlobPath(index.Digest);
        }
        File.Delete(path);
        source.Response = "corrupt";
        source.CorruptFull = true;
        await using LayerStore store = new(root);
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            store.OpenIndexedBlobAsync(source, index, [0], Token));
        Assert.False(File.Exists(path));
        Assert.All(source.Streams, stream => Assert.True(stream.Disposed));
        Assert.Empty(Directory.GetFiles(Path.Combine(root, "layer-store", "data"), "*.tmp"));
    }

    [Theory]
    [InlineData("valid")]
    [InlineData("corrupt")]
    [InlineData("truncated")]
    [InlineData("excess")]
    [InlineData("ignored")]
    [InlineData("unsatisfiable")]
    public async Task CoalescedRangeVerifiesEveryFileAndFallsBackOnInvalidResponse(string response)
    {
        byte[] raw = DockerArchiveFixture.BuildTar(("first", new byte[] { 1, 2, 3 }),
            ("second", new byte[] { 4, 5, 6 }));
        ArraySource content = new(raw);
        StoredLayerIndex index;
        string path;
        await using (LayerStore seed = new(root))
        {
            index = await seed.GetIndexAsync(content, new(0, LayerCacheTestContext.Digest(raw)), raw.Length, Token);
            path = seed.GetBlobPath(index.Digest);
        }
        File.Delete(path);
        content.Response = response;
        content.FullDownloads = 0;
        RecordingSource source = new(content);
        await using LayerStore store = new(root);
        using Stream blob = await store.OpenIndexedBlobAsync(source, index, [0, 1], Token);
        using (LayerContentReader reader = new(blob, "tar"))
        using (MemoryStream output = new())
        {
            await reader.CopyToAsync(index.Changes.Entries[1], output, Token);
            Assert.Equal(new byte[] { 4, 5, 6 }, output.ToArray());
            output.SetLength(0);
            await reader.CopyToAsync(index.Changes.Entries[0], output, Token);
            Assert.Equal(new byte[] { 1, 2, 3 }, output.ToArray());
        }
        long start = index.Changes.Entries[0].UncompressedOffset;
        long length = index.Changes.Entries[1].UncompressedOffset + 3 - start;
        Assert.Equal((start, (long?)length), source.Requests[0]);
        Assert.Equal(response is "valid" or "ignored" ? 1 : 2, source.Requests.Count);
        Assert.Equal(response is "valid" or "ignored" ? 0 : 1, content.FullDownloads);
        if (response == "valid")
        {
            Assert.Equal(length, source.BytesRead);
            Assert.False(File.Exists(path));
        }
        else
        {
            Assert.Equal(raw, await File.ReadAllBytesAsync(path, Token));
        }
        Assert.All(content.Streams, stream => Assert.True(stream.Disposed));
    }

    [Fact]
    public async Task CoalescedFilesCrossBufferedWriteBoundariesWithoutMixingHashesOrOffsets()
    {
        byte[] first = Enumerable.Range(0, 90_000).Select(i => (byte)i).ToArray();
        byte[] second = Enumerable.Range(0, 100_000).Select(i => (byte)(255 - i)).ToArray();
        byte[] raw = DockerArchiveFixture.BuildTar(("first", first), ("second", second));
        ArraySource source = new(raw);
        StoredLayerIndex index;
        string path;
        await using (LayerStore seed = new(root))
        {
            index = await seed.GetIndexAsync(source, new(0, LayerCacheTestContext.Digest(raw)), raw.Length, Token);
            path = seed.GetBlobPath(index.Digest);
        }
        File.Delete(path);
        await using LayerStore store = new(root);
        using Stream blob = await store.OpenIndexedBlobAsync(source, index, [0, 1], Token);
        using LayerContentReader reader = new(blob, "tar");
        using MemoryStream output = new();
        await reader.CopyToAsync(index.Changes.Entries[1], output, Token);
        Assert.Equal(second, output.ToArray());
        output.SetLength(0);
        await reader.CopyToAsync(index.Changes.Entries[0], output, Token);
        Assert.Equal(first, output.ToArray());
        Assert.False(File.Exists(path));
        Assert.All(source.Streams, stream => Assert.True(stream.Disposed));
    }

    [Fact]
    public async Task CancellationDisposesRangeAndScratchWithoutDownloadingFallback()
    {
        byte[] raw = DockerArchiveFixture.BuildTar(("file", new byte[] { 1, 2, 3 }));
        ArraySource source = new(raw);
        StoredLayerIndex index;
        string path;
        await using (LayerStore seed = new(root))
        {
            index = await seed.GetIndexAsync(source, new(0, LayerCacheTestContext.Digest(raw)), raw.Length, Token);
            path = seed.GetBlobPath(index.Digest);
        }
        File.Delete(path);
        using CancellationTokenSource cancellation = CancellationTokenSource.CreateLinkedTokenSource(Token);
        source.CancelRead = cancellation;
        source.FullDownloads = 0;
        await using LayerStore store = new(root);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            store.OpenIndexedBlobAsync(source, index, [0], cancellation.Token));
        Assert.Equal(0, source.FullDownloads);
        Assert.All(source.Streams, stream => Assert.True(stream.Disposed));
        Assert.Empty(Directory.GetFiles(Path.Combine(root, "layer-store", "data"), "*.tmp"));
        Assert.False(File.Exists(path));
    }

    [Theory]
    [InlineData(-1, 1)]
    [InlineData(0, -1)]
    [InlineData(99, 2)]
    [InlineData(long.MaxValue, 1)]
    public async Task InvalidRawIndexBoundsFailBeforeRequest(long offset, long size)
    {
        ScannedEntry entry = new("file", ImageFileType.File, 0, 0, 0, size, DateTime.UnixEpoch,
            null, 0, offset, 100, new string('0', 64));
        StoredLayerIndex index = new(LayerCacheTestContext.Digest([1]), 100, new([entry], [], []), "tar");
        ExtentSource source = new(index, [1]);
        await using LayerStore store = new(root);
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            store.OpenIndexedBlobAsync(source, index, [0], Token));
        Assert.Empty(source.Requests);
    }

    public ValueTask DisposeAsync()
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
        return ValueTask.CompletedTask;
    }

    private sealed class ExtentSource(
        StoredLayerIndex index, byte[] bytes, IReadOnlyDictionary<long, byte[]>? extents = null) : IImageContentSource
    {
        public List<(long Start, long? Length)> Requests { get; } = [];
        private long bytesRead;
        public long BytesRead => bytesRead;
        public bool Disposed { get; private set; }
        public Task<BlobDownloadResult> GetRangeAsync(
            string digest, long start, long? length, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.Equal(index.Digest, digest);
            lock (Requests)
            {
                Requests.Add((start, length));
            }
            ScannedEntry entry = Assert.Single(index.Changes.Entries, entry => entry.UncompressedOffset == start);
            byte[] selected = extents is null ? bytes : extents[start];
            Assert.Equal(entry.Size, length);
            Assert.Equal(selected.LongLength, length);
            Stream stream = new TrackingStream(new MemoryStream(selected),
                read => Interlocked.Add(ref bytesRead, read), () => Disposed = true);
            return Task.FromResult(new BlobDownloadResult(
                stream, true, start, start + selected.Length - 1, index.BlobLength));
        }
    }

    private sealed class RecordingSource(IImageContentSource source) : IImageContentSource
    {
        public List<(long Start, long? Length)> Requests { get; } = [];
        private long bytesRead;
        public long BytesRead { get => bytesRead; set => bytesRead = value; }
        public async Task<BlobDownloadResult> GetRangeAsync(
            string digest, long start, long? length, CancellationToken cancellationToken)
        {
            lock (Requests)
            {
                Requests.Add((start, length));
            }
            BlobDownloadResult result = await source.GetRangeAsync(digest, start, length, cancellationToken);
            return new(new TrackingStream(result.Content, read => Interlocked.Add(ref bytesRead, read)),
                result.IsRangeHonored, result.RangeStart, result.RangeEnd, result.TotalLength);
        }
    }

    private sealed class GeneratedSource(
        StoredLayerIndex index, List<(long Start, long? Length)> requests,
        Action<int> read, int spacing) : IImageContentSource
    {
        public Task<BlobDownloadResult> GetRangeAsync(
            string digest, long start, long? length, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.Equal(index.Digest, digest);
            Assert.NotNull(length);
            lock (requests)
            {
                requests.Add((start, length));
            }
            byte[] bytes = new byte[checked((int)length.Value)];
            for (int i = 0; i < bytes.Length; i++)
            {
                if ((start + i) % spacing == 0)
                {
                    bytes[i] = 7;
                }
            }
            return Task.FromResult(new BlobDownloadResult(
                new TrackingStream(new MemoryStream(bytes), read), true,
                start, start + length - 1, index.BlobLength));
        }
    }

    private sealed class ArraySource(byte[] bytes) : IImageContentSource
    {
        private readonly string expectedDigest = LayerCacheTestContext.Digest(bytes);
        public string Response { get; set; } = "valid";
        public bool CorruptFull { get; set; }
        public CancellationTokenSource? CancelRead { get; set; }
        public int FullDownloads { get; set; }
        public List<TrackingStream> Streams { get; } = [];
        public Task<BlobDownloadResult> GetRangeAsync(
            string digest, long start, long? length, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.Equal(expectedDigest, digest);
            if (length.HasValue && Response == "unsatisfiable")
            {
                return Task.FromException<BlobDownloadResult>(
                    new RegistryException { StatusCode = HttpStatusCode.RequestedRangeNotSatisfiable });
            }
            byte[] result;
            bool full = length is null || Response == "ignored";
            if (full)
            {
                if (length is null)
                {
                    FullDownloads++;
                }
                result = [.. bytes];
                if (CorruptFull)
                {
                    result[0] ^= 1;
                }
            }
            else
            {
                result = bytes[checked((int)start)..checked((int)(start + length!.Value))];
                if (Response == "corrupt")
                {
                    result[0] ^= 1;
                }
                if (Response == "truncated")
                {
                    result = result[..^1];
                }
                if (Response == "excess")
                {
                    result = [.. result, 0];
                }
            }
            TrackingStream stream = new(new MemoryStream(result), _ => CancelRead?.Cancel());
            lock (Streams)
            {
                Streams.Add(stream);
            }
            return Task.FromResult(new BlobDownloadResult(stream, !full,
                full ? null : Response == "wrong-start" ? start + 1 : start,
                full ? null : Response == "wrong-end" ? start + length : start + length - 1,
                Response == "wrong-total" && !full ? bytes.Length + 1 : bytes.Length));
        }
    }

    private sealed class TrackingStream(Stream inner, Action<int> read, Action? disposed = null) : Stream
    {
        public bool Disposed { get; private set; }
        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }
        public override int Read(byte[] buffer, int offset, int count)
        {
            int result = inner.Read(buffer, offset, count);
            read(result);
            return result;
        }
        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            int result = await inner.ReadAsync(buffer, cancellationToken);
            read(result);
            return result;
        }
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                Disposed = true;
                inner.Dispose();
                disposed?.Invoke();
            }
            base.Dispose(disposing);
        }
    }
}
