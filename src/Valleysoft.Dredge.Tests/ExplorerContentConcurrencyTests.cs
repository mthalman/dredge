using System.Collections.Concurrent;
using Valleysoft.DockerRegistryClient;
using Valleysoft.DockerRegistryClient.Models.Images;
using Valleysoft.DockerRegistryClient.Models.Manifests;
using Valleysoft.DockerRegistryClient.Models.Manifests.Docker;

namespace Valleysoft.Dredge.Tests;

public sealed class ExplorerContentConcurrencyTests : IAsyncDisposable
{
    private readonly string root = Path.Combine(Environment.CurrentDirectory, $".identity-parallel-{Guid.NewGuid():N}");
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task IndependentLayersVerifyConcurrentlyWithinBoundAndSnapshotExpectedIds()
    {
        BarrierSource source = new(7, ExplorerContentIdentity.VerificationConcurrency);
        (Image config, ResolvedManifest manifest) = Manifest(source);
        string[] ids = source.Digests.ToArray();
        await using LayerStore store = new(root);
        Task verification = ExplorerContentIdentity.VerifyAsync(
            source, config, manifest, store, Token, expectedDiffIds: ids);
        try
        {
            await source.Started.Task.WaitAsync(Token);
            Assert.Equal(ExplorerContentIdentity.VerificationConcurrency, source.Active);
            Array.Fill(ids, "sha256:" + new string('f', 64));
            source.Release.SetResult();

            await verification;
            await ExplorerContentIdentity.VerifyAsync(source, config, manifest, store, Token);

            Assert.InRange(source.MaximumActive, 2, ExplorerContentIdentity.VerificationConcurrency);
            Assert.Equal(0, source.Active);
            Assert.All(source.Downloads.Values, downloads => Assert.Equal(1, downloads));
            Assert.Equal(7, source.Downloads.Count);
        }
        finally
        {
            await ReleaseAndDrainAsync(source, verification);
        }
    }

    [Fact]
    public async Task DuplicatePairsDoNotOccupyIndependentVerificationWorkers()
    {
        BarrierSource source = new(3, 3);
        (Image config, ResolvedManifest manifest) = Manifest(source, repeats: 8);
        ((DockerManifest)manifest.Manifest).Layers = manifest.Manifest.Layers
            .Cast<ManifestLayer>().OrderBy(layer => layer.Digest).ToArray();
        config.RootFilesystem!.DiffIds = manifest.Manifest.Layers.Select(layer => layer.Digest!).ToArray();
        await using LayerStore store = new(root);
        Task verification = ExplorerContentIdentity.VerifyAsync(source, config, manifest, store, Token);
        try
        {
            await source.Started.Task.WaitAsync(Token);
            source.Release.SetResult();
            await verification;
            Assert.Equal(3, source.Downloads.Count);
            Assert.All(source.Downloads.Values, downloads => Assert.Equal(1, downloads));
            Assert.Equal(3, source.MaximumActive);
        }
        finally
        {
            await ReleaseAndDrainAsync(source, verification);
        }
    }

    [Fact]
    public async Task DifferentExpectedIdsForSameDigestStillVerifySeparately()
    {
        BarrierSource source = new(1, 1);
        source.Release.SetResult();
        (Image config, ResolvedManifest manifest) = Manifest(source, repeats: 2);
        await using LayerStore store = new(root);
        await ExplorerContentIdentity.VerifyAsync(source, config, manifest, store, Token);
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            ExplorerContentIdentity.VerifyAsync(source, config, manifest, store, Token,
                expectedDiffIds: [source.Digests[0], "sha256:" + new string('f', 64)]));
        await ExplorerContentIdentity.VerifyAsync(source, config, manifest, store, Token);
        Assert.Equal(1, Assert.Single(source.Downloads).Value);
    }

    [Fact]
    public async Task FirstFailureCancelsAndDrainsSiblingsWithoutReplacingOriginalError()
    {
        BarrierSource source = new(6, ExplorerContentIdentity.VerificationConcurrency)
        {
            Failure = new IOException("first download failed"),
            DelayCancellationDrain = true,
            ExpectedCanceled = ExplorerContentIdentity.VerificationConcurrency - 1
        };
        Exception expectedFailure = source.Failure;
        (Image config, ResolvedManifest manifest) = Manifest(source);
        await using LayerStore store = new(root);
        Task verification = ExplorerContentIdentity.VerifyAsync(source, config, manifest, store, Token);
        try
        {
            await source.Started.Task.WaitAsync(Token);
            source.Fail.SetResult();
            await source.Canceled.Task.WaitAsync(Token);
            Assert.False(verification.IsCompleted);
            Assert.Equal(ExplorerContentIdentity.VerificationConcurrency, source.Downloads.Count);
            source.Drain.SetResult();

            Assert.Same(source.Failure, await Assert.ThrowsAsync<IOException>(() => verification));
            Assert.Equal(0, source.Active);
            source.Failure = null;
            source.Release.SetResult();
            await ExplorerContentIdentity.VerifyAsync(source, config, manifest, store, Token);
            Assert.Equal(6, source.Downloads.Count);
            Assert.All(source.Digests.Take(3), digest => Assert.Equal(2, source.Downloads[digest]));
        }
        finally
        {
            await ReleaseAndDrainAsync(source, verification, expectedFailure);
        }
    }

    [Fact]
    public async Task CallerCancellationDrainsAllStartedDownloadsAndAllowsRetry()
    {
        BarrierSource source = new(6, ExplorerContentIdentity.VerificationConcurrency)
        {
            DelayCancellationDrain = true,
            ExpectedCanceled = ExplorerContentIdentity.VerificationConcurrency
        };
        (Image config, ResolvedManifest manifest) = Manifest(source);
        await using LayerStore store = new(root);
        using CancellationTokenSource cancellation = CancellationTokenSource.CreateLinkedTokenSource(Token);
        Task verification = ExplorerContentIdentity.VerifyAsync(
            source, config, manifest, store, cancellation.Token);
        try
        {
            await source.Started.Task.WaitAsync(Token);
            cancellation.Cancel();
            await source.Canceled.Task.WaitAsync(Token);
            Assert.False(verification.IsCompleted);
            source.Drain.SetResult();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => verification);
            Assert.Equal(0, source.Active);
            Assert.Equal(ExplorerContentIdentity.VerificationConcurrency, source.Downloads.Count);
            source.Release.SetResult();
            await ExplorerContentIdentity.VerifyAsync(source, config, manifest, store, Token);
            Assert.Equal(6, source.Downloads.Count);
        }
        finally
        {
            await ReleaseAndDrainAsync(source, verification);
        }
    }

    private static async Task ReleaseAndDrainAsync(
        BarrierSource source, Task verification, Exception? expectedFailure = null)
    {
        source.Release.TrySetResult();
        source.Fail.TrySetResult();
        source.Drain.TrySetResult();
        try
        {
            await verification;
        }
        catch (IOException exception) when (ReferenceEquals(exception, expectedFailure ?? source.Failure))
        {
        }
        catch (OperationCanceledException)
        {
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedPreReleaseAssertionStillDrainsWorkersBeforeStoreDisposal(bool failure)
    {
        BarrierSource source = new(6, ExplorerContentIdentity.VerificationConcurrency)
        {
            Failure = failure ? new IOException("expected source failure") : null,
            DelayCancellationDrain = true
        };
        (Image config, ResolvedManifest manifest) = Manifest(source);
        await using LayerStore store = new(root);
        Task verification = ExplorerContentIdentity.VerifyAsync(source, config, manifest, store, Token);
        InvalidOperationException expected = new("simulated assertion failure");

        Exception? actual = await Record.ExceptionAsync(async () =>
        {
            try
            {
                await source.Started.Task.WaitAsync(TimeSpan.FromSeconds(10), Token);
                throw expected;
            }
            finally
            {
                await ReleaseAndDrainAsync(source, verification).WaitAsync(TimeSpan.FromSeconds(10), Token);
            }
        });

        Assert.Same(expected, actual);
        Assert.Equal(0, source.Active);
        Assert.True(verification.IsCompleted);
        await store.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10), Token);
    }

    private static (Image Config, ResolvedManifest Manifest) Manifest(BarrierSource source, int repeats = 1)
    {
        string[] ids = Enumerable.Range(0, repeats).SelectMany(_ => source.Digests).ToArray();
        DockerManifest manifest = new()
        {
            Layers = ids.Select(digest => new ManifestLayer
            {
                Digest = digest,
                Size = source.Bytes[digest].Length
            }).ToArray()
        };
        Image config = new()
        {
            RootFilesystem = new RootFilesystem { Type = "layers", DiffIds = ids }
        };
        return (config, new ResolvedManifest(
            new ManifestInfo("application/test", LayerCacheTestContext.Digest([42]), manifest), manifest));
    }

    public ValueTask DisposeAsync()
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
        return ValueTask.CompletedTask;
    }

    private sealed class BarrierSource : IImageContentSource
    {
        private readonly int expectedStarted;
        private int active;
        private int maximumActive;
        private int started;
        private int canceled;
        public Dictionary<string, byte[]> Bytes { get; } = [];
        public string[] Digests { get; }
        public ConcurrentDictionary<string, int> Downloads { get; } = new();
        public int Active => Volatile.Read(ref active);
        public int MaximumActive => Volatile.Read(ref maximumActive);
        public IOException? Failure { get; set; }
        public bool DelayCancellationDrain { get; init; }
        public int ExpectedCanceled { get; init; }
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Fail { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Canceled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Drain { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public BarrierSource(int count, int expectedStarted)
        {
            this.expectedStarted = expectedStarted;
            for (int index = 0; index < count; index++)
            {
                byte[] bytes = [(byte)index, 17, 29];
                Bytes.Add(LayerCacheTestContext.Digest(bytes), bytes);
            }
            Digests = Bytes.Keys.ToArray();
        }

        public async Task<BlobDownloadResult> GetRangeAsync(
            string digest, long start, long? length, CancellationToken cancellationToken)
        {
            Assert.Equal(0, start);
            Assert.Null(length);
            Downloads.AddOrUpdate(digest, 1, static (_, count) => count + 1);
            int current = Interlocked.Increment(ref active);
            int maximum;
            do
            {
                maximum = Volatile.Read(ref maximumActive);
            }
            while (current > maximum &&
                Interlocked.CompareExchange(ref maximumActive, current, maximum) != maximum);
            try
            {
                if (Interlocked.Increment(ref started) == expectedStarted)
                {
                    Started.TrySetResult();
                }
                if (Failure is IOException failure && digest == Digests[0])
                {
                    await Fail.Task.WaitAsync(cancellationToken);
                    throw failure;
                }
                await Release.Task.WaitAsync(cancellationToken);
                return new(new MemoryStream(Bytes[digest]), false, null, null, Bytes[digest].Length);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                if (Interlocked.Increment(ref canceled) == ExpectedCanceled)
                {
                    Canceled.TrySetResult();
                }
                if (DelayCancellationDrain)
                {
                    await Drain.Task.WaitAsync(Token);
                }
                throw;
            }
            finally
            {
                Interlocked.Decrement(ref active);
            }
        }
    }
}
