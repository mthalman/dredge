using System.Formats.Tar;
using System.Security.Cryptography;
using System.Text;
using Valleysoft.DockerRegistryClient;
using Valleysoft.DockerRegistryClient.Models.Images;
using Valleysoft.DockerRegistryClient.Models.Manifests;
using Valleysoft.DockerRegistryClient.Models.Manifests.Oci;
using Valleysoft.Dredge.Commands;
using Valleysoft.Dredge.Explorer;
using Valleysoft.Dredge.Explorer.Tui;

namespace Valleysoft.Dredge.Tests;

[Collection(ExplorerUiCollection.Name)]
public sealed class ExplorerDaemonTargetLifetimeTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task RepeatedUniqueExportsReleaseBlobPinsAfterReadersAndDisplayedTargetsDrain()
    {
        await using Harness harness = await Harness.CreateAsync(maxBytes: 0);
        ExplorerComparison first = await harness.Host.CompareAsync("first", () => { }, Token);
        using IDisposable reader = harness.Host.RetainComparison(first);
        for (int i = 0; i < 12; i++)
        {
            await harness.Host.CompareAsync($"revision{i}", () => { }, Token);
            Assert.InRange(harness.BlobPaths.Length, 1, 4);
        }
        Assert.NotEmpty((await harness.Host.DiffAsync(first, "file.txt", Token)).Lines!);
        reader.Dispose();
        await harness.Host.CompareAsync("last", () => { }, Token);
        Assert.InRange(harness.BlobPaths.Length, 1, 3);
        Assert.Equal(2, harness.TargetExports.Count(export => !export.Owner.Disposed));
    }

    [Fact]
    public async Task RepeatedUniqueExportsKeepCurrentAndOneInactiveAndRevisitReacquires()
    {
        await using Harness harness = await Harness.CreateAsync();
        for (int i = 0; i < 10; i++)
        {
            ExplorerComparison comparison = await harness.Host.CompareAsync($"revision{i}", () => { }, Token);
            Assert.False(harness.Exports[^1].Owner.Disposed);
            Assert.Equal($"docker://app:revision{i}", comparison.Target.Source!.Reference.ToString());
            Assert.True(harness.TargetExports.Count(export => !export.Owner.Disposed) <= 2);
        }
        Assert.Equal(8, harness.TargetExports.Count(export => export.Owner.Disposed));
        Assert.False(harness.Exports[0].Owner.Disposed);

        await harness.Host.CompareAsync("revision0", () => { }, Token);

        Assert.Equal(2, harness.TargetExports.Count(export => export.Reference == "docker://app:revision0"));
        Assert.Equal(2, harness.TargetExports.Count(export => !export.Owner.Disposed));
        await harness.Host.DisposeAsync();
        await harness.Host.DisposeAsync();
        Assert.All(harness.TargetExports, export => Assert.Equal(1, export.Owner.DisposeCount));
        Assert.False(harness.Exports[0].Owner.Disposed);
    }

    [Fact]
    public async Task DisplayedAndQueuedReaderSurviveSeveralNewComparisonsUntilTheirPinsDrain()
    {
        await using Harness harness = await Harness.CreateAsync();
        ExplorerComparison first = await harness.Host.CompareAsync("first", () => { }, Token);
        Export firstExport = harness.Exports[^1];
        using IDisposable reader = harness.Host.RetainComparison(first);
        using RetainedExplorerComparison pending = await harness.Host.CompareRetainedAsync("pending", () => { }, Token);
        Export pendingExport = harness.Exports[^1];
        using RetainedExplorerComparison newer = await harness.Host.CompareRetainedAsync("newer", () => { }, Token);

        Assert.False(firstExport.Owner.Disposed);
        Assert.False(pendingExport.Owner.Disposed);
        Assert.NotEmpty((await harness.Host.DiffAsync(first, "file.txt", Token)).Lines!);

        harness.Host.DisplayComparison(newer.Comparison);
        newer.Dispose();
        for (int i = 0; i < 4; i++)
        {
            await harness.Host.CompareAsync($"next{i}", () => { }, Token);
        }
        Assert.False(firstExport.Owner.Disposed);
        Assert.False(pendingExport.Owner.Disposed);
        Assert.NotEmpty((await harness.Host.DiffAsync(first, "file.txt", Token)).Lines!);

        reader.Dispose();
        pending.Dispose();
        await harness.Host.CompareAsync("last", () => { }, Token);

        Assert.True(firstExport.Owner.Disposed);
        Assert.True(pendingExport.Owner.Disposed);
        Assert.Equal(2, harness.TargetExports.Count(export => !export.Owner.Disposed));
    }

    [Fact]
    public async Task FailedAndCanceledComparisonsDoNotReplaceDisplayedSourceOrAccumulateExports()
    {
        await using Harness harness = await Harness.CreateAsync();
        ExplorerComparison displayed = await harness.Host.CompareAsync("displayed", () => { }, Token);
        Export displayedExport = harness.Exports[^1];
        for (int i = 0; i < 6; i++)
        {
            if (i % 2 == 0)
            {
                await Assert.ThrowsAsync<IOException>(() => harness.Host.CompareRetainedAsync(
                    $"failed{i}", () => throw new IOException("package callback failed"), Token));
            }
            else
            {
                using CancellationTokenSource canceled = CancellationTokenSource.CreateLinkedTokenSource(Token);
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => harness.Host.CompareRetainedAsync(
                    $"canceled{i}", canceled.Cancel, canceled.Token));
            }
            Assert.False(displayedExport.Owner.Disposed);
            Assert.True(harness.TargetExports.Count(export => !export.Owner.Disposed) <= 2);
        }
        Assert.NotEmpty((await harness.Host.DiffAsync(displayed, "file.txt", Token)).Lines!);
        await harness.Host.DisposeAsync();
        Assert.All(harness.TargetExports, export => Assert.Equal(1, export.Owner.DisposeCount));
    }

    [Fact]
    public async Task CanceledAcquisitionAndFailedIndexingReleaseTheirUnpublishedSourcesOnce()
    {
        await using Harness harness = await Harness.CreateAsync();
        using CancellationTokenSource canceled = CancellationTokenSource.CreateLinkedTokenSource(Token);
        harness.Factory.BeforeReturn = reference =>
        {
            if (reference.EndsWith(":canceled", StringComparison.Ordinal))
            {
                canceled.Cancel();
            }
        };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            harness.Host.CompareAsync("canceled", () => { }, canceled.Token));
        Assert.Equal(1, harness.Exports[^1].Owner.DisposeCount);

        harness.Factory.FailContent = true;
        await Assert.ThrowsAsync<IOException>(() =>
            harness.Host.CompareAsync("bad-content", () => { }, Token));
        Assert.Equal(1, harness.Exports[^1].Owner.DisposeCount);
        await harness.Host.DisposeAsync();
        Assert.All(harness.TargetExports, export => Assert.Equal(1, export.Owner.DisposeCount));
    }

    [Fact]
    public async Task ShutdownDrainsPendingPublicationAndReadersBeforeExactlyOnceCleanup()
    {
        await using Harness harness = await Harness.CreateAsync();
        ExplorerComparison displayed = await harness.Host.CompareAsync("displayed", () => { }, Token);
        using IDisposable reader = harness.Host.RetainComparison(displayed);
        using RetainedExplorerComparison pending = await harness.Host.CompareRetainedAsync("pending", () => { }, Token);

        Task closing = harness.Host.DisposeAsync().AsTask();
        Assert.False(closing.IsCompleted);
        Assert.All(harness.TargetExports, export => Assert.False(export.Owner.Disposed));
        reader.Dispose();
        Assert.False(closing.IsCompleted);
        pending.Dispose();
        await closing.WaitAsync(TimeSpan.FromSeconds(10), Token);

        Assert.All(harness.TargetExports, export => Assert.Equal(1, export.Owner.DisposeCount));
        Assert.False(harness.Exports[0].Owner.Disposed);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task CancelingUiReaderDoesNotReleaseItsSourceUntilTheWorkDrains(int failureStage)
    {
        await using Harness harness = await Harness.CreateAsync();
        ExplorerComparison first = await harness.Host.CompareAsync("first", () => { }, Token);
        Export firstExport = harness.Exports[^1];
        using CancellationTokenSource canceled = CancellationTokenSource.CreateLinkedTokenSource(Token);
        using ExplorerUiHarness ui = new(160, 45, _ =>
            new(harness.Image, new(), harness.Host, Token));
        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int callbacks = 0;
        Task reader = ui.Window.RunAsync(async _ =>
        {
            entered.SetResult();
            await release.Task;
            Assert.False(firstExport.Owner.Disposed);
            return 1;
        }, _ => callbacks++, token: canceled.Token, retention: harness.Host.RetainComparison(first));
        void RunReaderChecks()
        {
            try
            {
                ui.Wait(async () => { await entered.Task; return true; }, "reader start");
                if (failureStage == 1)
                {
                    throw new IOException("comparison failed");
                }
                for (int i = 0; i < 4; i++)
                {
                    ui.Wait(() => harness.Host.CompareAsync($"next{i}", () => { }, Token), "new target");
                }
                canceled.Cancel();
                Assert.False(firstExport.Owner.Disposed);
                if (failureStage == 2)
                {
                    throw new IOException("reader assertion failed");
                }
            }
            finally
            {
                canceled.Cancel();
                release.TrySetResult();
                ui.Wait(async () => { await reader; return true; }, "canceled reader drain");
            }
        }

        if (failureStage == 0)
        {
            RunReaderChecks();
            Assert.True(firstExport.Owner.Disposed);
            Assert.Equal(2, harness.TargetExports.Count(export => !export.Owner.Disposed));
        }
        else
        {
            Assert.Throws<IOException>(RunReaderChecks);
            ui.Wait(async () => { await harness.Host.DisposeAsync(); return true; }, "failed reader shutdown");
            Assert.All(harness.TargetExports, export => Assert.Equal(1, export.Owner.DisposeCount));
        }
        Assert.Equal(0, callbacks);
    }

    [Fact]
    public async Task CanceledQueuedPublicationReleasesItsPinWithoutReplacingTheDisplayedTarget()
    {
        await using Harness harness = await Harness.CreateAsync();
        await harness.Host.CompareAsync("displayed", () => { }, Token);
        Export displayed = harness.Exports[^1];
        using CancellationTokenSource canceled = CancellationTokenSource.CreateLinkedTokenSource(Token);
        using ExplorerUiHarness ui = new(160, 45, _ =>
            new(harness.Image, new(), harness.Host, Token));
        using ManualResetEventSlim publicationQueued = new();
        int published = 0;
        Task queued = ui.Window.RunAsync(
            ct => harness.Host.CompareRetainedAsync("unpublished", () => { }, ct),
            result =>
            {
                using (result)
                {
                    published++;
                    harness.Host.DisplayComparison(result.Comparison);
                }
            }, token: canceled.Token, abandoned: result => result.Dispose());

        // Keep the UI thread occupied: yielding it to the pool can let Invoke run inline
        // on that same thread instead of queuing the publication for cancellation.
        _ = queued.ContinueWith(_ => publicationQueued.Set(), CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        Assert.True(publicationQueued.Wait(TimeSpan.FromSeconds(10), Token));
        Assert.True(queued.IsCompletedSuccessfully, queued.Exception?.ToString());
        Export unpublished = harness.Exports[^1];
        Assert.False(displayed.Owner.Disposed);
        canceled.Cancel();
        ui.Pump();
        Assert.Equal(0, published);
        Assert.False(displayed.Owner.Disposed);
        for (int i = 0; i < 3; i++)
        {
            ui.Wait(() => harness.Host.CompareAsync($"next{i}", () => { }, Token), "new target");
        }
        Assert.Equal(1, unpublished.Owner.DisposeCount);
        Assert.Equal(2, harness.TargetExports.Count(export => !export.Owner.Disposed));
    }

    [Fact]
    public async Task RetentionReportsEvictionErrorsAndStillCleansEveryExportExactlyOnce()
    {
        ExplorerTargetRetention<Owner> retention = new();
        Owner broken = new() { DisposalError = new IOException("export cleanup failed") };
        Owner other = new();
        using (retention.Add("broken", broken, broken.DisposeAsync)) { }
        using (retention.Add("other", other, other.DisposeAsync)) { }

        IOException error = await Assert.ThrowsAsync<IOException>(retention.CheckCleanupAsync);
        Assert.Equal("export cleanup failed", error.Message);
        Assert.Equal(1, retention.PendingCleanupCount);
        await Assert.ThrowsAsync<IOException>(() => retention.DisposeAsync().AsTask());
        await Assert.ThrowsAsync<IOException>(() => retention.DisposeAsync().AsTask());
        Assert.Equal(1, broken.DisposeCount);
        Assert.Equal(1, other.DisposeCount);
    }

    [Fact]
    public async Task CleanupBookkeepingPrunesSuccessfulTasksAndKeepsUnfinishedCleanupUntilShutdown()
    {
        ExplorerTargetRetention<Owner> retention = new();
        Owner pending = new();
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using (retention.Add("pending", pending, async () =>
        {
            await release.Task;
            await pending.DisposeAsync();
        })) { }
        for (int i = 0; i < 1000; i++)
        {
            Owner owner = new();
            using (retention.Add($"target{i}", owner, owner.DisposeAsync)) { }
            Assert.Equal(1, retention.PendingCleanupCount);
        }
        Assert.False(pending.Disposed);
        Task closing = retention.DisposeAsync().AsTask();
        Assert.False(closing.IsCompleted);
        release.SetResult();
        await closing.WaitAsync(TimeSpan.FromSeconds(10), Token);
        await retention.CheckCleanupAsync();
        Assert.Equal(0, retention.PendingCleanupCount);
        Assert.Equal(1, pending.DisposeCount);
    }

    [Fact]
    public async Task RegistryOnlyTargetsKeepTheirExistingCachedLifetime()
    {
        string cache = CachePath();
        List<string> resolvedTags = [];
        using MockRegistry registry = new(resolvedTags);
        RegistryFactory registries = new(registry.Client);
        try
        {
            await using LayerStore store = new(cache);
            await using ExplorerSource baseline = await ExplorerSource.OpenAsync(
                registry.Client, registries, ImageName.Parse("registry.test/app:baseline"),
                new(), null, Token);
            ExplorerSession session = await ExplorerSession.CreateAsync(registry.Client, baseline, store, null, Token);
            ExplorerLayerIndexer indexer = ExplorerLayerIndexer.Create(registry.Client, baseline, store);
            await using ExplorerHost host = new(registry.Client, registries, baseline, store,
                ExplorerImage.FromSource(baseline), indexer, new(null, null, false, ClipboardMode.Off, "", ""))
                { Session = session };
            for (int i = 0; i < 5; i++)
            {
                await host.CompareAsync($"revision{i}", () => { }, Token);
            }
            int beforeRevisit = resolvedTags.Count;
            await host.CompareAsync("revision0", () => { }, Token);
            Assert.Equal(beforeRevisit, resolvedTags.Count);
            registry.Mock.Verify(client => client.Dispose(), Times.Never);
            await host.DisposeAsync();
            registry.Mock.Verify(client => client.Dispose(), Times.Never);
        }
        finally
        {
            if (Directory.Exists(cache))
            {
                Directory.Delete(cache, true);
            }
        }
    }

    private static string CachePath() => Path.Combine(Directory.GetCurrentDirectory(),
        "explorer-target-lifetime-" + Guid.NewGuid().ToString("N"));

    private sealed class Harness : IAsyncDisposable
    {
        private readonly string cache = CachePath();
        private LayerStore store = null!;
        private ExplorerSource baseline = null!;
        public Factory Factory { get; } = new();
        public List<Export> Exports => Factory.Exports;
        public IEnumerable<Export> TargetExports => Exports.Skip(1);
        public ExplorerHost Host { get; private set; } = null!;
        public ExplorerImage Image { get; private set; } = null!;
        public string[] BlobPaths => Directory.GetFiles(Path.Combine(cache, "layer-store", "data"), "*.blob");

        public static async Task<Harness> CreateAsync(long maxBytes = CacheSettings.DefaultMaxBytes)
        {
            Harness harness = new();
            harness.store = new(harness.cache, maxBytes);
            RegistryFactory registries = new();
            harness.baseline = await ExplorerSource.OpenAsync(harness.Factory, registries,
                "docker://app:baseline", new() { DockerHost = "tcp://selected-daemon:2375" },
                null, Token, store: harness.store);
            ExplorerSession session = await ExplorerSession.CreateAsync(null, harness.baseline, harness.store, null, Token);
            ExplorerLayerIndexer indexer = ExplorerLayerIndexer.Create(null, harness.baseline, harness.store);
            harness.Image = ExplorerImage.FromSource(harness.baseline);
            harness.Image.SetSession(session, ExplorerInsights.Build(
                session.Analysis, harness.Image.Instructions, harness.Image.BaseLayerCount, includePotential: true));
            harness.Host = new(null, registries, harness.baseline, harness.store,
                harness.Image, indexer,
                new(null, null, false, ClipboardMode.Off, "", "")) { Session = session };
            return harness;
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                await Host.DisposeAsync();
            }
            finally
            {
                try
                {
                    await baseline.DisposeAsync();
                }
                finally
                {
                    await store.DisposeAsync();
                    if (Directory.Exists(cache))
                    {
                        Directory.Delete(cache, true);
                    }
                }
            }
        }
    }

    private sealed record Export(string Reference, Owner Owner);

    private sealed class Factory : IImageSourceFactory
    {
        private readonly Dictionary<string, string> pinnedReferences = [];
        public List<Export> Exports { get; } = [];
        public Action<string>? BeforeReturn { get; set; }
        public bool FailContent { get; set; }

        public Task<ResolvedImage> OpenAsync(string reference, PlatformOptionsBase options, CancellationToken token,
            ImageSourceRequirements requirements = ImageSourceRequirements.LayerContent)
        {
            token.ThrowIfCancellationRequested();
            Assert.Equal("tcp://selected-daemon:2375", options.DockerHost);
            string requestedReference = reference;
            reference = pinnedReferences.GetValueOrDefault(reference) ?? reference;
            Owner owner = new();
            Exports.Add(new(reference, owner));
            byte[] tar;
            using (MemoryStream buffer = new())
            {
                using (TarWriter writer = new(buffer, TarEntryFormat.Pax, leaveOpen: true))
                {
                    writer.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, "file.txt")
                    {
                        ModificationTime = DateTimeOffset.UnixEpoch,
                        DataStream = new MemoryStream(Encoding.UTF8.GetBytes(reference))
                    });
                }
                tar = buffer.ToArray();
            }
            string json = $$"""{"os":"linux","architecture":"amd64","rootfs":{"type":"layers","diff_ids":["{{Hash(tar)}}"]},"history":[{"created_by":"COPY file.txt /"}]}""";
            byte[] configuration = Encoding.UTF8.GetBytes(json);
            string configDigest = Hash(configuration);
            pinnedReferences["docker://" + configDigest] = reference;
            OciImageManifest manifest = new()
            {
                Config = new OciDescriptor { Digest = configDigest },
                Layers = [new OciDescriptor { Digest = Hash(tar), Size = tar.Length }]
            };
            ManifestInfo info = new("application/vnd.oci.image.manifest.v1+json", configDigest, manifest);
            ResolvedImage result = new(ImageSourceReference.Parse(requestedReference), ImageName.Parse("registry.test/app:current"),
                new(info, manifest), null, JsonHelper.Deserialize<Image>(json)!, configuration,
                new Content(tar, owner, FailContent), configDigest, "linux/amd64", owner);
            BeforeReturn?.Invoke(reference);
            return Task.FromResult(result);
        }
    }

    private static string Hash(byte[] bytes) => "sha256:" + Convert.ToHexStringLower(SHA256.HashData(bytes));

    private sealed class Content(byte[] bytes, Owner owner, bool fail) : IImageContentSource
    {
        public Task<BlobDownloadResult> GetRangeAsync(string digest, long start, long? length, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            ObjectDisposedException.ThrowIf(owner.Disposed, owner);
            if (fail)
            {
                throw new IOException("Export content unavailable.");
            }
            return Task.FromResult(new BlobDownloadResult(new MemoryStream(bytes), false, null, null, bytes.Length));
        }
    }

    private sealed class Owner : IAsyncDisposable
    {
        public int DisposeCount { get; private set; }
        public bool Disposed => DisposeCount != 0;
        public Exception? DisposalError { get; init; }
        public ValueTask DisposeAsync()
        {
            DisposeCount++;
            return DisposalError is null ? ValueTask.CompletedTask : ValueTask.FromException(DisposalError);
        }
    }

    private sealed class RegistryFactory(IDockerRegistryClient? client = null) : IDockerRegistryClientFactory
    {
        public Task<IDockerRegistryClient> GetClientAsync(string? registry, CancellationToken cancellationToken = default) =>
            Task.FromResult(client ?? throw new InvalidOperationException("A daemon target opened a registry."));
    }

    private sealed class MockRegistry : IDisposable
    {
        public Mock<IDockerRegistryClient> Mock { get; } = new() { DefaultValue = DefaultValue.Mock };
        public IDockerRegistryClient Client => Mock.Object;

        public MockRegistry(List<string> resolvedTags)
        {
            const string configJson = """{"os":"linux","architecture":"amd64","rootfs":{"type":"layers","diff_ids":[]},"history":[]}""";
            byte[] configuration = Encoding.UTF8.GetBytes(configJson);
            string configDigest = Hash(configuration);
            OciImageManifest manifest = new() { Config = new OciDescriptor { Digest = configDigest }, Layers = [] };
            Mock.Setup(client => client.Manifests.GetAsync("app", It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .Returns((string _, string tag, CancellationToken _) =>
                {
                    resolvedTags.Add(tag);
                    return Task.FromResult(new ManifestInfo("application/vnd.oci.image.manifest.v1+json",
                        Hash(Encoding.UTF8.GetBytes(tag)), manifest));
                });
            Mock.Setup(client => client.Blobs.GetAsync("app", configDigest, It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => new MemoryStream(configuration));
        }

        public void Dispose() => Client.Dispose();
    }
}
