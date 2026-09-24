using System.Formats.Tar;
using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Valleysoft.DockerRegistryClient;

namespace Valleysoft.Dredge.Tests;

public sealed class LayerStoreTests
{
    private static readonly ImageName Image = ImageName.Parse("registry.test/repo:tag");
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Processes_ShareOneDownloadAndPersistMetadata()
    {
        await using LayerCacheTestContext cache = new();
        byte[] bytes = CreateLayer();
        await File.WriteAllBytesAsync(Path.Combine(cache.Root, "source"), bytes, Token);
        await Task.WhenAll(RunChildAsync(cache.Root), RunChildAsync(cache.Root));
        await RunChildAsync(cache.Root);
        Assert.Single(await File.ReadAllLinesAsync(Path.Combine(cache.Root, "downloads"), Token));
        Assert.Single(Directory.GetFiles(DataPath(cache), "*.blob"));
        Assert.Single(Directory.GetFiles(DataPath(cache), "*.index"));
        Assert.Single(Directory.GetFiles(DataPath(cache), "*.view"));
    }

    [Fact]
    public async Task Processes_AbandonedWriterReleasesLockAndStaging()
    {
        await using LayerCacheTestContext cache = new();
        await File.WriteAllBytesAsync(Path.Combine(cache.Root, "source"), CreateLayer(), Token);
        await RunChildAsync(cache.Root, abandon: true);
        Assert.Empty(Directory.GetFiles(DataPath(cache), "*.blob"));
        Assert.Single(Directory.GetFiles(DataPath(cache), "*.tmp"));
        await RunChildAsync(cache.Root);
        Assert.Single(Directory.GetFiles(DataPath(cache), "*.blob"));
        Assert.Empty(Directory.GetFiles(DataPath(cache), "*.tmp"));
    }

    [Fact]
    public async Task ProcessWorker()
    {
        string? root = Environment.GetEnvironmentVariable("DREDGE_TEST_CACHE_WORKER");
        if (root is null)
        {
            return;
        }
        byte[] bytes = await File.ReadAllBytesAsync(Path.Combine(root, "source"), Token);
        string digest = LayerCacheTestContext.Digest(bytes);
        Mock<IDockerRegistryClient> client = Client(bytes);
        client.Setup(c => c.Blobs.GetAsync(Image.Repo, digest, It.IsAny<CancellationToken>()))
            .Returns(async () =>
            {
                await File.AppendAllTextAsync(Path.Combine(root, "downloads"), "download\n", Token);
                await Task.Delay(200, Token);
                return Environment.GetEnvironmentVariable("DREDGE_TEST_ABANDON_WRITE") == "1"
                    ? new AbandonedWriteStream(bytes)
                    : new MemoryStream(bytes);
            });
        await using LayerStore store = new(Path.Combine(root, "cache"));
        StoredLayerIndex index = await store.GetIndexAsync(client.Object, Image, new(0, digest), bytes.Length, Token);
        using Stream blob = await store.OpenIndexedBlobAsync(client.Object, Image, index, [0], Token);
        Assert.Equal(bytes, LayerCacheTestContext.ReadBytes(blob));
        await store.WriteMetadataAsync(digest, "view", new[] { "complete" }, Token);
        Assert.Equal(["complete"], Assert.IsType<string[]>(await store.ReadMetadataAsync<string[]>(digest, "view", Token)));
    }

    private static async Task RunChildAsync(string root, bool abandon = false)
    {
        ProcessStartInfo start = new("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        start.ArgumentList.Add(typeof(LayerStoreTests).Assembly.Location);
        start.ArgumentList.Add("-method");
        start.ArgumentList.Add($"{typeof(LayerStoreTests).FullName}.{nameof(ProcessWorker)}");
        start.ArgumentList.Add("-noLogo");
        start.Environment["DREDGE_TEST_CACHE_WORKER"] = root;
        start.Environment["DREDGE_TEST_ABANDON_WRITE"] = abandon ? "1" : "0";
        using Process process = Process.Start(start)!;
        Task<string> output = process.StandardOutput.ReadToEndAsync(Token);
        Task<string> error = process.StandardError.ReadToEndAsync(Token);
        try
        {
            await process.WaitForExitAsync(Token).WaitAsync(TimeSpan.FromSeconds(60), Token);
            Assert.True(process.ExitCode == 0, $"{await output}\n{await error}");
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(CancellationToken.None);
            }
        }
    }

    [Theory]
    [InlineData("0", 0L)]
    [InlineData("5368709120", 5368709120L)]
    [InlineData("9223372036854775807", long.MaxValue)]
    public void Settings_ParseByteLimit(string setting, long expected) =>
        Assert.Equal(expected, new CacheSettings { MaxBytes = setting }.GetMaxBytes());

    [Theory]
    [InlineData("-1")]
    [InlineData("unlimited")]
    [InlineData("")]
    [InlineData("9223372036854775808")]
    public void Settings_RejectInvalidByteLimit(string value) =>
        Assert.Throws<InvalidOperationException>(() => new CacheSettings { MaxBytes = value }.GetMaxBytes());

    [Fact]
    public async Task Blob_IsSharedAcrossStoresAndConcurrentDownloads()
    {
        await using LayerCacheTestContext cache = new();
        byte[] bytes = CreateLayer();
        string digest = LayerCacheTestContext.Digest(bytes);
        Mock<IDockerRegistryClient> client = Client(bytes);
        int downloads = 0;
        client.Setup(c => c.Blobs.GetAsync(Image.Repo, digest, It.IsAny<CancellationToken>()))
            .Returns(async () =>
            {
                Interlocked.Increment(ref downloads);
                await Task.Delay(75, Token);
                return new MemoryStream(bytes);
            });
        await using LayerStore second = new(cache.Paths.CachePath);
        Task<Stream> firstRead = cache.Store.OpenBlobAsync(client.Object, Image, digest, bytes.Length, Token);
        Task<Stream> secondRead = second.OpenBlobAsync(client.Object, Image, digest, bytes.Length, Token);
        using Stream first = await firstRead;
        using Stream other = await secondRead;
        Assert.Equal(bytes, LayerCacheTestContext.ReadBytes(first));
        Assert.Equal(bytes, LayerCacheTestContext.ReadBytes(other));
        Assert.Equal(1, downloads);
        Assert.Empty(Directory.GetFiles(DataPath(cache), "*.tmp"));
    }

    [Fact]
    public async Task Blob_RejectsWrongDigestAndCleansStaging()
    {
        await using LayerCacheTestContext cache = new();
        byte[] bytes = CreateLayer();
        string digest = LayerCacheTestContext.Digest(bytes);
        Mock<IDockerRegistryClient> client = Client(bytes);
        client.Setup(c => c.Blobs.GetAsync(Image.Repo, digest, It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new MemoryStream(new byte[bytes.Length]));
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            cache.Store.OpenBlobAsync(client.Object, Image, digest, bytes.Length, Token));
        Assert.Empty(Directory.GetFiles(DataPath(cache)));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Blob_RebuildsTruncatedOrSameLengthCorruption(bool truncate)
    {
        await using LayerCacheTestContext cache = new();
        byte[] bytes = CreateLayer();
        string digest = LayerCacheTestContext.Digest(bytes);
        Mock<IDockerRegistryClient> client = Client(bytes);
        using (await cache.Store.OpenBlobAsync(client.Object, Image, digest, bytes.Length, Token)) { }
        await cache.Store.DisposeAsync();
        await File.WriteAllBytesAsync(cache.Store.GetBlobPath(digest),
            new byte[truncate ? 1 : bytes.Length], Token);
        await using LayerStore second = new(cache.Paths.CachePath);
        using Stream result = await second.OpenBlobAsync(client.Object, Image, digest, bytes.Length, Token);
        Assert.Equal(bytes, LayerCacheTestContext.ReadBytes(result));
        client.Verify(c => c.Blobs.GetAsync(Image.Repo, digest, It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [Theory]
    [InlineData(0L)]
    [InlineData(1L)]
    public async Task Budget_PinsOversizedBlobUntilOperationCompletes(long budget)
    {
        await using LayerCacheTestContext cache = new(budget);
        byte[] bytes = CreateLayer();
        string digest = LayerCacheTestContext.Digest(bytes);
        Mock<IDockerRegistryClient> client = Client(bytes);
        StoredLayerIndex index = await cache.Store.GetIndexAsync(client.Object, Image, new(0, digest), bytes.Length, Token);
        using (Stream read = await cache.Store.OpenIndexedBlobAsync(client.Object, Image, index, [0], Token))
        {
            Assert.True(read.CanRead);
        }
        client.Verify(c => c.Blobs.GetAsync(Image.Repo, digest, It.IsAny<CancellationToken>()), Times.Once);
        await cache.Store.DisposeAsync();
        Assert.True(new DirectoryInfo(DataPath(cache)).GetFiles().Sum(file => file.Length) <= budget);
    }

    [Fact]
    public async Task Budget_EvictsBlobBeforeIndex()
    {
        await using LayerCacheTestContext cache = new(32 * 1024);
        byte[] bytes = CreateLayer(256 * 1024);
        string digest = LayerCacheTestContext.Digest(bytes);
        await cache.Store.GetIndexAsync(Client(bytes).Object, Image, new(0, digest), bytes.Length, Token);
        await cache.Store.DisposeAsync();
        Assert.Empty(Directory.GetFiles(DataPath(cache), "*.blob"));
        Assert.Single(Directory.GetFiles(DataPath(cache), "*.index"));
        Assert.True(new DirectoryInfo(DataPath(cache)).GetFiles().Sum(file => file.Length) <= 32 * 1024);
    }

    [Theory]
    [InlineData("range")]
    [InlineData("ignored")]
    [InlineData("shifted")]
    [InlineData("short")]
    [InlineData("damaged")]
    public async Task IndexedRead_ValidatesRangesAndSupportsIgnoredRange(string response)
    {
        await using LayerCacheTestContext cache = new();
        byte[] bytes = CreateLayer(256 * 1024);
        string digest = LayerCacheTestContext.Digest(bytes);
        Mock<IDockerRegistryClient> client = Client(bytes);
        StoredLayerIndex index = await cache.Store.GetIndexAsync(client.Object, Image, new(0, digest), bytes.Length, Token);
        ScannedEntry entry = index.Changes.Entries[0];
        Assert.True(entry.CompressedHighWaterMark < bytes.Length / 2);
        await cache.EvictBlobsAsync();
        client.Setup(c => c.Blobs.GetRangeAsync(Image.Repo, digest, 0,
            entry.CompressedHighWaterMark, It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                int length = checked((int)entry.CompressedHighWaterMark);
                byte[] prefix = response == "ignored" ? bytes : bytes[..(response == "short" ? length - 1 : length)];
                if (response == "damaged")
                {
                    prefix[0] = 0;
                }
                return new BlobDownloadResult(new MemoryStream(prefix),
                    response != "ignored", response == "shifted" ? 1 : 0, length - 1, bytes.Length);
            });
        using Stream blob = await cache.Store.OpenIndexedBlobAsync(client.Object, Image, index, [entry.EntryIndex], Token);
        using GZipStream gzip = new(blob, CompressionMode.Decompress);
        using MemoryStream content = new();
        await LayerStore.CopyBytesAsync(gzip, Stream.Null, entry.UncompressedOffset, new byte[4096], Token);
        await LayerStore.CopyBytesAsync(gzip, content, entry.Size, new byte[4096], Token);
        Assert.Equal("selected content", Encoding.UTF8.GetString(content.ToArray()));
        client.Verify(c => c.Blobs.GetRangeAsync(Image.Repo, digest, 0,
            entry.CompressedHighWaterMark, It.IsAny<CancellationToken>()), Times.Once);
        client.Verify(c => c.Blobs.GetAsync(Image.Repo, digest, It.IsAny<CancellationToken>()),
            Times.Exactly(response is "range" or "ignored" ? 1 : 2));
        Assert.Equal(response != "range", File.Exists(cache.Store.GetBlobPath(digest)));
    }

    [Theory]
    [InlineData("truncated")]
    [InlineData("version")]
    [InlineData("checksum")]
    public async Task Index_RebuildsInvalidMetadataWithoutRedownloadingBlob(string damage)
    {
        await using LayerCacheTestContext cache = new();
        byte[] bytes = CreateLayer();
        string digest = LayerCacheTestContext.Digest(bytes);
        Mock<IDockerRegistryClient> client = Client(bytes);
        StoredLayerIndex original = await cache.Store.GetIndexAsync(client.Object, Image, new(0, digest), bytes.Length, Token);
        string path = Assert.Single(Directory.GetFiles(DataPath(cache), "*.index"));
        JsonObject json = JsonNode.Parse(await File.ReadAllTextAsync(path, Token))!.AsObject();
        if (damage == "version")
        {
            json["Version"] = -1;
        }
        else
        {
            json["Checksum"] = "incorrect";
        }
        await File.WriteAllTextAsync(path, damage == "truncated" ? "{" : json.ToJsonString(), Token);
        StoredLayerIndex rebuilt = await cache.Store.GetIndexAsync(client.Object, Image, new(8, digest), bytes.Length, Token);
        Assert.Equal(original.Changes.Entries, rebuilt.Changes.Entries);
        client.Verify(c => c.Blobs.GetAsync(Image.Repo, digest, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Theory]
    [InlineData(HttpStatusCode.RequestedRangeNotSatisfiable)]
    [InlineData(HttpStatusCode.Unauthorized)]
    public async Task IndexedRead_FallsBackOnlyForUnsatisfiableRange(HttpStatusCode status)
    {
        await using LayerCacheTestContext cache = new();
        byte[] bytes = CreateLayer();
        string digest = LayerCacheTestContext.Digest(bytes);
        Mock<IDockerRegistryClient> client = Client(bytes);
        StoredLayerIndex index = await cache.Store.GetIndexAsync(client.Object, Image, new(0, digest), bytes.Length, Token);
        await cache.EvictBlobsAsync();
        client.Setup(c => c.Blobs.GetRangeAsync(Image.Repo, digest, 0, It.IsAny<long?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new RegistryException { StatusCode = status });
        if (status == HttpStatusCode.Unauthorized)
        {
            await Assert.ThrowsAsync<RegistryException>(() =>
                cache.Store.OpenIndexedBlobAsync(client.Object, Image, index, [0], Token));
        }
        else
        {
            using Stream blob = await cache.Store.OpenIndexedBlobAsync(client.Object, Image, index, [0], Token);
            Assert.Equal(bytes, LayerCacheTestContext.ReadBytes(blob));
        }
        client.Verify(c => c.Blobs.GetAsync(Image.Repo, digest, It.IsAny<CancellationToken>()),
            Times.Exactly(status == HttpStatusCode.Unauthorized ? 1 : 2));
    }

    [Fact]
    public async Task Index_RecordsEmptyFilesLongNamesAndDuplicateOrdinals()
    {
        await using LayerCacheTestContext cache = new();
        using MemoryStream compressed = new();
        string path = new('a', 200);
        using (GZipStream gzip = new(compressed, CompressionMode.Compress, leaveOpen: true))
        using (TarWriter writer = new(gzip, leaveOpen: true))
        {
            foreach (string content in new[] { "", "duplicate" })
            {
                writer.WriteEntry(new GnuTarEntry(TarEntryType.RegularFile, path)
                {
                    DataStream = new MemoryStream(Encoding.UTF8.GetBytes(content))
                });
            }
        }
        byte[] bytes = compressed.ToArray();
        string digest = LayerCacheTestContext.Digest(bytes);
        Mock<IDockerRegistryClient> client = Client(bytes);
        StoredLayerIndex index = await cache.Store.GetIndexAsync(client.Object, Image, new(0, digest), bytes.Length, Token);
        Assert.Equal([0, 1], index.Changes.Entries.Select(entry => entry.EntryIndex));
        using Stream blob = await cache.Store.OpenIndexedBlobAsync(client.Object, Image, index, [0, 1], Token);
        using LayerContentReader reader = new(blob);
        using MemoryStream result = new();
        foreach (ScannedEntry entry in index.Changes.Entries)
        {
            await reader.CopyToAsync(entry, result, Token);
        }
        Assert.Equal("duplicate", Encoding.UTF8.GetString(result.ToArray()));
    }

    [Fact]
    public async Task Cache_RejectsLinkedDataDirectoryWithoutTouchingTarget()
    {
        await using LayerCacheTestContext cache = new();
        string external = Path.Combine(cache.Root, "outside");
        Directory.CreateDirectory(external);
        string marker = Path.Combine(external, "keep");
        await File.WriteAllTextAsync(marker, "unchanged", Token);
        string data = DataPath(cache);
        Directory.Delete(data);
        Directory.CreateSymbolicLink(data, external);
        try
        {
            Assert.Throws<IOException>(() => new LayerStore(cache.Paths.CachePath));
            Assert.Equal("unchanged", await File.ReadAllTextAsync(marker, Token));
        }
        finally
        {
            Directory.Delete(data);
            CacheFileSystem.CreateDirectory(data);
        }
    }

    [Fact]
    public async Task Clear_PreservesLeasedBlobsAndUnrelatedFiles()
    {
        await using LayerCacheTestContext cache = new();
        byte[] bytes = CreateLayer();
        string digest = LayerCacheTestContext.Digest(bytes);
        using (await cache.Store.OpenBlobAsync(Client(bytes).Object, Image, digest, bytes.Length, Token)) { }
        string unrelated = Path.Combine(cache.Paths.CachePath, "unrelated.txt");
        await File.WriteAllTextAsync(unrelated, "keep", Token);
        await using LayerStore second = new(cache.Paths.CachePath);
        Assert.Equal(0, await second.ClearAsync(Token));
        Assert.True(File.Exists(cache.Store.GetBlobPath(digest)));
        await cache.Store.DisposeAsync();
        Assert.Equal(bytes.Length, await second.ClearAsync(Token));
        Assert.True(File.Exists(unrelated));
    }

    [Fact]
    public async Task ScratchDirectory_IsLeasedUntilOperationDisposal()
    {
        await using LayerCacheTestContext cache = new();
        string directory = await cache.Store.CreateScratchDirectoryAsync(Token);
        await File.WriteAllTextAsync(Path.Combine(directory, "file"), "content", Token);
        await using LayerStore other = new(cache.Paths.CachePath);

        Assert.Equal(0, await other.ClearAsync(Token));
        Assert.True(File.Exists(Path.Combine(directory, "file")));
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute,
                File.GetUnixFileMode(directory));
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute,
                File.GetUnixFileMode(Path.GetDirectoryName(directory)!));
        }

        await cache.Store.DisposeAsync();
        Assert.False(Directory.Exists(directory));
        Assert.Empty(Directory.GetFiles(DataPath(cache), "*.scratch"));
    }

    [Fact]
    public async Task Clear_RemovesAbandonedScratchWithoutFollowingLinks()
    {
        await using LayerCacheTestContext cache = new();
        string marker = Path.Combine(DataPath(cache), $"{Guid.NewGuid():N}.scratch");
        using (CacheFileSystem.CreateFile(marker)) { }
        string scratch = $"{marker}.dir";
        CacheFileSystem.CreateDirectory(scratch);
        await File.WriteAllBytesAsync(Path.Combine(scratch, "partial"), [1, 2, 3, 4], Token);
        string external = Path.Combine(cache.Root, "outside");
        Directory.CreateDirectory(external);
        string externalFile = Path.Combine(external, "keep");
        await File.WriteAllTextAsync(externalFile, "unchanged", Token);
        Directory.CreateSymbolicLink(Path.Combine(scratch, "link"), external);

        Assert.Equal(4, await cache.Store.ClearAsync(Token));
        Assert.False(Directory.Exists(scratch));
        Assert.False(File.Exists(marker));
        Assert.Equal("unchanged", await File.ReadAllTextAsync(externalFile, Token));
    }

    [Fact]
    public async Task Cancellation_DuringDownloadLeavesNoPublishedEntry()
    {
        await using LayerCacheTestContext cache = new();
        byte[] bytes = CreateLayer();
        string digest = LayerCacheTestContext.Digest(bytes);
        Mock<IDockerRegistryClient> client = Client(bytes);
        client.Setup(c => c.Blobs.GetAsync(Image.Repo, digest, It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new CancelingStream(bytes));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            cache.Store.OpenBlobAsync(client.Object, Image, digest, bytes.Length, Token));
        Assert.Empty(Directory.GetFiles(DataPath(cache)));
    }

    [Fact]
    public async Task Cancellation_WhileWaitingForWriterDoesNotBreakItsLock()
    {
        await using LayerCacheTestContext cache = new();
        byte[] bytes = CreateLayer();
        string digest = LayerCacheTestContext.Digest(bytes);
        Mock<IDockerRegistryClient> client = Client(bytes);
        TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        client.Setup(c => c.Blobs.GetAsync(Image.Repo, digest, It.IsAny<CancellationToken>()))
            .Returns(async () =>
            {
                started.SetResult();
                await release.Task.WaitAsync(Token);
                return new MemoryStream(bytes);
            });
        Task<Stream> writer = cache.Store.OpenBlobAsync(client.Object, Image, digest, bytes.Length, Token);
        await started.Task.WaitAsync(Token);
        try
        {
            await using LayerStore second = new(cache.Paths.CachePath);
            using CancellationTokenSource canceled = new();
            canceled.CancelAfter(100);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                second.OpenBlobAsync(client.Object, Image, digest, bytes.Length, canceled.Token));
        }
        finally
        {
            release.SetResult();
            (await writer).Dispose();
        }
        Assert.True(File.Exists(cache.Store.GetBlobPath(digest)));
    }

    private static string DataPath(LayerCacheTestContext cache) =>
        Path.Combine(cache.Paths.CachePath, "layer-store", "data");

    internal static byte[] CreateLayer(int tailSize = 0)
    {
        using MemoryStream bytes = new();
        using (GZipStream gzip = new(bytes, CompressionMode.Compress, leaveOpen: true))
        using (TarWriter tar = new(gzip, leaveOpen: true))
        {
            tar.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, "selected")
            {
                ModificationTime = DateTimeOffset.UnixEpoch,
                DataStream = new MemoryStream("selected content"u8.ToArray())
            });
            if (tailSize > 0)
            {
                byte[] tail = new byte[tailSize];
                new Random(42).NextBytes(tail);
                tar.WriteEntry(new GnuTarEntry(TarEntryType.RegularFile, "unused-tail")
                {
                    ModificationTime = DateTimeOffset.UnixEpoch,
                    DataStream = new MemoryStream(tail)
                });
            }
        }
        return bytes.ToArray();
    }

    private static Mock<IDockerRegistryClient> Client(byte[] bytes)
    {
        Mock<IDockerRegistryClient> client = new() { DefaultValue = DefaultValue.Mock };
        client.Setup(c => c.Blobs.GetAsync(Image.Repo,
            LayerCacheTestContext.Digest(bytes), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new MemoryStream(bytes));
        return client;
    }

    private sealed class CancelingStream(byte[] bytes) : MemoryStream(bytes)
    {
        public override Task CopyToAsync(Stream destination, int bufferSize, CancellationToken cancellationToken) =>
            Task.FromException(new OperationCanceledException());
    }

    private sealed class AbandonedWriteStream(byte[] bytes) : MemoryStream(bytes)
    {
        public override async Task CopyToAsync(Stream destination, int bufferSize, CancellationToken cancellationToken)
        {
            await destination.WriteAsync(new byte[] { 1, 2, 3 }, cancellationToken);
            await destination.FlushAsync(cancellationToken);
            Environment.Exit(0);
        }
    }
}
