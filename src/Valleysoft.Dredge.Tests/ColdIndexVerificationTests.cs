using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;
using Valleysoft.DockerRegistryClient;
using Valleysoft.DockerRegistryClient.Models.Images;
using Valleysoft.DockerRegistryClient.Models.Manifests;
using Valleysoft.DockerRegistryClient.Models.Manifests.Docker;

namespace Valleysoft.Dredge.Tests;

public sealed class ColdIndexVerificationTests : IAsyncDisposable
{
    private readonly string root = Path.Combine(Environment.CurrentDirectory, $".cold-index-{Guid.NewGuid():N}");
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task PinnedContentUsesExpectedDigestAlgorithm(bool gzip, bool sha512)
    {
        byte[] raw = Layer();
        Content source = new(gzip ? Compress(raw) : raw);
        string expected = Digest(raw, sha512);
        await using LayerStore store = new(root);
        await store.VerifyContentAsync(source, source.Digest, source.Bytes.Length, expected, Token);
        TaskCompletionSource<string> successful = Assert.Single(Verifications(store)).Value;
        string wrong = Digest([42], sha512);
        InvalidDataException error = await Assert.ThrowsAsync<InvalidDataException>(() =>
            store.VerifyContentAsync(source, source.Digest, source.Bytes.Length, wrong, Token));
        Assert.Contains("does not match", error.Message);
        Assert.Same(successful, Assert.Single(Verifications(store)).Value);
    }

    [Theory]
    [InlineData("sha384:abcdef")]
    [InlineData("sha512:abcd")]
    [InlineData("sha256:ABCDEF")]
    public async Task UnsupportedOrMalformedExpectedDigestIsExplicitAndDoesNotDownload(string expected)
    {
        Content source = new(Compress(Layer()));
        await using LayerStore store = new(root);
        InvalidDataException error = await Assert.ThrowsAsync<InvalidDataException>(() =>
            store.VerifyContentAsync(source, source.Digest, source.Bytes.Length, expected, Token));
        Assert.Contains("Unsupported or invalid", error.Message);
        Assert.Equal(0, source.Downloads);
        Assert.Empty(Verifications(store));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ColdExplorerSessionPublishesProofBeforeContentVerificationAndReusesIt(bool sha512)
    {
        byte[] raw = Layer();
        Content content = new(Compress(raw));
        await using LayerStore store = new(root);
        string expected = Digest(raw, sha512);
        await using ExplorerSource source = Source(content, expected);
        ExplorerSession session = await ExplorerSession.CreateAsync(null, source, store, null, Token);
        try
        {
            var pair = Assert.Single(Verifications(store));
            Assert.Equal((content.Digest, expected), pair.Key);
            Assert.True(pair.Value.Task.IsCompletedSuccessfully);
            await session.EnsureContentIdentitiesAsync(Token);
            Assert.Same(pair.Value, Assert.Single(Verifications(store)).Value);
            Assert.Equal(1, content.Downloads);
        }
        finally
        {
            await session.Files.DisposeAsync();
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ColdExplorerIndexerPublishesProofForConfiguredAlgorithm(bool sha512)
    {
        byte[] raw = Layer();
        Content content = new(Compress(raw));
        string expected = Digest(raw, sha512);
        await using LayerStore store = new(root);
        await using ExplorerSource source = Source(content, expected);
        ExplorerLayerIndexer indexer = ExplorerLayerIndexer.Create(null, source, store);
        TaskCompletionSource indexed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        indexer.Completed += _ => indexed.TrySetResult();
        indexer.Failed += (_, exception) => indexed.TrySetException(exception);
        indexer.Start(Token);
        try
        {
            await indexed.Task.WaitAsync(Token);
            Assert.Equal((content.Digest, expected), Assert.Single(Verifications(store)).Key);
            Assert.True(Assert.Single(Verifications(store)).Value.Task.IsCompletedSuccessfully);
        }
        finally
        {
            await indexer.StopAsync();
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ColdIndexHashesHeadersPaddingAndEveryTrailingDecodedByte(bool sha512)
    {
        byte[] tar = Layer();
        byte[] raw = [.. tar, .. Enumerable.Range(0, 131_073).Select(index => (byte)(index % 251))];
        Content content = new(Compress(raw));
        await using LayerStore store = new(root);
        string expected = Digest(raw, sha512);
        await store.GetIndexAsync(content, new(0, content.Digest), content.Bytes.Length, Token,
            expectedDiffId: expected);
        var pair = Assert.Single(Verifications(store));
        Assert.Equal((content.Digest, expected), pair.Key);
        Assert.True(pair.Value.Task.IsCompletedSuccessfully);
        await store.VerifyContentAsync(content, content.Digest, content.Bytes.Length, expected, Token);
        Assert.Same(pair.Value, Assert.Single(Verifications(store)).Value);
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            store.VerifyContentAsync(content, content.Digest, content.Bytes.Length, Digest(tar, sha512), Token));
        Assert.Same(pair.Value, Assert.Single(Verifications(store)).Value);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CanonicalIdentityIsRetainedFromVerificationOrColdIndex(bool indexFirst)
    {
        byte[] raw = [.. Layer(), .. new byte[131_073]];
        Content content = new(Compress(raw));
        string declared = Digest(raw, sha512: true);
        string canonical = Digest(raw);
        await using LayerStore store = new(root);
        await using ExplorerSource source = Source(content, declared);
        if (indexFirst)
        {
            await store.GetIndexAsync(content, new(0, content.Digest), content.Bytes.Length, Token,
                expectedDiffId: declared);
        }
        string[] identities = await ExplorerContentIdentity.VerifyAsync(
            content, source.Config, source.Resolved, store, Token);
        var proof = Assert.Single(Verifications(store));
        Assert.Equal((content.Digest, declared), proof.Key);
        Assert.Equal(canonical, await proof.Value.Task);
        Assert.Equal([canonical], identities);
        await using LayerStore sibling = store.CreateScope();
        Assert.Equal(identities, await ExplorerContentIdentity.VerifyAsync(
            content, source.Config, source.Resolved, sibling, Token));
        Assert.Same(proof.Value, Assert.Single(Verifications(store)).Value);
        Assert.Equal(declared, source.Config.RootFilesystem!.DiffIds[0]);
        Assert.Equal(1, content.Downloads);
        await Assert.ThrowsAsync<InvalidDataException>(() => sibling.VerifyContentAsync(
            content, content.Digest, content.Bytes.Length + 1, declared, Token));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DualHashIndexReadsDecodedContentOnceIncludingTrailingBytes(bool gzip)
    {
        byte[] raw = [.. Layer(), .. new byte[131_073]];
        byte[] stored = gzip ? Compress(raw) : raw;
        using ReadCountingStream input = new(stored);
        using SHA256 sha256 = SHA256.Create();
        using SHA512 sha512 = SHA512.Create();

        await ImageLayerScanner.ScanAsync(input, new(0, Digest(stored)), Token, sha256, sha512);

        Assert.Equal(Digest(raw), $"sha256:{Convert.ToHexStringLower(sha256.Hash!)}");
        Assert.Equal(Digest(raw, true), $"sha512:{Convert.ToHexStringLower(sha512.Hash!)}");
        Assert.InRange(input.BytesRead, stored.Length, stored.Length + 6L);
        Assert.True(input.CanRead);
    }

    [Fact]
    public async Task IndexWithoutAggregateHashersDoesNotReadAlreadyVerifiedTrailingBytes()
    {
        byte[] raw = Layer();
        using ReadCountingStream input = new([.. raw, .. new byte[131_073]]);

        LayerChanges changes = await ImageLayerScanner.ScanAsync(input, new(0, Digest(raw)), Token);

        Assert.Single(changes.Entries);
        Assert.NotNull(changes.Entries[0].ContentHash);
        Assert.InRange(input.BytesRead, 1, raw.Length + 6L);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DaemonIndexDoesNotPublishUnpinnedContentProof(bool verifyContent)
    {
        DockerArchiveFixture fixture = DockerArchiveFixture.Create(oci: true, gzip: true);
        await using DockerImageArchive archive = await DockerImageArchive.OpenAsync(
            new MemoryStream(fixture.Export), fixture.Id, Token, Path.Combine(root, "archive"),
            verifyLayerContent: verifyContent);
        await using LayerStore store = new(Path.Combine(root, "cache"));
        string digest = archive.Resolved.Manifest.Layers[0].Digest!;

        await store.GetIndexAsync(archive, new(0, digest), fixture.StoredLayer.Length, Token,
            expectedDiffId: fixture.Id);

        Assert.Empty(Verifications(store));
        Assert.False(File.Exists(store.GetBlobPath(digest)));
        await Assert.ThrowsAsync<InvalidDataException>(() => store.VerifyContentAsync(
            archive, digest, fixture.StoredLayer.Length, fixture.Id, Token));
        Assert.Empty(Verifications(store));
    }

    [Fact]
    public async Task WrongHintPublishesOnlyActualDecodedIdentity()
    {
        byte[] raw = Layer();
        Content content = new(Compress(raw));
        string wrong = Digest([99], sha512: true);
        await using LayerStore store = new(root);
        await store.GetIndexAsync(content, new(0, content.Digest), content.Bytes.Length, Token,
            expectedDiffId: wrong);
        Assert.Equal((content.Digest, Digest(raw, sha512: true)), Assert.Single(Verifications(store)).Key);
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            store.VerifyContentAsync(content, content.Digest, content.Bytes.Length, wrong, Token));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("sha384:ignored")]
    [InlineData("sha256:invalid")]
    public async Task IndexingDoesNotNewlyRequireValidConfigurationDiffIds(string? hint)
    {
        byte[] raw = Layer();
        Content content = new(Compress(raw));
        await using LayerStore store = new(root);
        StoredLayerIndex index = await store.GetIndexAsync(
            content, new(0, content.Digest), content.Bytes.Length, Token, expectedDiffId: hint);
        Assert.Single(index.Changes.Entries);
        Assert.Equal((content.Digest, Digest(raw)), Assert.Single(Verifications(store)).Key);
    }

    [Theory]
    [InlineData(false, 0)]
    [InlineData(false, 2)]
    [InlineData(true, 0)]
    [InlineData(true, 2)]
    public async Task CountMismatchedOptionalDiffIdsDoNotRejectIndexingOrBecomeProof(
        bool useIndexer, int diffIdCount)
    {
        byte[] raw = Layer();
        Content content = new(Compress(raw));
        await using LayerStore store = new(root);
        await using ExplorerSource source = Source(content, Digest(raw, sha512: true));
        source.Config.RootFilesystem!.DiffIds =
            Enumerable.Repeat(Digest(raw, sha512: true), diffIdCount).ToArray();

        if (useIndexer)
        {
            ExplorerLayerIndexer indexer = ExplorerLayerIndexer.Create(null, source, store);
            TaskCompletionSource indexed = new(TaskCreationOptions.RunContinuationsAsynchronously);
            indexer.Completed += _ => indexed.TrySetResult();
            indexer.Failed += (_, exception) => indexed.TrySetException(exception);
            indexer.Start(Token);
            try
            {
                await indexed.Task.WaitAsync(Token);
                Assert.Single(Assert.Single(indexer.Snapshot()).Value.Changes.Entries);
            }
            finally
            {
                await indexer.StopAsync();
            }
        }
        else
        {
            ExplorerSession session = await ExplorerSession.CreateAsync(null, source, store, null, Token);
            try
            {
                Assert.Single(session.Entries, entry => entry.Type == ImageFileType.File);
                await Assert.ThrowsAsync<InvalidDataException>(() => session.EnsureContentIdentitiesAsync(Token));
            }
            finally
            {
                await session.Files.DisposeAsync();
            }
        }

        var proof = Assert.Single(Verifications(store));
        Assert.Equal((content.Digest, Digest(raw)), proof.Key);
        Assert.True(proof.Value.Task.IsCompletedSuccessfully);
        Assert.Equal(1, content.Downloads);
    }

    [Fact]
    public async Task PersistedIndexDoesNotCarryContentVerificationAcrossPinLifetimes()
    {
        byte[] raw = Layer();
        Content content = new(Compress(raw));
        TaskCompletionSource<string> prior;
        await using (LayerStore first = new(root))
        {
            await first.GetIndexAsync(content, new(0, content.Digest), content.Bytes.Length, Token);
            prior = Assert.Single(Verifications(first)).Value;
        }
        await using LayerStore next = new(root);
        await next.GetIndexAsync(content, new(0, content.Digest), content.Bytes.Length, Token);
        Assert.Empty(Verifications(next));
        await Assert.ThrowsAsync<InvalidDataException>(() => next.VerifyContentAsync(
            content, content.Digest, content.Bytes.Length, Digest([42]), Token));
        Assert.Empty(Verifications(next));
        await next.VerifyContentAsync(content, content.Digest, content.Bytes.Length, Digest(raw), Token);
        Assert.NotSame(prior, Assert.Single(Verifications(next)).Value);
        Assert.Equal(1, content.Downloads);
    }

    [Fact]
    public async Task SuppliedIndexAndFileHashesAreNotProofOfTheDiffId()
    {
        byte[] raw = Layer();
        Content content = new(Compress(raw));
        await using LayerStore store = new(root);
        StoredLayerIndex forged = new(content.Digest, content.Bytes.Length,
            new LayerChanges(
                [new ScannedEntry("file", ImageFileType.File, 0x1A4, 0, 0, 3,
                    DateTime.UnixEpoch, null, 0, 512, content.Bytes.Length, new string('a', 64))],
                [], []), "gzip");
        await store.WriteMetadataAsync(content.Digest, ImageFileSystemSemantics.LinuxIndexKind, forged, Token);
        Assert.Equal(new string('a', 64), Assert.Single(
            (await store.GetIndexAsync(content, new(0, content.Digest), content.Bytes.Length, Token))
                .Changes.Entries).ContentHash);
        Assert.Empty(Verifications(store));
        await Assert.ThrowsAsync<InvalidDataException>(() => store.VerifyContentAsync(
            content, content.Digest, content.Bytes.Length, Digest([42]), Token));
        await store.VerifyContentAsync(content, content.Digest, content.Bytes.Length, Digest(raw), Token);
        Assert.True(Assert.Single(Verifications(store)).Value.Task.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task ColdProofSurvivesOnlyWhileAnotherScopePinsTheLayer()
    {
        byte[] raw = Layer();
        Content content = new(Compress(raw));
        await using LayerStore store = new(root, maxBytes: 0);
        await using LayerStore first = store.CreateScope();
        await using LayerStore second = store.CreateScope();
        await first.GetIndexAsync(content, new(0, content.Digest), content.Bytes.Length, Token);
        TaskCompletionSource<string> proof = Assert.Single(Verifications(store)).Value;
        await Task.WhenAll(
            second.VerifyContentAsync(content, content.Digest, content.Bytes.Length, Digest(raw), Token),
            first.VerifyContentAsync(content, content.Digest, content.Bytes.Length, Digest(raw), Token));
        await first.DisposeAsync();
        Assert.Same(proof, Assert.Single(Verifications(store)).Value);
        await second.VerifyContentAsync(content, content.Digest, content.Bytes.Length, Digest(raw), Token);
        Assert.Same(proof, Assert.Single(Verifications(store)).Value);
        await second.DisposeAsync();
        Assert.Empty(Verifications(store));
        Assert.False(File.Exists(store.GetBlobPath(content.Digest)));
    }

    [Fact]
    public async Task CanceledColdIndexDoesNotPublishVerificationAndCanRetry()
    {
        Content content = new(Compress(Layer()))
        {
            Release = new(TaskCreationOptions.RunContinuationsAsynchronously)
        };
        await using LayerStore store = new(root);
        using CancellationTokenSource cancellation = CancellationTokenSource.CreateLinkedTokenSource(Token);
        Task canceled = store.GetIndexAsync(content, new(0, content.Digest),
            content.Bytes.Length, cancellation.Token);
        await content.Started.Task.WaitAsync(Token);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => canceled);
        Assert.Empty(Verifications(store));
        content.Release.SetResult();
        await store.GetIndexAsync(content, new(0, content.Digest), content.Bytes.Length, Token);
        Assert.True(Assert.Single(Verifications(store)).Value.Task.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task CancellationDuringTrailingByteDrainIsPropagated()
    {
        byte[] tar = Layer();
        using CancellationTokenSource cancellation = CancellationTokenSource.CreateLinkedTokenSource(Token);
        using PausingReadStream blob = new([.. tar, .. new byte[131_073]], tar.Length);
        using HashAlgorithm hash = SHA256.Create();
        Task scan = ImageLayerScanner.ScanAsync(blob, new(0, Digest(blob.ToArray())),
            cancellation.Token, hash);
        await blob.Draining.Task.WaitAsync(Token);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => scan);
    }

    private static byte[] Layer() =>
        DockerArchiveFixture.BuildTar(("file", new byte[] { 1, 2, 3 }));

    private static string Digest(byte[] bytes, bool sha512 = false) => sha512
        ? $"sha512:{Convert.ToHexStringLower(SHA512.HashData(bytes))}"
        : LayerCacheTestContext.Digest(bytes);

    private static byte[] Compress(byte[] raw)
    {
        using MemoryStream result = new();
        using (GZipStream gzip = new(result, CompressionMode.Compress, leaveOpen: true))
        {
            gzip.Write(raw);
        }
        return result.ToArray();
    }

    private static Dictionary<(string Digest, string DiffId), TaskCompletionSource<string>> Verifications(LayerStore store) =>
        (Dictionary<(string Digest, string DiffId), TaskCompletionSource<string>>)typeof(LayerStore)
            .GetProperty("contentVerifications", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(store)!;

    private static ExplorerSource Source(Content content, string diffId)
    {
        ImageName image = ImageName.Parse("registry.test/image:tag");
        DockerManifest manifest = new()
        {
            Layers = [new ManifestLayer { Digest = content.Digest, Size = content.Bytes.Length }]
        };
        ManifestInfo info = new("application/test", Digest([4]), manifest);
        ResolvedManifest resolved = new(info, manifest);
        Image config = new()
        {
            Os = "linux",
            Architecture = "amd64",
            RootFilesystem = new RootFilesystem { Type = "layers", DiffIds = [diffId] },
            History = [new LayerHistory { CreatedBy = "COPY file /file" }]
        };
        return new ExplorerSource
        {
            Image = image, Config = config, Resolved = resolved, Platforms = [],
            Source = new ResolvedImage(ImageSourceReference.Parse("registry://registry.test/image:tag"),
                image, resolved, info, config, [], content, Digest([5]), "linux/amd64")
        };
    }

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
        public string Digest { get; } = ColdIndexVerificationTests.Digest(bytes);
        public int Downloads { get; private set; }
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
            return new(new MemoryStream(bytes), false, null, null, bytes.Length);
        }
    }

    private sealed class PausingReadStream(byte[] bytes, int pauseAt) : MemoryStream(bytes)
    {
        public TaskCompletionSource Draining { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (Position >= pauseAt)
            {
                Draining.TrySetResult();
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }
            int count = (int)Math.Min(buffer.Length, pauseAt - Position);
            return await base.ReadAsync(buffer[..count], cancellationToken);
        }
    }

}
