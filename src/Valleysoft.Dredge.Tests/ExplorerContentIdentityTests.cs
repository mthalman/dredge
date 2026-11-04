using System.Formats.Tar;
using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;
using Valleysoft.DockerRegistryClient;
using Valleysoft.DockerRegistryClient.Models.Images;
using Valleysoft.DockerRegistryClient.Models.Manifests;
using Valleysoft.DockerRegistryClient.Models.Manifests.Docker;

namespace Valleysoft.Dredge.Tests;

public sealed class ExplorerContentIdentityTests : IDisposable
{
    private readonly string root = Path.Combine(
        Directory.GetCurrentDirectory(), $".content-identity-{Guid.NewGuid():N}");
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RepeatedLayersAndDistinctSessionsShareOneSuccessfulVerification(bool gzip)
    {
        byte[] raw = Layer();
        Content content = new(gzip ? Compress(raw) : raw);
        await using LayerStore store = new(root);
        await using ExplorerSource source = Source(content, LayerCacheTestContext.Digest(raw), 3);
        ExplorerSession first = await ExplorerSession.CreateAsync(null, source, store, null, Token);
        ExplorerSession second = await ExplorerSession.CreateAsync(null, source, store, null, Token);
        try
        {
            await first.EnsureContentIdentitiesAsync(Token);
            TaskCompletionSource<string> verification = Assert.Single(Verifications(store)).Value;
            Assert.True(verification.Task.IsCompletedSuccessfully);
            await second.EnsureContentIdentitiesAsync(Token);
            Assert.Same(verification, Assert.Single(Verifications(store)).Value);
            Assert.Equal(1, content.Downloads);
        }
        finally
        {
            await first.Files.DisposeAsync();
            await second.Files.DisposeAsync();
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Sha512SessionUsesCanonicalContentIdentityWithoutChangingConfiguration(bool gzip)
    {
        byte[] raw = Layer();
        string declared = "sha512:" + Convert.ToHexStringLower(SHA512.HashData(raw));
        Content content = new(gzip ? Compress(raw) : raw);
        await using LayerStore store = new(root);
        await using ExplorerSource source = Source(content, declared, 2);
        ExplorerSession session = await ExplorerSession.CreateAsync(null, source, store, null, Token);
        try
        {
            await session.EnsureContentIdentitiesAsync(Token);
            Assert.Equal(LayerCacheTestContext.Digest(raw), session.LayerIdentity(0, true));
            Assert.Equal(LayerCacheTestContext.Digest(raw), session.LayerIdentity(1, true));
            Assert.Equal(declared, session.Config.RootFilesystem!.DiffIds[0]);
            Assert.Equal(source.Resolved.Manifest.Layers[0].Digest, session.LayerIdentity(0, false));
        }
        finally
        {
            await session.Files.DisposeAsync();
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DifferentExpectedDiffIdFailsWithoutPoisoningSuccessfulPair(bool gzip)
    {
        byte[] raw = Layer();
        Content content = new(gzip ? Compress(raw) : raw);
        await using LayerStore store = new(root);
        await VerifyAsync(content, store, LayerCacheTestContext.Digest(raw));
        TaskCompletionSource<string> success = Assert.Single(Verifications(store)).Value;
        string wrong = "sha256:" + new string('a', 64);
        await Assert.ThrowsAsync<InvalidDataException>(() => VerifyAsync(content, store, wrong));
        await Assert.ThrowsAsync<InvalidDataException>(() => VerifyAsync(content, store, wrong));
        await VerifyAsync(content, store, LayerCacheTestContext.Digest(raw));
        Assert.Same(success, Assert.Single(Verifications(store)).Value);
    }

    [Fact]
    public async Task FailedDownloadCanRetryTheSamePair()
    {
        Content content = new(Layer()) { FailNext = true };
        await using LayerStore store = new(root);
        await Assert.ThrowsAsync<IOException>(() => VerifyAsync(content, store, LayerCacheTestContext.Digest(content.Bytes)));
        Assert.Empty(Verifications(store));
        await VerifyAsync(content, store, LayerCacheTestContext.Digest(content.Bytes));
        Assert.True(Assert.Single(Verifications(store)).Value.Task.IsCompletedSuccessfully);
        Assert.Equal(2, content.Downloads);
    }

    [Fact]
    public async Task CanceledVerificationCanRetryTheSamePair()
    {
        Content content = new(Layer()) { Release = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        await using LayerStore store = new(root);
        using CancellationTokenSource cancellation = CancellationTokenSource.CreateLinkedTokenSource(Token);
        Task canceled = VerifyAsync(content, store, LayerCacheTestContext.Digest(content.Bytes), cancellation.Token);
        await content.Started.Task.WaitAsync(Token);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => canceled);
        Assert.Empty(Verifications(store));
        content.Release.SetResult();
        await VerifyAsync(content, store, LayerCacheTestContext.Digest(content.Bytes));
        Assert.Equal(2, content.Downloads);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConcurrentVerificationsSharePendingWorkAndCanceledWaiterDoesNotPoisonIt(bool failBeforeRelease)
    {
        Content content = new(Layer()) { Release = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        await using LayerStore store = new(root);
        Task first = VerifyAsync(content, store, LayerCacheTestContext.Digest(content.Bytes));
        async Task CheckPendingAsync()
        {
            Task second = Task.CompletedTask;
            try
            {
                await content.Started.Task.WaitAsync(Token);
                second = VerifyAsync(content, store, LayerCacheTestContext.Digest(content.Bytes));
                using CancellationTokenSource cancellation = CancellationTokenSource.CreateLinkedTokenSource(Token);
                Task canceledWaiter = VerifyAsync(content, store, LayerCacheTestContext.Digest(content.Bytes), cancellation.Token);
                cancellation.Cancel();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => canceledWaiter);
                Assert.Single(Verifications(store));
                if (failBeforeRelease)
                {
                    Assert.Fail("Simulated assertion failure before releasing verification.");
                }
            }
            finally
            {
                content.Release.TrySetResult();
                await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(10), Token);
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
        Assert.True(first.IsCompletedSuccessfully);
        Assert.Equal(1, content.Downloads);
        Assert.True(Assert.Single(Verifications(store)).Value.Task.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task DisposalWaitsForPendingVerificationAndClearsOperationCache()
    {
        Content content = new(Layer()) { Release = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        LayerStore store = new(root);
        Task verification = VerifyAsync(content, store, LayerCacheTestContext.Digest(content.Bytes));
        await content.Started.Task.WaitAsync(Token);
        Task disposal = store.DisposeAsync().AsTask();
        Assert.False(disposal.IsCompleted);
        content.Release.SetResult();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => verification);
        await disposal;
        Assert.Empty(Verifications(store));
        await Assert.ThrowsAsync<ObjectDisposedException>(() =>
            VerifyAsync(content, store, LayerCacheTestContext.Digest(content.Bytes)));
        Assert.Equal(1, content.Downloads);
    }

    [Fact]
    public async Task NewStoreRevalidatesRatherThanReusingDisposedOperationCache()
    {
        Content content = new(Layer());
        TaskCompletionSource<string> previous;
        await using (LayerStore first = new(root))
        {
            await VerifyAsync(content, first, LayerCacheTestContext.Digest(content.Bytes));
            previous = Assert.Single(Verifications(first)).Value;
        }
        await using LayerStore second = new(root);
        await VerifyAsync(content, second, LayerCacheTestContext.Digest(content.Bytes));
        Assert.NotSame(previous, Assert.Single(Verifications(second)).Value);
        Assert.Equal(1, content.Downloads);
    }

    [Fact]
    public async Task CacheHitsStillValidateAdvertisedLength()
    {
        Content content = new(Layer());
        await using LayerStore store = new(root);
        await VerifyAsync(content, store, LayerCacheTestContext.Digest(content.Bytes));
        await Assert.ThrowsAsync<InvalidDataException>(() => store.VerifyContentAsync(
            content, LayerCacheTestContext.Digest(content.Bytes), content.Bytes.Length + 1,
            LayerCacheTestContext.Digest(content.Bytes), Token));
        Assert.Single(Verifications(store));
    }

    [Fact]
    public async Task ExplicitSnapshotRemainsCompatible()
    {
        Content content = new(Layer());
        await using LayerStore store = new(root);
        await using ExplorerSource source = Source(content, "sha256:" + new string('a', 64), 1);
        await ExplorerContentIdentity.VerifyAsync(content, source.Config, source.Resolved, store, Token,
            layerCount: 1, expectedDiffIds: [LayerCacheTestContext.Digest(content.Bytes)]);
        Assert.Single(Verifications(store));
    }

    [Fact]
    public async Task RepeatedSuccessfulPairStillRejectsWrongDescriptorSize()
    {
        Content content = new(Layer());
        await using LayerStore store = new(root);
        await using ExplorerSource source = Source(content, LayerCacheTestContext.Digest(content.Bytes), 2);
        await ExplorerContentIdentity.VerifyAsync(content, source.Config, source.Resolved, store, Token);
        ((DockerManifest)source.Resolved.Manifest).Layers[1].Size++;

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            ExplorerContentIdentity.VerifyAsync(content, source.Config, source.Resolved, store, Token));

        Assert.Single(Verifications(store));
        Assert.Equal(1, content.Downloads);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task IndexedRepeatedDescriptorsKeepEveryIdentityAndValidateDistinctSizes(bool gzip, bool warm)
    {
        byte[] raw = Layer();
        Content content = new(gzip ? Compress(raw) : raw);
        string expected = LayerCacheTestContext.Digest(raw);
        await using LayerStore store = new(root);
        await using ExplorerSource source = Source(content, expected, 8);
        if (warm)
        {
            await store.GetIndexAsync(content, new(0, LayerCacheTestContext.Digest(content.Bytes)),
                content.Bytes.Length, Token, expectedDiffId: expected);
        }

        string[] identities = await ExplorerContentIdentity.VerifyAsync(
            content, source.Config, source.Resolved, store, Token, indexLayers: true);

        Assert.Equal(Enumerable.Repeat(expected, 8), identities);
        Assert.Equal(1, content.Downloads);
        Assert.Single(Verifications(store));
        ((DockerManifest)source.Resolved.Manifest).Layers[7].Size++;
        await Assert.ThrowsAsync<InvalidDataException>(() => ExplorerContentIdentity.VerifyAsync(
            content, source.Config, source.Resolved, store, Token, indexLayers: true));
    }

    private static Task VerifyAsync(
        Content content, LayerStore store, string expected, CancellationToken? token = null) =>
        store.VerifyContentAsync(content, LayerCacheTestContext.Digest(content.Bytes),
            content.Bytes.Length, expected, token ?? Token);

    private static Dictionary<(string Digest, string DiffId), TaskCompletionSource<string>> Verifications(LayerStore store) =>
        (Dictionary<(string Digest, string DiffId), TaskCompletionSource<string>>)typeof(LayerStore)
            .GetProperty("contentVerifications", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(store)!;

    private static ExplorerSource Source(Content content, string diffId, int count)
    {
        ImageName image = ImageName.Parse("registry.test/image:tag");
        DockerManifest manifest = new()
        {
            Layers = Enumerable.Range(0, count)
                .Select(_ => new ManifestLayer
                {
                    Digest = LayerCacheTestContext.Digest(content.Bytes), Size = content.Bytes.Length
                }).ToArray()
        };
        ManifestInfo info = new("application/test", "sha256:" + new string('b', 64), manifest);
        Image config = new()
        {
            Os = "linux",
            Architecture = "amd64",
            RootFilesystem = new RootFilesystem { Type = "layers", DiffIds = Enumerable.Repeat(diffId, count).ToArray() },
            History = Enumerable.Range(0, count).Select(_ => new LayerHistory { CreatedBy = "COPY file /file" }).ToArray()
        };
        ResolvedManifest resolved = new(info, manifest);
        return new ExplorerSource
        {
            Image = image, Config = config, Resolved = resolved, Platforms = [],
            Source = new ResolvedImage(ImageSourceReference.Parse("registry://registry.test/image:tag"),
                image, resolved, info, config, [], content, "sha256:image", "linux/amd64")
        };
    }

    private static byte[] Layer()
    {
        using MemoryStream result = new();
        using (TarWriter writer = new(result, leaveOpen: true))
        {
            using MemoryStream data = new([1, 2, 3]);
            writer.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, "file") { DataStream = data });
        }
        return result.ToArray();
    }

    private static byte[] Compress(byte[] raw)
    {
        using MemoryStream result = new();
        using (GZipStream gzip = new(result, CompressionMode.Compress, leaveOpen: true))
        {
            gzip.Write(raw);
        }
        return result.ToArray();
    }

    public void Dispose()
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private sealed class Content(byte[] bytes) : IImageContentSource
    {
        public byte[] Bytes => bytes;
        public int Downloads { get; private set; }
        public bool FailNext { get; set; }
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource? Release { get; init; }

        public async Task<BlobDownloadResult> GetRangeAsync(
            string digest, long start, long? length, CancellationToken cancellationToken)
        {
            Downloads++;
            Started.TrySetResult();
            if (Release is not null)
            {
                await Release.Task.WaitAsync(cancellationToken);
            }
            if (FailNext)
            {
                FailNext = false;
                throw new IOException("Retryable download failure.");
            }
            return new(new MemoryStream(bytes), false, null, null, bytes.Length);
        }
    }
}
