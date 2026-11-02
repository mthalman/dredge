using System.Collections.Concurrent;
using System.IO.Compression;
using System.Text;
using System.Text.Json.Nodes;
using Valleysoft.DockerRegistryClient;

namespace Valleysoft.Dredge.Tests;

public class ParallelArchiveVerificationTests : IDisposable
{
    private readonly string directory = Path.GetFullPath(Path.Combine(".test-parallel-archives", Guid.NewGuid().ToString("N")));

    [Fact]
    public async Task Workers_AreConcurrentBoundedAndDeduplicateMembers()
    {
        TaskCompletionSource firstWave = NewSignal();
        TaskCompletionSource release = NewSignal();
        ConcurrentDictionary<int, int> visits = new();
        int started = 0;
        int active = 0;
        int completed = 0;
        Task operation = DockerImageArchive.VerifyMembersAsync(
            Enumerable.Range(0, 17).SelectMany(member => new[] { member, member }), async (member, token) =>
            {
                visits.AddOrUpdate(member, 1, (_, count) => count + 1);
                int concurrent = Interlocked.Increment(ref active);
                try
                {
                    Assert.InRange(concurrent, 1, 4);
                    if (Interlocked.Increment(ref started) == 4)
                    {
                        firstWave.SetResult();
                    }
                    await release.Task.WaitAsync(token);
                    Interlocked.Increment(ref completed);
                }
                finally
                {
                    Interlocked.Decrement(ref active);
                }
            }, TestContext.Current.CancellationToken);
        try
        {
            await WaitAsync(firstWave.Task);
            Assert.Equal(4, Volatile.Read(ref active));
            Assert.Equal(4, Volatile.Read(ref started));
            Assert.Equal(0, Volatile.Read(ref completed));
            Assert.False(operation.IsCompleted);
        }
        finally
        {
            release.TrySetResult();
            await WaitAsync(operation);
        }
        Assert.Equal(17, visits.Count);
        Assert.All(visits.Values, count => Assert.Equal(1, count));
        Assert.Equal(17, completed);
        Assert.Equal(0, active);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailureOrCallerCancellation_CancelsAndDrainsWorkers(bool callerCancellation)
    {
        using CancellationTokenSource cancellation = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken);
        TaskCompletionSource firstWave = NewSignal();
        TaskCompletionSource fail = NewSignal();
        TaskCompletionSource cancelled = NewSignal();
        TaskCompletionSource cleanup = NewSignal();
        InvalidDataException expected = new("original member failure");
        int started = 0;
        int observedCancellation = 0;
        int disposed = 0;
        int cancellationTarget = callerCancellation ? 4 : 3;
        Task operation = DockerImageArchive.VerifyMembersAsync(Enumerable.Range(0, 12), async (member, token) =>
        {
            if (Interlocked.Increment(ref started) == 4)
            {
                firstWave.SetResult();
            }
            try
            {
                if (!callerCancellation && member == 0)
                {
                    await fail.Task.WaitAsync(token);
                    throw expected;
                }
                try
                {
                    await Task.Delay(Timeout.Infinite, token);
                }
                catch (OperationCanceledException)
                {
                    if (Interlocked.Increment(ref observedCancellation) == cancellationTarget)
                    {
                        cancelled.SetResult();
                    }
                    await cleanup.Task;
                    throw;
                }
            }
            finally
            {
                Interlocked.Increment(ref disposed);
            }
        }, cancellation.Token);
        try
        {
            await WaitAsync(firstWave.Task);
            if (callerCancellation)
            {
                cancellation.Cancel();
            }
            else
            {
                fail.SetResult();
            }
            await WaitAsync(cancelled.Task);
            Assert.False(operation.IsCompleted);
            Assert.Equal(4, Volatile.Read(ref started));
            Assert.Equal(callerCancellation ? 0 : 1, Volatile.Read(ref disposed));
        }
        finally
        {
            fail.TrySetResult();
            cleanup.TrySetResult();
            if (callerCancellation)
            {
                cancellation.Cancel();
            }
        }
        if (callerCancellation)
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => WaitAsync(operation));
        }
        else
        {
            Assert.Same(expected, await Assert.ThrowsAsync<InvalidDataException>(() => WaitAsync(operation)));
        }
        Assert.Equal(4, disposed);
        Assert.Equal(4, started);
    }

    [Fact]
    public async Task Failure_IsNotReplacedByCancellationCallbackFailure()
    {
        TaskCompletionSource ready = NewSignal();
        InvalidDataException expected = new("original failure");
        int completed = 0;
        Task operation = DockerImageArchive.VerifyMembersAsync(new[] { 0, 1 }, async (member, token) =>
        {
            if (member == 0)
            {
                await ready.Task.WaitAsync(token);
                throw expected;
            }
            Task pending = Task.Delay(Timeout.Infinite, token);
            using CancellationTokenRegistration registration = token.Register(() =>
                throw new InvalidOperationException("cancellation callback failure"));
            ready.SetResult();
            try
            {
                await pending;
            }
            finally
            {
                Interlocked.Increment(ref completed);
            }
        }, TestContext.Current.CancellationToken);
        Assert.Same(expected, await Assert.ThrowsAsync<InvalidDataException>(() => WaitAsync(operation)));
        Assert.Equal(1, completed);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EmptyOrPrecancelledWork_DoesNotInvokeVerifier(bool cancelled)
    {
        using CancellationTokenSource cancellation = new();
        if (cancelled)
        {
            cancellation.Cancel();
        }
        int calls = 0;
        int[] members = cancelled ? [1] : [];
        Task operation = DockerImageArchive.VerifyMembersAsync(members,
            (_, _) =>
            {
                Interlocked.Increment(ref calls);
                return Task.CompletedTask;
            }, cancellation.Token);
        if (cancelled)
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation);
        }
        else
        {
            await operation;
        }
        Assert.Equal(0, calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LateCallerCancellationTakesPrecedenceWhenContinuationResumes(bool fail)
    {
        using CancellationTokenSource cancellation = new();
        using QueuedContext context = new();
        TaskCompletionSource release = NewSignal();
        SynchronizationContext? previous = SynchronizationContext.Current;
        Task operation;
        SynchronizationContext.SetSynchronizationContext(context);
        try
        {
            operation = DockerImageArchive.VerifyMembersAsync(new[] { 0 }, async (_, _) =>
            {
                await release.Task.ConfigureAwait(false);
                if (fail)
                {
                    throw new InvalidDataException("member failure");
                }
            }, cancellation.Token);
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previous);
        }
        release.SetResult();
        Assert.True(context.Callbacks.TryTake(out Action? resume, TimeSpan.FromSeconds(10)));
        Assert.False(operation.IsCompleted);
        cancellation.Cancel();
        resume();

        OperationCanceledException error =
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => WaitAsync(operation));

        Assert.Equal(cancellation.Token, error.CancellationToken);
        Assert.True(operation.IsCanceled);
    }

    [Fact]
    public async Task WorkerCancellationPreservesOriginalExceptionAndToken()
    {
        using CancellationTokenSource unrelated = new();
        OperationCanceledException expected = new("worker cancellation", null, unrelated.Token);

        Task operation = DockerImageArchive.VerifyMembersAsync(new[] { 0 },
            (_, _) => Task.FromException(expected), TestContext.Current.CancellationToken);

        Assert.Same(expected, await Assert.ThrowsAnyAsync<OperationCanceledException>(() => WaitAsync(operation)));
        Assert.True(operation.IsCanceled);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DistinctAndRepeatedMembers_PreserveOrderedDescriptorsAndBytes(bool metadataOnly)
    {
        ArchiveFixture fixture = CreateArchive();
        await using DockerImageArchive archive = await OpenAsync(fixture, metadataOnly);
        Assert.Equal(fixture.Configuration, archive.ConfigurationJson);
        Assert.Equal(fixture.Digests, archive.Resolved.Manifest.Layers.Select(layer => layer.Digest).ToArray());
        Assert.Equal(fixture.Sizes, archive.Resolved.Manifest.Layers.Select(layer => layer.Size).ToArray());
        for (int i = 0; i < fixture.Digests.Length; i++)
        {
            Assert.Equal("application/vnd.oci.image.layer.v1.tar+gzip", archive.Resolved.Manifest.Layers[i].MediaType);
            BlobDownloadResult result = await archive.GetRangeAsync(fixture.Digests[i], 0, null,
                TestContext.Current.CancellationToken);
            await using Stream input = result.Content;
            using MemoryStream bytes = new();
            await input.CopyToAsync(bytes, TestContext.Current.CancellationToken);
            Assert.Equal(fixture.StoredLayers[i], bytes.ToArray());
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RepeatedMember_ValidatesEachExpectedDiffId(bool metadataOnly)
    {
        ArchiveFixture fixture = CreateArchive(wrongRepeatedDiffId: true);
        if (metadataOnly)
        {
            await using DockerImageArchive archive = await OpenAsync(fixture, metadataOnly);
            Assert.Equal(6, archive.Resolved.Manifest.Layers.Length);
            return;
        }
        InvalidDataException error = await Assert.ThrowsAsync<InvalidDataException>(() => OpenAsync(fixture));
        Assert.Contains("layer 3", error.Message);
        Assert.Empty(Directory.EnumerateFileSystemEntries(directory));
    }

    [Theory]
    [InlineData("size", false)]
    [InlineData("size", true)]
    [InlineData("digest", false)]
    [InlineData("digest", true)]
    [InlineData("encoding", false)]
    [InlineData("unsupported", false)]
    [InlineData("gzip", false)]
    public async Task MalformedMemberOrRepeatedDescriptor_IsRejectedAndCleaned(string defect, bool metadataOnly)
    {
        ArchiveFixture fixture = CreateArchive(defect: defect);
        await Assert.ThrowsAsync<InvalidDataException>(() => OpenAsync(fixture, metadataOnly));
        Assert.Empty(Directory.EnumerateFileSystemEntries(directory));
    }

    [Theory]
    [InlineData("encoding")]
    [InlineData("unsupported")]
    [InlineData("gzip")]
    public async Task MetadataOnly_SkipsDecompressionAndEncodingValidation(string defect)
    {
        ArchiveFixture fixture = CreateArchive(defect: defect);
        await using DockerImageArchive archive = await OpenAsync(fixture, metadataOnly: true);
        Assert.Equal(fixture.Digests, archive.Resolved.Manifest.Layers.Select(layer => layer.Digest).ToArray());
        Assert.Equal(defect switch
        {
            "encoding" => DockerImageArchive.RawLayerMediaType,
            "unsupported" => "application/vnd.oci.image.layer.v1.tar+zstd",
            _ => "application/vnd.oci.image.layer.v1.tar+gzip"
        }, archive.Resolved.Manifest.Layers[2].MediaType);
    }

    [Fact]
    public async Task ReadOnlyMember_FlushPreservesPositionAndAsyncCancellation()
    {
        ArchiveFixture fixture = CreateArchive();
        await using DockerImageArchive archive = await OpenAsync(fixture);
        BlobDownloadResult result = await archive.GetRangeAsync(fixture.Digests[0], 3, 10,
            TestContext.Current.CancellationToken);
        await using Stream input = result.Content;
        input.Flush();
        await input.FlushAsync(TestContext.Current.CancellationToken);
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => input.FlushAsync(cancellation.Token));
        Assert.False(input.CanWrite);
        Assert.Equal(0, input.Position);
        Assert.Equal(10, input.Length);
        Assert.Equal(fixture.StoredLayers[0][3], input.ReadByte());
        Assert.Throws<NotSupportedException>(() => input.WriteByte(0));
        Assert.Throws<NotSupportedException>(() => input.SetLength(0));
    }

    private Task<DockerImageArchive> OpenAsync(ArchiveFixture fixture, bool metadataOnly = false) =>
        DockerImageArchive.OpenAsync(new MemoryStream(fixture.Export), DockerImageArchive.Hash(fixture.Configuration),
            TestContext.Current.CancellationToken, directory, verifyLayerContent: !metadataOnly);

    private static ArchiveFixture CreateArchive(bool wrongRepeatedDiffId = false, string? defect = null)
    {
        int[] order = [3, 0, 3, 2, 1, 4];
        byte[][] contents = Enumerable.Range(0, 5).Select(index =>
            DockerArchiveFixture.BuildTar(($"layer-{index}", Encoding.UTF8.GetBytes($"content-{index}")))).ToArray();
        byte[][] stored = contents.Select(content =>
        {
            using MemoryStream buffer = new();
            using (GZipStream gzip = new(buffer, CompressionLevel.Fastest, leaveOpen: true))
            {
                gzip.Write(content);
            }
            return buffer.ToArray();
        }).ToArray();
        if (defect == "gzip")
        {
            stored[3][^8] ^= 0xff;
        }
        JsonArray diffIds = new();
        foreach (int index in order)
        {
            diffIds.Add(DockerImageArchive.Hash(contents[index]));
        }
        if (wrongRepeatedDiffId)
        {
            diffIds[2] = DockerImageArchive.Hash("wrong diff ID"u8);
        }
        JsonObject configuration = new()
        {
            ["os"] = "linux",
            ["architecture"] = "amd64",
            ["rootfs"] = new JsonObject { ["type"] = "layers", ["diff_ids"] = diffIds }
        };
        byte[] config = Encode(configuration);
        string id = DockerImageArchive.Hash(config);
        Dictionary<string, byte[]> entries = new(StringComparer.Ordinal)
        {
            ["oci-layout"] = """{"imageLayoutVersion":"1.0.0"}"""u8.ToArray(),
            ["blobs/sha256/" + id[7..]] = config
        };
        JsonArray layers = new();
        foreach (int index in order)
        {
            string digest = DockerImageArchive.Hash(stored[index]);
            entries["blobs/sha256/" + digest[7..]] = stored[index];
            layers.Add(Descriptor(digest, stored[index].Length, "application/vnd.oci.image.layer.v1.tar+gzip"));
        }
        switch (defect)
        {
            case "size":
                layers[2]!["size"] = stored[3].Length + 1;
                break;
            case "digest":
                string incorrect = DockerImageArchive.Hash("incorrect stored digest"u8);
                entries["blobs/sha256/" + incorrect[7..]] = stored[3];
                layers[2]!["digest"] = incorrect;
                break;
            case "encoding":
                layers[2]!["mediaType"] = DockerImageArchive.RawLayerMediaType;
                break;
            case "unsupported":
                layers[2]!["mediaType"] = "application/vnd.oci.image.layer.v1.tar+zstd";
                break;
        }
        byte[] manifest = Encode(new JsonObject
        {
            ["schemaVersion"] = 2,
            ["mediaType"] = "application/vnd.oci.image.manifest.v1+json",
            ["config"] = Descriptor(id, config.Length, "application/vnd.oci.image.config.v1+json"),
            ["layers"] = layers
        });
        string manifestDigest = DockerImageArchive.Hash(manifest);
        entries["blobs/sha256/" + manifestDigest[7..]] = manifest;
        entries["index.json"] = Encode(new JsonObject
        {
            ["schemaVersion"] = 2,
            ["manifests"] = new JsonArray(Descriptor(manifestDigest, manifest.Length, "application/vnd.oci.image.manifest.v1+json"))
        });
        return new ArchiveFixture(DockerArchiveFixture.BuildTar([.. entries.Select(entry => (entry.Key, entry.Value))]),
            config, order.Select(index => DockerImageArchive.Hash(stored[index])).ToArray(),
            order.Select(index => (long)stored[index].Length).ToArray(), order.Select(index => stored[index]).ToArray());
    }

    private static JsonObject Descriptor(string digest, long size, string mediaType) => new()
    {
        ["digest"] = digest,
        ["size"] = size,
        ["mediaType"] = mediaType
    };

    private static byte[] Encode(JsonObject value) => Encoding.UTF8.GetBytes(value.ToJsonString());

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private sealed class QueuedContext : SynchronizationContext, IDisposable
    {
        internal BlockingCollection<Action> Callbacks { get; } = new();

        public override void Post(SendOrPostCallback callback, object? state) =>
            Callbacks.Add(() => callback(state));

        public void Dispose() => Callbacks.Dispose();
    }

    private static Task WaitAsync(Task task) =>
        task.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

    public void Dispose()
    {
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private sealed record ArchiveFixture(byte[] Export, byte[] Configuration, string[] Digests, long[] Sizes, byte[][] StoredLayers);
}
