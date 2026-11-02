using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Valleysoft.DockerRegistryClient;

namespace Valleysoft.Dredge.Tests;

public sealed class ScopedLayerStoreTests : IAsyncDisposable
{
    private readonly string root = Path.Combine(Environment.CurrentDirectory, $".scoped-layers-{Guid.NewGuid():N}");
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task OwnedContentProofDoesNotReacquirePublicationLockButSiblingStillAcquiresItsPin()
    {
        await using LayerStore store = new(root);
        await using LayerStore owner = store.CreateScope();
        await using LayerStore sibling = store.CreateScope();
        Content content = new(DockerArchiveFixture.BuildTar(("file", new byte[] { 1 })));
        await VerifyAsync(owner, content);
        string stripe = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(LayerStore.GetKey(content.Digest))))[..2];
        using (FileStream cacheLock = new(Path.Combine(root, "layer-store", "locks", $"{stripe}.lock"),
            FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
        {
            using CancellationTokenSource cancellation = CancellationTokenSource.CreateLinkedTokenSource(Token);
            cancellation.CancelAfter(TimeSpan.FromSeconds(2));
            Assert.Equal(content.Digest, await owner.VerifyContentAsync(
                content, content.Digest, content.Bytes.Length, content.Digest, cancellation.Token));
            await Assert.ThrowsAsync<InvalidDataException>(() => owner.VerifyContentAsync(
                content, content.Digest, content.Bytes.Length + 1, content.Digest, cancellation.Token));
            using CancellationTokenSource siblingCancellation = CancellationTokenSource.CreateLinkedTokenSource(Token);
            siblingCancellation.CancelAfter(TimeSpan.FromMilliseconds(100));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => sibling.VerifyContentAsync(
                content, content.Digest, content.Bytes.Length, content.Digest, siblingCancellation.Token));
        }
        await VerifyAsync(sibling, content);
        await owner.DisposeAsync();
        await store.ClearAsync(Token);
        Assert.True(File.Exists(store.GetBlobPath(content.Digest)));
        await VerifyAsync(sibling, content);
        using CancellationTokenSource canceled = new();
        canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => sibling.VerifyContentAsync(
            content, content.Digest, content.Bytes.Length, content.Digest, canceled.Token));
        Assert.Equal(1, content.Downloads);
    }

    [Fact]
    public async Task SiblingScopesShareVerificationAndReleaseOnlyTheirOwnBlobs()
    {
        await using LayerStore store = new(root, maxBytes: 0);
        Content baseline = new(DockerArchiveFixture.BuildTar(("baseline", new byte[] { 1 })));
        Content shared = new(DockerArchiveFixture.BuildTar(("shared", new byte[] { 2 })));
        using (await store.OpenBlobAsync(baseline, baseline.Digest, baseline.Bytes.Length, Token)) { }
        await using LayerStore first = store.CreateScope();
        await using LayerStore second = store.CreateScope();
        await VerifyAsync(first, shared);
        TaskCompletionSource<string> verification = Assert.Single(Verifications(store)).Value;
        await VerifyAsync(second, shared);
        Assert.Same(verification, Assert.Single(Verifications(store)).Value);
        Assert.Equal(1, shared.Downloads);

        await first.DisposeAsync();
        await store.ClearAsync(Token);
        Assert.True(File.Exists(store.GetBlobPath(baseline.Digest)));
        Assert.True(File.Exists(store.GetBlobPath(shared.Digest)));
        await VerifyAsync(second, shared);
        Assert.Same(verification, Assert.Single(Verifications(store)).Value);

        await second.DisposeAsync();
        Assert.True(File.Exists(store.GetBlobPath(baseline.Digest)));
        Assert.False(File.Exists(store.GetBlobPath(shared.Digest)));
        Assert.Empty(Verifications(store));
        await using LayerStore replacement = store.CreateScope();
        await VerifyAsync(replacement, shared);
        Assert.NotSame(verification, Assert.Single(Verifications(store)).Value);
        Assert.Equal(2, shared.Downloads);
    }

    [Fact]
    public async Task DisposingScopeDrainsPublicationAndPreservesReturnedReader()
    {
        await using LayerStore store = new(root, maxBytes: 0);
        await using LayerStore scope = store.CreateScope();
        Content content = new(DockerArchiveFixture.BuildTar(("file", new byte[] { 3 })))
        {
            Release = new(TaskCreationOptions.RunContinuationsAsynchronously)
        };
        Task<Stream> publication = scope.OpenBlobAsync(content, content.Digest, content.Bytes.Length, Token);
        await content.Entered.Task.WaitAsync(Token);
        Task closing = scope.DisposeAsync().AsTask();
        Assert.False(closing.IsCompleted);
        content.Release.SetResult();
        using Stream reader = await publication;
        await closing.WaitAsync(TimeSpan.FromSeconds(10), Token);
        Assert.Equal(content.Bytes, LayerCacheTestContext.ReadBytes(reader));
        Assert.True(File.Exists(store.GetBlobPath(content.Digest)));
        reader.Dispose();
        Assert.Equal(content.Bytes.Length, await store.ClearAsync(Token));
        await Assert.ThrowsAsync<ObjectDisposedException>(() =>
            scope.OpenBlobAsync(content, content.Digest, content.Bytes.Length, Token));
        Assert.Equal(1, content.Downloads);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConcurrentScopesSharePendingVerificationAndSurvivingOwnerKeepsItsLease(bool failBeforeRelease)
    {
        await using LayerStore store = new(root, maxBytes: 0);
        await using LayerStore first = store.CreateScope();
        await using LayerStore second = store.CreateScope();
        Content content = new(DockerArchiveFixture.BuildTar(("file", new byte[] { 4 })))
        {
            Release = new(TaskCreationOptions.RunContinuationsAsynchronously)
        };
        Task firstVerification = VerifyAsync(first, content);
        async Task CheckPendingAsync()
        {
            Task secondVerification = Task.CompletedTask;
            try
            {
                await content.Entered.Task.WaitAsync(Token);
                secondVerification = VerifyAsync(second, content);
                Assert.Single(Verifications(store));
                if (failBeforeRelease)
                {
                    Assert.Fail("Simulated assertion failure before releasing verification.");
                }
            }
            finally
            {
                content.Release.TrySetResult();
                await Task.WhenAll(firstVerification, secondVerification).WaitAsync(TimeSpan.FromSeconds(10), Token);
            }
        }
        if (failBeforeRelease)
        {
            await Assert.ThrowsAsync<Xunit.Sdk.FailException>(CheckPendingAsync);
        }
        else
        {
            await CheckPendingAsync();
        }
        Assert.True(firstVerification.IsCompletedSuccessfully);
        await first.DisposeAsync();
        Assert.Equal(1, content.Downloads);
        Assert.Equal(0, await store.ClearAsync(Token));
        Assert.True(File.Exists(store.GetBlobPath(content.Digest)));
        await second.DisposeAsync();
        Assert.False(File.Exists(store.GetBlobPath(content.Digest)));
    }

    [Fact]
    public async Task ReleasingManyPinsAllocatesLinearlyAndRemovesEveryProof()
    {
        const int count = 128;
        await using LayerStore store = new(root);
        await using LayerStore scope = store.CreateScope();
        for (int i = 0; i < count; i++)
        {
            Content content = new(DockerArchiveFixture.BuildTar(($"file-{i}", new byte[] { 1, 2, 3 })));
            await VerifyAsync(scope, content);
        }
        Assert.Equal(count, Verifications(store).Count);
        Task closing;
        long allocated;
        using (FileStream maintenance = new(
            Path.Combine(root, "layer-store", "locks", "maintenance.lock"),
            FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            long before = GC.GetAllocatedBytesForCurrentThread();
            closing = scope.DisposeAsync().AsTask();
            allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        }
        await closing.WaitAsync(TimeSpan.FromSeconds(10), Token);

        Assert.Empty(Verifications(store));
        Assert.InRange(allocated, 0, count * 2048L + 65_536);
        await scope.DisposeAsync();
    }

    [Fact]
    public async Task BatchedCleanupKeepsOtherOwnersAndRemovesAllAlgorithmsForReleasedLayers()
    {
        await using LayerStore store = new(root, maxBytes: 0);
        await using LayerStore first = store.CreateScope();
        await using LayerStore second = store.CreateScope();
        Content shared = new(DockerArchiveFixture.BuildTar(("shared", new byte[] { 1 })));
        Content exclusive = new(DockerArchiveFixture.BuildTar(("exclusive", new byte[] { 2 })));
        Content baseline = new(DockerArchiveFixture.BuildTar(("baseline", new byte[] { 3 })));
        await VerifyBothAlgorithmsAsync(store, baseline);
        await VerifyBothAlgorithmsAsync(first, exclusive);
        await VerifyBothAlgorithmsAsync(first, shared);
        await VerifyBothAlgorithmsAsync(second, shared);
        Assert.Equal(6, Verifications(store).Count);

        await first.DisposeAsync();

        Assert.Equal(4, Verifications(store).Count);
        Assert.DoesNotContain(Verifications(store).Keys, key => key.Digest == exclusive.Digest);
        Assert.False(File.Exists(store.GetBlobPath(exclusive.Digest)));
        await VerifyBothAlgorithmsAsync(second, shared);
        Assert.Equal(1, shared.Downloads);
        await second.DisposeAsync();
        Assert.Equal(2, Verifications(store).Count);
        Assert.All(Verifications(store).Keys, key => Assert.Equal(baseline.Digest, key.Digest));
        Assert.True(File.Exists(store.GetBlobPath(baseline.Digest)));
        await store.DisposeAsync();
        Assert.Empty(Verifications(store));
    }

    [Fact]
    public async Task CleanupRemovesReleasedProofsEvenWhenALaterPinDisposalFails()
    {
        await using LayerStore store = new(root);
        LayerStore scope = store.CreateScope();
        await VerifyAsync(scope, new(DockerArchiveFixture.BuildTar(("first", new byte[] { 1 }))));
        await VerifyAsync(scope, new(DockerArchiveFixture.BuildTar(("second", new byte[] { 2 }))));
        var pins = (IDictionary<string, FileStream>)typeof(LayerStore)
            .GetField("pins", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(scope)!;
        List<string> released = [];
        bool recording = true;
        try
        {
            foreach (var pair in pins.ToArray())
            {
                pair.Value.Dispose();
                pins[pair.Key] = new DisposalCallbackStream(pair.Key, () =>
                {
                    if (recording)
                    {
                        released.Add(pair.Key);
                        if (released.Count == 2)
                        {
                            throw new IOException("Pin disposal failed.");
                        }
                    }
                });
            }

            IOException error = await Assert.ThrowsAsync<IOException>(() => scope.DisposeAsync().AsTask());

            Assert.Equal("Pin disposal failed.", error.Message);
            Assert.Equal(2, released.Count);
            var proof = Assert.Single(Verifications(store));
            Assert.Equal(released[1], store.GetBlobPath(proof.Key.Digest));
            Assert.DoesNotContain(Verifications(store).Keys,
                key => store.GetBlobPath(key.Digest) == released[0]);
        }
        finally
        {
            recording = false;
            foreach (FileStream pin in pins.Values)
            {
                pin.Dispose();
            }
        }
    }

    private static async Task VerifyBothAlgorithmsAsync(LayerStore store, Content content)
    {
        await VerifyAsync(store, content);
        string sha512 = "sha512:" + Convert.ToHexStringLower(SHA512.HashData(content.Bytes));
        await store.VerifyContentAsync(content, content.Digest, content.Bytes.Length, sha512, Token);
    }

    private static Task VerifyAsync(LayerStore store, Content content) =>
        store.VerifyContentAsync(content, content.Digest, content.Bytes.Length, content.Digest, Token);

    private static Dictionary<(string Digest, string DiffId), TaskCompletionSource<string>> Verifications(LayerStore store) =>
        (Dictionary<(string Digest, string DiffId), TaskCompletionSource<string>>)typeof(LayerStore)
            .GetProperty("contentVerifications", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(store)!;

    public ValueTask DisposeAsync()
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
        return ValueTask.CompletedTask;
    }

    private sealed class Content(byte[] bytes) : IImageContentSource
    {
        public byte[] Bytes => bytes;
        public string Digest { get; } = LayerCacheTestContext.Digest(bytes);
        public int Downloads { get; private set; }
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource? Release { get; init; }

        public async Task<BlobDownloadResult> GetRangeAsync(
            string digest, long start, long? length, CancellationToken cancellationToken)
        {
            Assert.Equal(Digest, digest);
            Assert.Equal(0, start);
            Assert.Null(length);
            Downloads++;
            Entered.TrySetResult();
            if (Release is not null)
            {
                await Release.Task.WaitAsync(cancellationToken);
            }
            return new(new MemoryStream(bytes), false, null, null, bytes.Length);
        }
    }

    private sealed class DisposalCallbackStream(string path, Action onDispose)
        : FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read)
    {
        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (disposing)
            {
                onDispose();
            }
        }
    }
}
