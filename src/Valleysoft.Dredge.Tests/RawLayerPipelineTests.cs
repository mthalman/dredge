using System.Formats.Tar;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Valleysoft.DockerRegistryClient;
using Valleysoft.DockerRegistryClient.Models.Images;
using Valleysoft.DockerRegistryClient.Models.Manifests;
using Valleysoft.DockerRegistryClient.Models.Manifests.Docker;
using Valleysoft.Dredge.Commands;
using static Valleysoft.Dredge.Tests.LayerCacheTestContext;

namespace Valleysoft.Dredge.Tests;

public sealed class RawLayerPipelineTests : IAsyncDisposable
{
    private readonly string root = Path.Combine(Environment.CurrentDirectory, $"raw-layer-tests-{Guid.NewGuid():N}");
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private static readonly ImageName Name = ImageName.Parse("registry.test/repo:tag");

    [Theory]
    [InlineData("tar")]
    [InlineData("gzip")]
    public async Task EmptyProofValidationDoesNotReadOrAllocateAContentBuffer(string encoding)
    {
        var validate = typeof(LayerStore).GetMethod("ValidatePrefixAsync",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
            .CreateDelegate<Func<Stream, string, IReadOnlyList<ScannedEntry>, CancellationToken, Task>>();
        using ReadCountingStream input = new([]);
        await validate(input, encoding, [], Token);
        long start = GC.GetAllocatedBytesForCurrentThread();

        Task validation = validate(input, encoding, [], Token);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - start;
        await validation;

        Assert.Equal(0, input.BytesRead);
        Assert.True(input.CanRead);
        Assert.True(allocated < 4096, $"Empty validation allocated {allocated:N0} bytes.");
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => validate(input, encoding, [], cancellation.Token));
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, false, true)]
    [InlineData(false, true, false)]
    [InlineData(false, true, true)]
    [InlineData(true, false, false)]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    [InlineData(true, true, true)]
    public async Task CachedContentChecksEncodedAndDecodedDigestsInOnePass(bool gzip, bool sha512Blob, bool sha512Diff)
    {
        byte[] raw = [.. CreateTar(), .. new byte[131_073]];
        byte[] bytes = gzip ? Compress(raw) : raw;
        string digest = sha512Blob ? "sha512:" + Convert.ToHexStringLower(SHA512.HashData(bytes)) : Digest(bytes);
        string diffId = sha512Diff ? "sha512:" + Convert.ToHexStringLower(SHA512.HashData(raw)) : Digest(raw);
        using ReadCountingStream blob = new(bytes);

        var result = await LayerStore.VerifyCachedContentAsync(blob, digest, diffId, Token);

        Assert.True(result.Valid);
        Assert.Null(result.Failure);
        Assert.Equal(Digest(raw), result.Identity);
        Assert.InRange(blob.BytesRead, bytes.Length, bytes.Length + 2L);
        Assert.True(blob.CanRead);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task FusedCachedVerificationPreservesCorruptionRecoveryAndContentFailures(bool invalidGzip, bool corruptCache)
    {
        byte[] bytes = Compress(CreateTar());
        if (invalidGzip)
        {
            bytes[3] = 0xff;
        }
        TestSource source = new(bytes);
        string digest = Digest(bytes);
        string diffId = Digest([42]);
        string path;
        await using (LayerStore seed = new(root))
        {
            using (await seed.OpenBlobAsync(source, digest, bytes.Length, Token)) { }
            path = seed.GetBlobPath(digest);
        }
        if (corruptCache)
        {
            byte[] corrupt = [.. bytes];
            corrupt[3] = 0xff;
            corrupt[^1] ^= 0xff;
            await File.WriteAllBytesAsync(path, corrupt, Token);
        }
        using StringWriter diagnostics = new();
        await using LayerStore store = new(root, diagnostics: diagnostics);

        InvalidDataException failure = await Assert.ThrowsAsync<InvalidDataException>(() =>
            store.VerifyContentAsync(source, digest, bytes.Length, diffId, Token));

        Assert.Equal(corruptCache ? 2 : 1, source.FullRequests);
        Assert.Equal(corruptCache, diagnostics.ToString().Contains("Rebuilding corrupt layer blob", StringComparison.Ordinal));
        if (!invalidGzip)
        {
            Assert.Contains("rootfs diff ID", failure.Message);
        }
        Assert.Equal(bytes, await File.ReadAllBytesAsync(path, Token));
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            store.VerifyContentAsync(source, digest, bytes.Length, diffId, Token));
        Assert.Equal(corruptCache ? 2 : 1, source.FullRequests);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnownedPersistedContentUsesFusedVerificationAndRetainsSizeAndCancellationChecks(bool gzip)
    {
        byte[] raw = CreateTar();
        byte[] bytes = gzip ? Compress(raw) : raw;
        TestSource source = new(bytes);
        string digest = Digest(bytes);
        await using (LayerStore seed = new(root))
        {
            using (await seed.OpenBlobAsync(source, digest, bytes.Length, Token)) { }
        }
        await using LayerStore store = new(root);
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            store.VerifyContentAsync(source, digest, bytes.Length, Digest(raw), cancellation.Token));
        Assert.Equal(Digest(raw), await store.VerifyContentAsync(source, digest, bytes.Length, Digest(raw), Token));
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            store.VerifyContentAsync(source, digest, bytes.Length + 1, Digest(raw), Token));
        Assert.Equal(Digest(raw), await store.VerifyContentAsync(source, digest, bytes.Length, Digest(raw), Token));
        Assert.Equal(1, source.FullRequests);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    [InlineData(true, false)]
    public async Task PinnedContentReusesOnlyMatchingRawDigest(bool compressed, bool matchingDiffId)
    {
        byte[] raw = CreateTar();
        byte[] bytes = compressed ? Compress(raw) : raw;
        using ReadCountingStream blob = new(bytes);
        string diffId = matchingDiffId ? Digest(raw) : Digest([42]);
        if (matchingDiffId)
        {
            await LayerStore.VerifyPinnedContentAsync(blob, Digest(bytes), diffId, Token);
        }
        else
        {
            await Assert.ThrowsAsync<InvalidDataException>(() =>
                LayerStore.VerifyPinnedContentAsync(blob, Digest(bytes), diffId, Token));
        }

        if (!compressed && matchingDiffId)
        {
            Assert.InRange(blob.BytesRead, 1, 2);
            Assert.Equal(0, blob.Position);
        }
        else
        {
            Assert.True(blob.BytesRead >= bytes.Length);
        }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task Sha512ProofComputesCanonicalSha256InOneDecodedPass(bool compressed, bool sha512Blob)
    {
        byte[] raw = [.. CreateTar(), .. new byte[131_073]];
        byte[] bytes = compressed ? Compress(raw) : raw;
        string diffId = "sha512:" + Convert.ToHexStringLower(SHA512.HashData(raw));
        string blobDigest = sha512Blob ? "sha512:" + Convert.ToHexStringLower(SHA512.HashData(bytes)) : Digest(bytes);
        using ReadCountingStream blob = new(bytes);

        string canonical = await LayerStore.VerifyPinnedContentAsync(blob, blobDigest, diffId, Token);

        Assert.Equal(Digest(raw), canonical);
        Assert.InRange(blob.BytesRead, bytes.Length, bytes.Length + 4L);
        Assert.True(blob.CanRead);
    }

    [Fact]
    public async Task MatchingRawContentStillChecksSizeCancellationAndMemoization()
    {
        byte[] raw = CreateTar();
        TestSource source = new(raw);
        await using LayerStore store = new(root);
        string digest = Digest(raw);
        using (await store.OpenBlobAsync(source, digest, raw.Length, Token)) { }
        await store.VerifyContentAsync(source, digest, raw.Length, digest, Token);
        await store.VerifyContentAsync(source, digest, raw.Length, digest, Token);
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            store.VerifyContentAsync(source, digest, raw.Length + 1, digest, Token));
        using CancellationTokenSource canceled = new();
        canceled.Cancel();
        using ReadCountingStream blob = new(raw);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            LayerStore.VerifyPinnedContentAsync(blob, digest, digest, canceled.Token));
        Assert.Equal(0, blob.BytesRead);
        Assert.Equal(1, source.FullRequests);
    }

    [Fact]
    public async Task GzipContentNeverReusesItsEncodedDigestAsTheDiffId()
    {
        byte[] gzip = Compress(CreateTar());
        using ReadCountingStream blob = new(gzip);
        string digest = Digest(gzip);
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            LayerStore.VerifyPinnedContentAsync(blob, digest, digest, Token));
        Assert.True(blob.BytesRead >= gzip.Length);
    }

    [Fact]
    public async Task RawAndGzipHaveEquivalentIndexesAndTarContent()
    {
        byte[] raw = CreateTar();
        byte[] gzip = Compress(raw);
        using MemoryStream rawBlob = new(raw);
        using MemoryStream gzipBlob = new(gzip);
        LayerChanges rawChanges = await ImageLayerScanner.ScanAsync(rawBlob, new(0, Digest(raw)), Token);
        LayerChanges gzipChanges = await ImageLayerScanner.ScanAsync(gzipBlob, new(0, Digest(gzip)), Token);

        Assert.Equal(rawChanges.Whiteouts, gzipChanges.Whiteouts);
        Assert.Equal(rawChanges.OpaqueDirectories, gzipChanges.OpaqueDirectories);
        Assert.Equal(rawChanges.Entries, gzipChanges.Entries.Select((entry, index) =>
            entry with { CompressedHighWaterMark = rawChanges.Entries[index].CompressedHighWaterMark }));
        Assert.All(rawChanges.Entries, entry =>
            Assert.Equal(entry.UncompressedOffset + entry.Size, entry.CompressedHighWaterMark));
        Assert.All(gzipChanges.Entries, entry =>
            Assert.InRange(entry.CompressedHighWaterMark, 1, gzip.Length));

        foreach (byte[] bytes in new[] { raw, gzip })
        {
            using MemoryStream blob = new(bytes);
            using ImageTarReader.LayerArchive archive = ImageTarReader.Open(blob);
            TarEntry entry = (await ImageTarReader.GetNextEntryAsync(archive.Reader, new(0, Digest(bytes)), Token))!;
            using MemoryStream output = new();
            await ImageTarReader.CopyEntryAsync(entry, output, new(0, Digest(bytes)), entry.Name, Token);
            Assert.Equal("selected content", Encoding.UTF8.GetString(output.ToArray()));
        }
    }

    [Theory]
    [InlineData(false, "valid")]
    [InlineData(true, "valid")]
    [InlineData(false, "ignored")]
    [InlineData(false, "corrupt")]
    [InlineData(false, "truncated")]
    [InlineData(false, "wrong-range")]
    [InlineData(false, "wrong-total")]
    public async Task IndexedContentUsesEncodingAndPreservesRangeValidation(bool compressed, string response)
    {
        byte[] raw = CreateTar();
        byte[] bytes = compressed ? Compress(raw) : raw;
        TestSource source = new(bytes) { RangeResponse = response };
        StoredLayerIndex index;
        string blobPath;
        await using (LayerStore seed = new(root))
        {
            index = await seed.GetIndexAsync(source, new(0, Digest(bytes)), bytes.Length, Token);
            Assert.Equal(compressed ? "gzip" : "tar", index.Encoding);
            blobPath = seed.GetBlobPath(index.Digest);
        }
        File.Delete(blobPath);

        await using LayerStore store = new(root);
        ScannedEntry selected = index.Changes.Entries.First();
        using Stream blob = await store.OpenIndexedBlobAsync(source, index, [selected.EntryIndex], Token);
        using LayerContentReader reader = new(blob, index.Encoding);
        using MemoryStream output = new();
        await reader.CopyToAsync(selected, output, Token);

        Assert.Equal("selected content", Encoding.UTF8.GetString(output.ToArray()));
        Assert.Equal(1, source.RangeRequests);
        Assert.Equal(response is "valid" or "ignored" ? 1 : 2, source.FullRequests);
        Assert.Equal(response != "valid", File.Exists(store.GetBlobPath(index.Digest)));
    }

    [Fact]
    public async Task LegacyGzipIndexWithoutEncodingIsReusedAndRawIndexIsSeparate()
    {
        byte[] raw = CreateTar();
        byte[] gzip = Compress(raw);
        TestSource gzipSource = new(gzip);
        await using LayerStore store = new(root);
        StoredLayerIndex gzipIndex = await store.GetIndexAsync(gzipSource, new(0, Digest(gzip)), gzip.Length, Token);
        JsonObject legacy = JsonNode.Parse(JsonHelper.Serialize(gzipIndex, JsonHelper.CompactSettings))!.AsObject();
        Assert.True(legacy.Remove("encoding"));
        await store.WriteMetadataAsync(gzipIndex.Digest, "index", legacy, Token);
        StoredLayerIndex restored = await store.GetIndexAsync(gzipSource, new(0, Digest(gzip)), gzip.Length, Token);
        Assert.Equal("gzip", restored.Encoding);
        Assert.Equal(1, gzipSource.FullRequests);
        Assert.Equal(gzipIndex.Changes.Entries, restored.Changes.Entries);

        TestSource rawSource = new(raw);
        StoredLayerIndex rawIndex = await store.GetIndexAsync(rawSource, new(0, Digest(raw)), raw.Length, Token);
        Assert.Equal("tar", rawIndex.Encoding);
        Assert.NotEqual(gzipIndex.Digest, rawIndex.Digest);
        Assert.Equal(2, Directory.GetFiles(Path.Combine(root, "layer-store", "data"), "*.index").Length);
        LayerCacheEnvelope envelope = JsonHelper.Deserialize<LayerCacheEnvelope>(
            await File.ReadAllTextAsync(Directory.GetFiles(Path.Combine(root, "layer-store", "data"), "*.index")[0], Token))!;
        Assert.Equal(1, envelope.Version);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InvalidIndexEncodingIsRebuiltFromVerifiedBlob(bool raw)
    {
        byte[] bytes = raw ? CreateTar() : Compress(CreateTar());
        TestSource source = new(bytes);
        await using LayerStore store = new(root);
        StoredLayerIndex index = await store.GetIndexAsync(source, new(0, Digest(bytes)), bytes.Length, Token);
        await store.WriteMetadataAsync(index.Digest, "index",
            new StoredLayerIndex(index.Digest, index.BlobLength, index.Changes, "unsupported"), Token);

        StoredLayerIndex rebuilt = await store.GetIndexAsync(source, new(0, Digest(bytes)), bytes.Length, Token);

        Assert.Equal(raw ? "tar" : "gzip", rebuilt.Encoding);
        Assert.Equal(1, source.FullRequests);
    }

    [Theory]
    [InlineData("size")]
    [InlineData("digest")]
    [InlineData("range")]
    public async Task SourceDownloadsRetainFullBlobValidation(string invalid)
    {
        byte[] bytes = CreateTar();
        string digest = Digest(invalid == "digest" ? "wrong"u8.ToArray() : bytes);
        TestSource source = new(bytes) { RequestedDigest = digest, IncompleteFullRange = invalid == "range" };
        await using LayerStore store = new(root);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            store.OpenBlobAsync(source, digest, invalid == "size" ? bytes.Length + 1 : bytes.Length, Token));

        Assert.False(File.Exists(store.GetBlobPath(digest)));
    }

    [Theory]
    [InlineData(false, "selected")]
    [InlineData(true, "selected")]
    [InlineData(false, "/selected")]
    [InlineData(true, "/selected")]
    [InlineData(false, "//selected")]
    [InlineData(true, "//selected")]
    public async Task FileSystemCopiesExtractsAndRestoresRawAndGzipViews(bool compressed, string path)
    {
        byte[] raw = CreateTar();
        byte[] bytes = compressed ? Compress(raw) : raw;
        TestSource source = new(bytes);
        DockerManifest manifest = new()
        {
            Config = new ManifestConfig { Digest = Digest("config"u8.ToArray()) },
            Layers = [new ManifestLayer { Digest = Digest(bytes), Size = bytes.Length }]
        };
        ResolvedManifest resolved = new(
            new ManifestInfo("application/vnd.oci.image.manifest.v1+json", Digest("manifest"u8.ToArray()), manifest),
            manifest);
        Image config = new() { Os = "linux" };
        await using ResolvedImage image = new(ImageSourceReference.Parse(compressed ? "registry.test/repo:tag" : "docker-daemon:local"),
            Name, resolved, null, config, "{\"os\":\"linux\"}"u8.ToArray(), source, Digest(bytes), "linux/amd64");
        string blobPath;
        await using (LayerStore seed = new(root))
        {
            await seed.GetIndexAsync(source, new(0, Digest(bytes)), bytes.Length, Token);
            blobPath = seed.GetBlobPath(Digest(bytes));
        }
        File.Delete(blobPath);
        await using LayerStore store = new(root);
        await using ImageFileSystem files = await ImageFileSystem.CreateAsync(
            image, Token, store, requireLayerIndexes: true, extractionPath: path);
        Assert.DoesNotContain(files.List(null, true, true), entry => entry.Path.Contains(".wh.", StringComparison.Ordinal));
        using MemoryStream output = new();
        await files.CopyFileToAsync(path, output, Token);
        Assert.Equal("selected content", Encoding.UTF8.GetString(output.ToArray()));
        string destination = Path.Combine(root, "extracted");
        await files.ExtractAsync(path, destination, Token);
        Assert.Equal("selected content", await File.ReadAllTextAsync(destination, Token));
        await foreach (var result in files.ReadFilesAsync([("hard", 1024L)], Token))
        {
            Assert.Null(result.Error);
            Assert.Equal("selected content", Encoding.UTF8.GetString(result.Content!));
        }
        await using ImageFileSystem restored = await ImageFileSystem.CreateAsync(
            image, Token, store);
        Assert.Equal(files.List(null, true, true), restored.List(null, true, true));
        Assert.Equal(1, source.FullRequests);
        Assert.Equal(3, source.RangeRequests);
    }

    [Fact]
    public async Task FileSystemViewDoesNotMixRawAndGzipLayerCoordinates()
    {
        byte[] raw = CreateTar();
        await using LayerStore store = new(root);
        foreach (byte[] bytes in new[] { raw, Compress(raw), raw })
        {
            TestSource source = new(bytes);
            DockerManifest manifest = new()
            {
                Layers = [new ManifestLayer { Digest = Digest(bytes), Size = bytes.Length }]
            };
            ResolvedManifest resolved = new(
                new ManifestInfo("application/vnd.oci.image.manifest.v1+json", Digest("shared-view-key"u8.ToArray()), manifest),
                manifest);
            await using ResolvedImage image = new(ImageSourceReference.Parse("docker-daemon:local"), Name, resolved,
                null, new Image { Os = "linux" }, "{\"os\":\"linux\"}"u8.ToArray(), source, Digest(bytes), "linux/amd64");
            await using ImageFileSystem files = await ImageFileSystem.CreateAsync(image, Token, store);
            ImageFileSystemEntry selected = Assert.Single(files.List("selected", false, false));
            Assert.Equal(Digest(bytes), selected.IntroducedLayer.Digest);
            using MemoryStream output = new();
            await files.CopyFileToAsync("selected", output, Token);
            Assert.Equal("selected content", Encoding.UTF8.GetString(output.ToArray()));
        }
    }

    [Fact]
    public async Task WindowsRawLayersRequireGzipMediaType()
    {
        byte[] bytes = CreateTar();
        TestSource source = new(bytes);
        DockerManifest manifest = new() { Layers = [new ManifestLayer { Digest = Digest(bytes), Size = bytes.Length }] };
        ResolvedManifest resolved = new(
            new ManifestInfo("application/vnd.oci.image.manifest.v1+json", Digest("windows"u8.ToArray()), manifest),
            manifest);
        await using ResolvedImage image = new(ImageSourceReference.Parse("docker-daemon:local"), Name, resolved,
            null, new Image { Os = "windows" }, "{\"os\":\"windows\"}"u8.ToArray(), source, Digest(bytes), "windows/amd64");
        await using LayerStore store = new(root);

        NotSupportedException exception = await Assert.ThrowsAsync<NotSupportedException>(() =>
            ImageFileSystem.CreateAsync(image, Token, store));

        Assert.Contains("Unsupported Windows layer media type", exception.Message);
        Assert.Equal(0, source.FullRequests);
    }

    private static byte[] CreateTar()
    {
        using MemoryStream bytes = new();
        using (TarWriter tar = new(bytes, leaveOpen: true))
        {
            tar.WriteEntry(new UstarTarEntry(TarEntryType.RegularFile, "selected")
            {
                DataStream = new MemoryStream("selected content"u8.ToArray())
            });
            tar.WriteEntry(new UstarTarEntry(TarEntryType.Directory, "tree"));
            tar.WriteEntry(new UstarTarEntry(TarEntryType.RegularFile, ".wh.missing"));
            tar.WriteEntry(new UstarTarEntry(TarEntryType.RegularFile, "tree/.wh..wh..opq"));
            tar.WriteEntry(new UstarTarEntry(TarEntryType.HardLink, "hard") { LinkName = "selected" });
            tar.WriteEntry(new UstarTarEntry(TarEntryType.SymbolicLink, "symbolic") { LinkName = "selected" });
            byte[] tail = new byte[128 * 1024];
            new Random(42).NextBytes(tail);
            tar.WriteEntry(new UstarTarEntry(TarEntryType.RegularFile, "tail") { DataStream = new MemoryStream(tail) });
        }
        return bytes.ToArray();
    }

    private static byte[] Compress(byte[] raw)
    {
        using MemoryStream bytes = new();
        using (GZipStream gzip = new(bytes, CompressionMode.Compress, leaveOpen: true))
        {
            gzip.Write(raw);
        }
        return bytes.ToArray();
    }

    private sealed class TestSource(byte[] bytes) : IImageContentSource
    {
        public int FullRequests { get; private set; }
        public int RangeRequests { get; private set; }
        public string RangeResponse { get; init; } = "valid";
        public string? RequestedDigest { get; init; }
        public bool IncompleteFullRange { get; init; }

        public Task<BlobDownloadResult> GetRangeAsync(string digest, long start, long? length, CancellationToken cancellationToken)
        {
            Assert.Equal(RequestedDigest ?? Digest(bytes), digest);
            cancellationToken.ThrowIfCancellationRequested();
            if (length is null)
            {
                Assert.Equal(0, start);
                FullRequests++;
                if (IncompleteFullRange)
                {
                    return Task.FromResult(new BlobDownloadResult(new MemoryStream(bytes), true, 1, bytes.Length - 1, bytes.Length));
                }
                return Task.FromResult(new BlobDownloadResult(new MemoryStream(bytes), false, null, null, bytes.Length));
            }
            RangeRequests++;
            if (RangeResponse == "ignored")
            {
                return Task.FromResult(new BlobDownloadResult(new MemoryStream(bytes), false, null, null, bytes.Length));
            }
            byte[] prefix = bytes[checked((int)start)..checked((int)(start + length.Value))];
            if (RangeResponse == "corrupt")
            {
                prefix[0] ^= 0xff;
            }
            if (RangeResponse == "truncated")
            {
                prefix = prefix[..^1];
            }
            return Task.FromResult(new BlobDownloadResult(new MemoryStream(prefix), true,
                RangeResponse == "wrong-range" ? start + 1 : start, start + length - 1,
                RangeResponse == "wrong-total" ? bytes.Length + 1 : bytes.Length));
        }
    }

    public ValueTask DisposeAsync()
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
        return ValueTask.CompletedTask;
    }
}
