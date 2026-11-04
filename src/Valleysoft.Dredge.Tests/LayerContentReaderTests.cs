using System.IO.Compression;
using System.Formats.Tar;
using System.Security.Cryptography;
using Valleysoft.DockerRegistryClient;

namespace Valleysoft.Dredge.Tests;

public sealed class LayerContentReaderTests
{
    [Fact]
    public async Task RawLargeOffsetSeeksWithoutReadingDiscardedBytesAndLeavesBlobOpen()
    {
        const long offset = 8L * 1024 * 1024 * 1024;
        using VirtualBlob blob = new(offset + 1024);
        using MemoryStream output = new();
        using (LayerContentReader reader = new(blob, "tar"))
        {
            await reader.CopyToAsync(Entry(offset, 1024), output, Token);
            Assert.Equal(1024, blob.BytesRead);
            Assert.Equal([offset], blob.Seeks);
            Assert.Equal(1024, output.Length);
            output.SetLength(0);
            await reader.CopyToAsync(Entry(0, 1), output, Token);
            Assert.Equal(1025, blob.BytesRead);
        }
        Assert.False(blob.Disposed);
    }

    [Theory]
    [InlineData(-1, 1)]
    [InlineData(0, -1)]
    [InlineData(long.MaxValue, 1)]
    [InlineData(99, 2)]
    public async Task RawInvalidBoundsFailBeforeReadingOrWriting(long offset, long size)
    {
        using VirtualBlob blob = new(100);
        using LayerContentReader reader = new(blob, "tar");
        using MemoryStream output = new();
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            reader.CopyToAsync(Entry(offset, size), output, Token));
        Assert.Equal(0, blob.BytesRead);
        Assert.Empty(blob.Seeks);
        Assert.Empty(output.ToArray());
    }

    [Fact]
    public async Task RawCancellationDoesNotSeekEvenForEmptyContent()
    {
        using VirtualBlob blob = new(100);
        using LayerContentReader reader = new(blob, "tar");
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            reader.CopyToAsync(Entry(100, 0), Stream.Null, cancellation.Token));
        Assert.Empty(blob.Seeks);
    }

    [Fact]
    public async Task GzipRemainsSequentialRejectsBackwardOffsetsAndLeavesBlobOpen()
    {
        byte[] raw = Enumerable.Range(0, 128).Select(value => (byte)value).ToArray();
        using MemoryStream blob = new();
        using (GZipStream gzip = new(blob, CompressionMode.Compress, leaveOpen: true))
        {
            gzip.Write(raw);
        }
        blob.Position = 0;
        using MemoryStream output = new();
        using (LayerContentReader reader = new(blob, "gzip"))
        {
            await reader.CopyToAsync(Entry(64, 16), output, Token);
            Assert.Equal(raw[64..80], output.ToArray());
            await Assert.ThrowsAsync<InvalidDataException>(() =>
                reader.CopyToAsync(Entry(32, 1), output, Token));
            await Assert.ThrowsAsync<InvalidDataException>(() =>
                reader.CopyToAsync(Entry(127, 2), output, Token));
        }
        Assert.True(blob.CanRead);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task IndexedPrefixStillRequiresContentHashValidationBeforeSeeking(bool gzip)
    {
        string root = Path.Combine(Directory.GetCurrentDirectory(), $".reader-prefix-{Guid.NewGuid():N}");
        try
        {
            using MemoryStream tar = new();
            using (TarWriter writer = new(tar, leaveOpen: true))
            using (MemoryStream data = new([1, 2, 3]))
            {
                writer.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, "file") { DataStream = data });
            }
            byte[] raw = tar.ToArray();
            using MemoryStream compressed = new();
            using (GZipStream encoder = new(compressed, CompressionMode.Compress, leaveOpen: true))
            {
                encoder.Write(raw);
            }
            byte[] bytes = gzip ? compressed.ToArray() : raw;
            string digest = "sha256:" + Convert.ToHexStringLower(SHA256.HashData(bytes));
            PrefixContentSource source = new(digest, bytes);
            StoredLayerIndex index;
            string blobPath;
            await using (LayerStore seed = new(root))
            {
                index = await seed.GetIndexAsync(source, new(0, digest), bytes.Length, Token);
                blobPath = seed.GetBlobPath(digest);
            }
            File.Delete(blobPath);
            ScannedEntry entry = Assert.Single(index.Changes.Entries);
            long start = gzip ? 0 : entry.UncompressedOffset;
            long length = gzip ? entry.CompressedHighWaterMark : entry.Size;
            byte[] corrupt = bytes[checked((int)start)..checked((int)(start + length))];
            corrupt[0] ^= 1;
            source.Prefix = corrupt;
            source.RangeStart = start;
            await using LayerStore store = new(root);
            using Stream blob = await store.OpenIndexedBlobAsync(
                source, index, [entry.EntryIndex], Token);
            using LayerContentReader reader = new(blob, index.Encoding);
            using MemoryStream output = new();
            await reader.CopyToAsync(entry, output, Token);
            Assert.Equal(new byte[] { 1, 2, 3 }, output.ToArray());
            Assert.Equal(2, source.FullDownloads);
            Assert.Equal(1, source.PrefixDownloads);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static ScannedEntry Entry(long offset, long size) =>
        new("file", ImageFileType.File, 0, 0, 0, size, DateTime.UnixEpoch, null, 0,
            offset, 1, new string('0', 64));

    private sealed class PrefixContentSource(string digest, byte[] bytes) : IImageContentSource
    {
        public byte[]? Prefix { get; set; }
        public long RangeStart { get; set; }
        public int FullDownloads { get; private set; }
        public int PrefixDownloads { get; private set; }

        public Task<BlobDownloadResult> GetRangeAsync(
            string requestedDigest, long start, long? length, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.Equal(digest, requestedDigest);
            if (length is null)
            {
                Assert.Equal(0, start);
                FullDownloads++;
                return Task.FromResult(new BlobDownloadResult(
                    new MemoryStream(bytes), false, null, null, bytes.Length));
            }
            byte[] prefix = Assert.IsType<byte[]>(Prefix);
            Assert.Equal(RangeStart, start);
            Assert.Equal(prefix.LongLength, length.Value);
            PrefixDownloads++;
            return Task.FromResult(new BlobDownloadResult(
                new MemoryStream(prefix), true, start, start + length.Value - 1, bytes.Length));
        }
    }

    private sealed class VirtualBlob(long length) : Stream
    {
        public long BytesRead { get; private set; }
        public List<long> Seeks { get; } = [];
        public bool Disposed { get; private set; }
        public override bool CanRead => !Disposed;
        public override bool CanSeek => !Disposed;
        public override bool CanWrite => false;
        public override long Length => length;
        public override long Position { get; set; }
        public override int Read(byte[] buffer, int offset, int count) =>
            Read(buffer.AsSpan(offset, count));
        public override int Read(Span<byte> buffer)
        {
            int count = (int)Math.Min(buffer.Length, Length - Position);
            buffer[..count].Fill(42);
            Position += count;
            BytesRead += count;
            return count;
        }
        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(Read(buffer.Span));
        }
        public override long Seek(long offset, SeekOrigin origin)
        {
            Assert.Equal(SeekOrigin.Begin, origin);
            Seeks.Add(offset);
            return Position = offset;
        }
        protected override void Dispose(bool disposing)
        {
            Disposed = true;
            base.Dispose(disposing);
        }
        public override void Flush() => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
