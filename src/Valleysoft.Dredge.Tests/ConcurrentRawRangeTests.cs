using System.Net;
using Valleysoft.DockerRegistryClient;

namespace Valleysoft.Dredge.Tests;

public sealed class ConcurrentRawRangeTests : IAsyncDisposable
{
    private readonly string root = Path.Combine(Environment.CurrentDirectory, $".concurrent-ranges-{Guid.NewGuid():N}");
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SparseRangesUseFourWorkersAndPreserveSelectedBytesAndOrder(bool failBeforeRelease)
    {
        ConcurrentSource source = new(1000, 1024 * 1024);
        await using LayerStore store = new(root);
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        Task<Stream> opening = store.OpenIndexedBlobAsync(
            source, source.Index, Enumerable.Range(0, 1000).Reverse(), timeout.Token);
        async Task CheckPendingAsync()
        {
            try
            {
                await source.BodiesStarted.Task.WaitAsync(timeout.Token);
                Assert.False(opening.IsCompleted);
                Assert.Equal(4, source.Active);
                if (failBeforeRelease)
                {
                    Assert.Fail("Simulated assertion failure before releasing range bodies.");
                }
            }
            finally
            {
                source.Release.TrySetResult();
                await ((Task)opening).ConfigureAwait(
                    ConfigureAwaitOptions.ContinueOnCapturedContext | ConfigureAwaitOptions.SuppressThrowing);
            }
        }
        try
        {
            if (failBeforeRelease)
            {
                await Assert.ThrowsAsync<Xunit.Sdk.FailException>(CheckPendingAsync);
            }
            else
            {
                await CheckPendingAsync();
            }
            using Stream blob = await opening;
            Assert.Equal(1000, source.Requests);
            Assert.Equal(4, source.MaximumActive);
            Assert.Equal(1000 * 4096L, source.BytesRead);
            Assert.Equal(1000, source.EndsRead);
            Assert.Equal(0, source.Active);
            Assert.Equal(source.Opened, source.Disposed);
            Assert.Equal(0, source.FullRequests);
            Assert.False(File.Exists(store.GetBlobPath(source.Index.Digest)));
            Assert.Equal(1000 * 4096L, new FileInfo(Assert.Single(
                Directory.GetFiles(Path.Combine(root, "layer-store", "data"), "*.tmp"))).Length);
            using LayerContentReader reader = new(blob, "tar");
            foreach (int ordinal in new[] { 999, 500, 0, 137 })
            {
                using MemoryStream output = new();
                await reader.CopyToAsync(source.Index.Changes.Entries[ordinal], output, Token);
                Assert.Equal(ConcurrentSource.FileBytes(ordinal), output.ToArray());
            }
        }
        finally
        {
            if (opening.IsCompletedSuccessfully)
            {
                (await opening).Dispose();
            }
        }
        Assert.Empty(Directory.GetFiles(Path.Combine(root, "layer-store", "data"), "*.tmp"));
    }

    [Fact]
    public async Task CancellationDrainsAllRangeBodiesBeforeRemovingScratch()
    {
        ConcurrentSource source = new(8, 1024 * 1024);
        source.BodyDisposed = () => Assert.NotEmpty(
            Directory.GetFiles(Path.Combine(root, "layer-store", "data"), "*.tmp"));
        await using LayerStore store = new(root);
        using CancellationTokenSource cancellation = CancellationTokenSource.CreateLinkedTokenSource(Token);
        cancellation.CancelAfter(TimeSpan.FromSeconds(30));
        Task<Stream> opening = store.OpenIndexedBlobAsync(
            source, source.Index, Enumerable.Range(0, 8), cancellation.Token);
        await source.BodiesStarted.Task.WaitAsync(cancellation.Token);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => opening);

        Assert.Equal(4, source.Requests);
        Assert.Equal(0, source.Active);
        Assert.Equal(source.Opened, source.Disposed);
        Assert.Equal(0, source.FullRequests);
        Assert.False(File.Exists(store.GetBlobPath(source.Index.Digest)));
        Assert.Empty(Directory.GetFiles(Path.Combine(root, "layer-store", "data"), "*.tmp"));
    }

    [Theory]
    [InlineData("wrong-range", false)]
    [InlineData("wrong-range", true)]
    [InlineData("wrong-total", false)]
    [InlineData("wrong-total", true)]
    [InlineData("corrupt", false)]
    [InlineData("corrupt", true)]
    [InlineData("truncated", false)]
    [InlineData("truncated", true)]
    [InlineData("excess", false)]
    [InlineData("excess", true)]
    [InlineData("unsatisfiable", false)]
    [InlineData("unsatisfiable", true)]
    [InlineData("ignored", false)]
    [InlineData("ignored", true)]
    [InlineData("corrupt-full", false)]
    [InlineData("corrupt-full", true)]
    [InlineData("io", false)]
    [InlineData("io", true)]
    public async Task FallbackDrainsSiblingBodiesAndReusesAnIgnoredRangeResponse(string response, bool failBeforeRelease)
    {
        ConcurrentSource source = new(8, 16384, response);
        source.BodyDisposed = () => Assert.NotEmpty(
            Directory.GetFiles(Path.Combine(root, "layer-store", "data"), "*.tmp"));
        using StringWriter diagnostics = new();
        await using LayerStore store = new(root, diagnostics: diagnostics);
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        Task<Stream> opening = store.OpenIndexedBlobAsync(
            source, source.Index, Enumerable.Range(0, 8), timeout.Token);
        async Task CheckPendingAsync()
        {
            try
            {
                await source.BodiesStarted.Task.WaitAsync(timeout.Token);
                Assert.Equal(4, source.Active);
                if (failBeforeRelease)
                {
                    Assert.Fail("Simulated assertion failure before releasing range bodies.");
                }
            }
            finally
            {
                source.Release.TrySetResult();
                await ((Task)opening).ConfigureAwait(
                    ConfigureAwaitOptions.ContinueOnCapturedContext | ConfigureAwaitOptions.SuppressThrowing);
            }
        }
        try
        {
            if (failBeforeRelease)
            {
                await Assert.ThrowsAsync<Xunit.Sdk.FailException>(CheckPendingAsync);
            }
            else
            {
                await CheckPendingAsync();
            }
            if (response is "corrupt-full" or "io")
            {
                if (response == "io")
                {
                    await Assert.ThrowsAsync<IOException>(() => opening);
                }
                else
                {
                    await Assert.ThrowsAsync<InvalidDataException>(() => opening);
                }
                Assert.False(File.Exists(store.GetBlobPath(source.Index.Digest)));
            }
            else
            {
                using Stream blob = await opening;
                using MemoryStream output = new();
                await blob.CopyToAsync(output, Token);
                Assert.Equal(source.Raw, output.ToArray());
                Assert.Equal(source.Raw, await File.ReadAllBytesAsync(store.GetBlobPath(source.Index.Digest), Token));
            }
        }
        finally
        {
            if (opening.IsCompletedSuccessfully)
            {
                (await opening).Dispose();
            }
        }
        Assert.Equal(4, source.Requests);
        Assert.Equal(response is "ignored" or "io" ? 0 : 1, source.FullRequests);
        Assert.Equal(0, source.Active);
        Assert.Equal(source.Opened, source.Disposed);
        Assert.Empty(Directory.GetFiles(Path.Combine(root, "layer-store", "data"), "*.tmp"));
        if (response is not ("ignored" or "io"))
        {
            Assert.Contains(source.Index.Digest, diagnostics.ToString());
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

    private sealed class ConcurrentSource : IImageContentSource
    {
        private readonly int spacing;
        private readonly string response;
        private readonly TaskCompletionSource allRequests = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource never = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly object sync = new();
        private int bodiesStarted;
        private int requests;
        private int active;
        private int maximumActive;
        private int opened;
        private int disposed;
        private int endsRead;
        private long bytesRead;
        public StoredLayerIndex Index { get; }
        public byte[] Raw { get; }
        public TaskCompletionSource BodiesStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Requests => requests;
        public int Active => active;
        public int MaximumActive => maximumActive;
        public int Opened => opened;
        public int Disposed => disposed;
        public int EndsRead => endsRead;
        public long BytesRead => bytesRead;
        public int FullRequests { get; private set; }
        public Action? BodyDisposed { get; set; }

        public ConcurrentSource(int count, int spacing, string response = "valid")
        {
            this.spacing = spacing;
            this.response = response;
            Raw = response == "valid" ? [] : new byte[count * spacing];
            ScannedEntry[] entries = [.. Enumerable.Range(0, count).Select(i =>
            {
                byte[] bytes = FileBytes(i);
                if (Raw.Length != 0)
                {
                    bytes.CopyTo(Raw, i * spacing);
                }
                return new ScannedEntry($"file{i}", ImageFileType.File, 0, 0, 0, bytes.Length,
                    DateTime.UnixEpoch, null, i, (long)i * spacing, (long)i * spacing + bytes.Length,
                    LayerCacheTestContext.Digest(bytes)[7..]);
            })];
            Index = new(LayerCacheTestContext.Digest(Raw), (long)count * spacing, new(entries, [], []), "tar");
        }

        public static byte[] FileBytes(int ordinal) => Enumerable.Repeat((byte)(ordinal % 251 + 1), 4096).ToArray();

        public async Task<BlobDownloadResult> GetRangeAsync(
            string digest, long start, long? length, CancellationToken cancellationToken)
        {
            Assert.Equal(Index.Digest, digest);
            cancellationToken.ThrowIfCancellationRequested();
            if (length is null)
            {
                Assert.Equal(0, Active);
                FullRequests++;
                byte[] bytes = [.. Raw];
                if (response == "corrupt-full")
                {
                    bytes[0] ^= 1;
                }
                return new(CreateBody(bytes, false, false), false, null, null, Raw.Length);
            }
            Assert.Equal(4096L, length!.Value);
            lock (sync)
            {
                requests++;
                int currentActive = Interlocked.Increment(ref active);
                maximumActive = Math.Max(maximumActive, currentActive);
                if (requests == 4)
                {
                    allRequests.SetResult();
                }
            }
            try
            {
                await allRequests.Task.WaitAsync(cancellationToken);
                if (response != "valid" && start == 0)
                {
                    await BodiesStarted.Task.WaitAsync(cancellationToken);
                    await Release.Task.WaitAsync(cancellationToken);
                    if (response == "unsatisfiable")
                    {
                        throw new RegistryException { StatusCode = HttpStatusCode.RequestedRangeNotSatisfiable };
                    }
                    if (response == "io")
                    {
                        throw new IOException($"Failed range for '{digest}'.");
                    }
                    if (response == "ignored")
                    {
                        return new(CreateBody(Raw, true, true), false, null, null, Raw.Length);
                    }
                }
                byte[] selected = FileBytes(checked((int)(start / spacing)));
                if (start == 0)
                {
                    if (response is "corrupt" or "corrupt-full")
                    {
                        selected[0] ^= 1;
                    }
                    if (response == "truncated")
                    {
                        selected = selected[..^1];
                    }
                    if (response == "excess")
                    {
                        selected = [.. selected, 0];
                    }
                }
                return new(CreateBody(selected, true, false, start), true,
                    start == 0 && response == "wrong-range" ? 1 : start, start + length - 1,
                    start == 0 && response == "wrong-total" ? Index.BlobLength + 1 : Index.BlobLength);
            }
            catch
            {
                Interlocked.Decrement(ref active);
                throw;
            }
        }

        private Stream CreateBody(byte[] bytes, bool range, bool ignored, long start = 0)
        {
            Interlocked.Increment(ref opened);
            return new Body(bytes, async token =>
            {
                if (ignored)
                {
                    Assert.Equal(1, Active);
                    Assert.Equal(3, Disposed);
                    token.ThrowIfCancellationRequested();
                }
                else if (range)
                {
                    int count = Interlocked.Increment(ref bodiesStarted);
                    if (count == (response == "valid" ? 4 : 3))
                    {
                        BodiesStarted.TrySetResult();
                    }
                    if (response == "valid")
                    {
                        await Release.Task.WaitAsync(token);
                    }
                    else if (start != 0)
                    {
                        await never.Task.WaitAsync(token);
                    }
                }
            }, count =>
            {
                Interlocked.Add(ref bytesRead, count);
                if (count == 0)
                {
                    Interlocked.Increment(ref endsRead);
                }
            }, () =>
            {
                Interlocked.Increment(ref disposed);
                if (range)
                {
                    Interlocked.Decrement(ref active);
                    BodyDisposed?.Invoke();
                }
            });
        }
    }

    private sealed class Body(
        byte[] bytes, Func<CancellationToken, Task> beforeRead, Action<int> read, Action disposed) : MemoryStream(bytes)
    {
        private bool started;
        private bool closed;
        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (!started)
            {
                started = true;
                await beforeRead(cancellationToken);
            }
            cancellationToken.ThrowIfCancellationRequested();
            int count = await base.ReadAsync(buffer, cancellationToken);
            read(count);
            return count;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && !closed)
            {
                closed = true;
                disposed();
            }
            base.Dispose(disposing);
        }
    }
}
