using Docker.DotNet.Models;
using Valleysoft.DockerRegistryClient;
using Valleysoft.Dredge.Commands;
using Valleysoft.Dredge.Explorer;
using Valleysoft.Dredge.Explorer.Tui;
using FakeEngine = Valleysoft.Dredge.Tests.ImageSourceFactoryTests.FakeEngine;

namespace Valleysoft.Dredge.Tests;

public sealed class LiveDaemonArchiveTests : IDisposable
{
    private readonly string directory = Path.GetFullPath(Path.Combine(".test-live-daemon", Guid.NewGuid().ToString("N")));
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private static PlatformOptionsBase Options() => new() { DockerHost = "http://localhost:2375" };

    [Fact]
    public async Task ExplorerSameImmutableImage_ExportsOnlyOnceUntilAllOwnersDrain()
    {
        await using LayerCacheTestContext cache = new();
        DockerArchiveFixture fixture = DockerArchiveFixture.Create();
        List<FakeEngine> engines = [];
        IDockerRegistryClientFactory registries = Mock.Of<IDockerRegistryClientFactory>();
        ImageSourceFactory factory = Factory(() =>
        {
            FakeEngine engine = new(fixture);
            engines.Add(engine);
            return engine;
        });
        await using ExplorerSource baseline = await ExplorerSource.OpenAsync(
            factory, registries, "docker://app:latest", Options(), null, Token, store: cache.Store);
        ExplorerSession session = await ExplorerSession.CreateAsync(null, baseline, cache.Store, null, Token);
        ExplorerLayerIndexer indexer = ExplorerLayerIndexer.Create(null, baseline, cache.Store);
        await using ExplorerHost host = new(null, registries, baseline, cache.Store,
            ExplorerImage.FromSource(baseline), indexer, new(null, null, false, ClipboardMode.Off, "", ""))
        { Session = session };

        ExplorerComparison comparison = await host.CompareAsync("docker://" + fixture.Id, () => { }, Token);

        Assert.Equal(comparison.Baseline.Source!.Identity, comparison.Target.Source!.Identity);
        Assert.Same(comparison.Baseline.Source.Content, comparison.Target.Source.Content);
        Assert.Empty(comparison.Files);
        Assert.Single(engines.SelectMany(engine => engine.Exports));
        Assert.Equal(["app:latest", fixture.Id], engines[0].Inspections);
        Assert.Equal([fixture.Id], engines[1].Inspections);
        Assert.Equal(fixture.Export.Length, new FileInfo(Assert.Single(Spools())).Length);
        await host.DisposeAsync();
        Assert.Single(Spools());
        await baseline.DisposeAsync();
        Assert.Empty(Spools());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Aliases_InspectIndependentlyAndReleaseIdempotentLeases(bool disposeFirst)
    {
        DockerArchiveFixture fixture = DockerArchiveFixture.Create();
        List<FakeEngine> engines = [];
        ImageSourceFactory factory = Factory(() =>
        {
            FakeEngine engine = new(fixture);
            engines.Add(engine);
            return engine;
        });
        await using ResolvedImage first = await factory.OpenAsync("docker://app:latest", Options(), Token);
        await using ResolvedImage second = await factory.OpenAsync("docker://alias", Options(), Token);
        Assert.Same(first.Content, second.Content);
        Assert.Single(engines.SelectMany(engine => engine.Exports));
        Assert.Equal(["app:latest", fixture.Id], engines[0].Inspections);
        Assert.Equal(["alias", fixture.Id], engines[1].Inspections);
        ResolvedImage released = disposeFirst ? first : second;
        ResolvedImage retained = disposeFirst ? second : first;
        await released.DisposeAsync();
        await released.DisposeAsync();
        Assert.Single(Spools());
        await AssertReadableAsync(retained, fixture);
        await retained.DisposeAsync();
        Assert.Empty(Spools());

        await using ResolvedImage reopened = await factory.OpenAsync("docker://app:latest", Options(), Token);
        Assert.NotSame(first.Content, reopened.Content);
        Assert.Equal(2, engines.Sum(engine => engine.Exports.Count));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConcurrentAliases_JoinExportAndCanceledWaiterDoesNotCancelOwner(bool comparisonScope)
    {
        DockerArchiveFixture fixture = DockerArchiveFixture.Create();
        TaskCompletionSource release = Signal();
        List<ControlledEngine> engines = [];
        ImageSourceFactory factory = Factory(() =>
        {
            ControlledEngine engine = new(fixture, async token =>
            {
                await release.Task.WaitAsync(token);
                return new MemoryStream(fixture.Export);
            });
            engines.Add(engine);
            return engine;
        });
        await using ImageSourceFactory.ComparisonScope? scope = comparisonScope ? factory.CreateComparisonScope() : null;
        IImageSourceFactory sources = scope is null ? factory : scope;
        using CancellationTokenSource waiterCancellation = CancellationTokenSource.CreateLinkedTokenSource(Token);
        Task<ResolvedImage> owner = sources.OpenAsync("docker://app", Options(), Token);
        Task<ResolvedImage> waiter = sources.OpenAsync("docker://alias", Options(), waiterCancellation.Token);
        try
        {
            Assert.Equal(1, engines.Sum(engine => engine.Saves));
            Assert.All(engines, engine => Assert.Equal(2, engine.Inner.Inspections.Count));
            await waiterCancellation.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiter.WaitAsync(TimeSpan.FromSeconds(10), Token));
            Assert.False(owner.IsCompleted);
        }
        finally
        {
            release.TrySetResult();
        }
        await using ResolvedImage image = await owner.WaitAsync(TimeSpan.FromSeconds(10), Token);
        await AssertReadableAsync(image, fixture);
        Assert.Single(Spools());
        await image.DisposeAsync();
        if (scope is not null)
        {
            await scope.DisposeAsync();
        }
        Assert.Empty(Spools());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CanceledExportInitiator_DoesNotCancelSurvivingWaiter(bool comparisonScope)
    {
        DockerArchiveFixture fixture = DockerArchiveFixture.Create();
        TaskCompletionSource release = Signal();
        List<ControlledEngine> engines = [];
        ImageSourceFactory factory = Factory(() =>
        {
            ControlledEngine engine = new(fixture, async token =>
            {
                await release.Task.WaitAsync(token);
                return new MemoryStream(fixture.Export);
            });
            engines.Add(engine);
            return engine;
        });
        await using ImageSourceFactory.ComparisonScope? scope = comparisonScope ? factory.CreateComparisonScope() : null;
        IImageSourceFactory sources = scope is null ? factory : scope;
        using CancellationTokenSource firstCancellation = CancellationTokenSource.CreateLinkedTokenSource(Token);
        Task<ResolvedImage> first = sources.OpenAsync("docker://app", Options(), firstCancellation.Token);
        Task<ResolvedImage> survivor = sources.OpenAsync("docker://alias", Options(), Token);
        try
        {
            await firstCancellation.CancelAsync();
            Assert.False(survivor.IsCompleted);
        }
        finally
        {
            release.TrySetResult();
        }
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first.WaitAsync(TimeSpan.FromSeconds(10), Token));
        await using ResolvedImage image = await survivor.WaitAsync(TimeSpan.FromSeconds(10), Token);
        Assert.Equal(1, engines.Sum(engine => engine.Saves));
        await AssertReadableAsync(image, fixture);
    }

    [Fact]
    public async Task FailedExport_IsEvictedAndRetriedWithoutRetainingEngine()
    {
        DockerArchiveFixture fixture = DockerArchiveFixture.Create();
        IOException expected = new("Export failed.");
        int calls = 0;
        List<ControlledEngine> engines = [];
        ImageSourceFactory factory = Factory(() =>
        {
            bool fail = calls++ == 0;
            ControlledEngine engine = new(fixture, _ => fail
                ? Task.FromException<Stream>(expected)
                : Task.FromResult<Stream>(new MemoryStream(fixture.Export)));
            engines.Add(engine);
            return engine;
        });
        IOException actual = await Assert.ThrowsAsync<IOException>(() =>
            factory.OpenAsync("docker://app", Options(), Token));
        Assert.Same(expected, actual);
        Assert.Empty(Spools());
        await using ResolvedImage retry = await factory.OpenAsync("docker://app", Options(), Token);
        Assert.All(engines, engine => Assert.True(engine.Inner.Disposed));
        Assert.Equal(2, engines.Sum(engine => engine.Saves));
        await AssertReadableAsync(retry, fixture);
    }

    [Fact]
    public async Task ConcurrentFailedExport_PropagatesOriginalFailureAndRetriesOnSameFactory()
    {
        DockerArchiveFixture fixture = DockerArchiveFixture.Create();
        IOException expected = new("The shared export failed.");
        TaskCompletionSource release = Signal();
        int calls = 0;
        List<ControlledEngine> engines = [];
        ImageSourceFactory factory = Factory(() =>
        {
            bool fail = calls++ == 0;
            ControlledEngine engine = new(fixture, async token =>
            {
                if (fail)
                {
                    await release.Task.WaitAsync(token);
                    throw expected;
                }
                return new MemoryStream(fixture.Export);
            });
            engines.Add(engine);
            return engine;
        });
        Task<ResolvedImage> first = factory.OpenAsync("docker://app", Options(), Token);
        Task<ResolvedImage> second = factory.OpenAsync("docker://alias", Options(), Token);
        try
        {
            Assert.Equal(1, engines.Sum(engine => engine.Saves));
        }
        finally
        {
            release.TrySetResult();
        }
        Assert.Same(expected, await Assert.ThrowsAsync<IOException>(() => first.WaitAsync(TimeSpan.FromSeconds(10), Token)));
        Assert.Same(expected, await Assert.ThrowsAsync<IOException>(() => second.WaitAsync(TimeSpan.FromSeconds(10), Token)));
        Assert.Empty(Spools());

        await using ResolvedImage retry = await factory.OpenAsync("docker://app", Options(), Token);
        Assert.Equal(2, engines.Sum(engine => engine.Saves));
        Assert.All(engines, engine => Assert.True(engine.Inner.Disposed));
        await AssertReadableAsync(retry, fixture);
    }

    [Fact]
    public async Task AllCanceledWaiters_DrainExportBeforeReturningAndAllowRetry()
    {
        DockerArchiveFixture fixture = DockerArchiveFixture.Create();
        TaskCompletionSource copying = Signal();
        TaskCompletionSource canceled = Signal();
        TaskCompletionSource drain = Signal();
        BlockingExport export = new(fixture.Export, copying, canceled, drain);
        int calls = 0;
        List<ControlledEngine> engines = [];
        ImageSourceFactory factory = Factory(() =>
        {
            bool blocked = calls++ == 0;
            ControlledEngine engine = new(fixture, _ => Task.FromResult<Stream>(
                blocked ? export : new MemoryStream(fixture.Export)));
            engines.Add(engine);
            return engine;
        });
        using CancellationTokenSource cancellation = CancellationTokenSource.CreateLinkedTokenSource(Token);
        Task<ResolvedImage> first = factory.OpenAsync("docker://app", Options(), cancellation.Token);
        Task<ResolvedImage> second = factory.OpenAsync("docker://alias", Options(), cancellation.Token);
        try
        {
            await copying.Task.WaitAsync(TimeSpan.FromSeconds(10), Token);
            await cancellation.CancelAsync();
            await canceled.Task.WaitAsync(TimeSpan.FromSeconds(10), Token);
            Assert.False(first.IsCompleted);
            Assert.True(export.Disposed);
            Assert.False(export.CopyCompleted);
            Assert.False(engines[0].Inner.Disposed);
            Assert.Single(Spools());
        }
        finally
        {
            drain.TrySetResult();
        }
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first.WaitAsync(TimeSpan.FromSeconds(10), Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => second.WaitAsync(TimeSpan.FromSeconds(10), Token));
        Assert.True(export.Disposed);
        Assert.True(export.CopyCompleted);
        Assert.All(engines, engine => Assert.True(engine.Inner.Disposed));
        Assert.Empty(Spools());

        await using ResolvedImage retry = await factory.OpenAsync("docker://app", Options(), Token);
        Assert.Equal(2, engines.Sum(engine => engine.Saves));
        await AssertReadableAsync(retry, fixture);
    }

    [Fact]
    public async Task LastReleaseRacingNewAcquisition_NeverResurrectsDisposedArchive()
    {
        DockerArchiveFixture fixture = DockerArchiveFixture.Create();
        ImageSourceFactory factory = Factory(() => new FakeEngine(fixture));
        for (int iteration = 0; iteration < 16; iteration++)
        {
            ResolvedImage previous = await factory.OpenAsync("docker://app", Options(), Token);
            TaskCompletionSource start = Signal();
            Task release = Task.Run(async () =>
            {
                await start.Task;
                await previous.DisposeAsync();
            }, Token);
            Task<ResolvedImage> acquire = Task.Run(async () =>
            {
                await start.Task;
                return await factory.OpenAsync("docker://alias", Options(), Token);
            }, Token);
            start.SetResult();
            await release.WaitAsync(TimeSpan.FromSeconds(10), Token);
            await using ResolvedImage current = await acquire.WaitAsync(TimeSpan.FromSeconds(10), Token);
            Assert.Single(Spools());
            await AssertReadableAsync(current, fixture);
            await current.DisposeAsync();
            Assert.Empty(Spools());
        }
    }

    [Fact]
    public async Task CleanupFailure_EvictsFailedArchiveInsteadOfResurrectingIt()
    {
        DockerArchiveFixture fixture = DockerArchiveFixture.Create();
        List<FakeEngine> engines = [];
        ImageSourceFactory factory = Factory(() =>
        {
            FakeEngine engine = new(fixture);
            engines.Add(engine);
            return engine;
        });
        ResolvedImage first = await factory.OpenAsync("docker://app", Options(), Token);
        Directory.Delete(Assert.Single(Directory.GetDirectories(directory)), recursive: true);

        await Assert.ThrowsAsync<DirectoryNotFoundException>(() => first.DisposeAsync().AsTask());
        await first.DisposeAsync();
        await using ResolvedImage retry = await factory.OpenAsync("docker://alias", Options(), Token);

        Assert.NotSame(first.Content, retry.Content);
        Assert.Equal(2, engines.Sum(engine => engine.Exports.Count));
        await AssertReadableAsync(retry, fixture);
    }

    [Theory]
    [InlineData("daemon")]
    [InlineData("platform")]
    [InlineData("immutable-id")]
    [InlineData("immutable-inspection")]
    [InlineData("capability")]
    public async Task LiveArchive_DoesNotHideInvalidIndependentInspection(string mismatch)
    {
        DockerArchiveFixture fixture = DockerArchiveFixture.Create();
        int calls = 0;
        ControlledEngine invalid = new(fixture, _ => throw new InvalidOperationException("Must not export."),
            reference => new ImageInspectResponse
            {
                ID = mismatch == "immutable-id" ? "invalid" :
                    mismatch == "immutable-inspection" && reference == fixture.Id ? "sha256:" + new string('a', 64) : fixture.Id,
                Os = mismatch == "capability" ? "windows" : "linux",
                Architecture = mismatch == "platform" ? "arm64" : "amd64"
            }, mismatch == "daemon" ? "different-daemon" : "daemon");
        ImageSourceFactory factory = Factory(() => calls++ == 0 ? new FakeEngine(fixture) : invalid);
        await using ResolvedImage live = await factory.OpenAsync("docker://app", Options(), Token);
        PlatformOptionsBase options = Options();
        if (mismatch == "platform")
        {
            options.Architecture = "amd64";
        }

        await Assert.ThrowsAnyAsync<Exception>(() => factory.OpenAsync(
            "docker://alias", options, Token, mismatch == "capability"
                ? ImageSourceRequirements.LinuxFileSystem : ImageSourceRequirements.LayerContent));

        Assert.Equal(mismatch == "capability" ? 1 : 0, invalid.Saves);
        Assert.Single(Spools());
        await AssertReadableAsync(live, fixture);
    }

    [Fact]
    public async Task StrongerRequirements_DoNotReuseLiveMetadataArchive()
    {
        DockerArchiveFixture fixture = DockerArchiveFixture.Create(corruptLayer: true);
        List<FakeEngine> engines = [];
        ImageSourceFactory factory = Factory(() =>
        {
            FakeEngine engine = new(fixture);
            engines.Add(engine);
            return engine;
        });
        await using ResolvedImage metadata = await factory.OpenAsync(
            "docker://app", Options(), Token, ImageSourceRequirements.Metadata);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            factory.OpenAsync("docker://alias", Options(), Token, ImageSourceRequirements.LayerContent));

        Assert.Equal(2, engines.Sum(engine => engine.Exports.Count));
        Assert.Single(Spools());
    }

    [Theory]
    [InlineData("os")]
    [InlineData("architecture")]
    [InlineData("os-version")]
    [InlineData("variant")]
    public async Task DifferentPinnedPlatform_DoesNotReuseLiveArchive(string changedPart)
    {
        DockerArchiveFixture fixture = DockerArchiveFixture.Create();
        IOException expected = new("A different inspected platform requires an independent export.");
        int calls = 0;
        ControlledEngine different = new(fixture, _ => Task.FromException<Stream>(expected),
            _ => new ImageInspectResponse
            {
                ID = fixture.Id,
                Os = changedPart == "os" ? "windows" : "linux",
                Architecture = changedPart == "architecture" ? "arm64" : "amd64",
                OsVersion = changedPart == "os-version" ? "different-version" : null,
                Variant = changedPart == "variant" ? "different-variant" : null
            });
        ImageSourceFactory factory = Factory(() => calls++ == 0 ? new FakeEngine(fixture) : different);
        await using ResolvedImage first = await factory.OpenAsync("docker://app", Options(), Token);

        IOException actual = await Assert.ThrowsAsync<IOException>(() =>
            factory.OpenAsync("docker://alias", Options(), Token));

        Assert.Same(expected, actual);
        Assert.Equal(1, different.Saves);
        Assert.Single(Spools());
        await AssertReadableAsync(first, fixture);
    }

    [Fact]
    public async Task RetaggedLiveReference_ExportsNewImmutableIdWithoutReplacingExistingLease()
    {
        DockerArchiveFixture original = DockerArchiveFixture.Create();
        DockerArchiveFixture replacement = DockerArchiveFixture.Create(scratch: true);
        DockerArchiveFixture current = original;
        List<FakeEngine> engines = [];
        ImageSourceFactory factory = Factory(() =>
        {
            FakeEngine engine = new(current);
            engines.Add(engine);
            return engine;
        });
        await using ResolvedImage previous = await factory.OpenAsync("docker://app:latest", Options(), Token);
        current = replacement;
        await using ResolvedImage next = await factory.OpenAsync("docker://app:latest", Options(), Token);
        Assert.NotSame(previous.Content, next.Content);
        Assert.Equal(original.Id, previous.Identity);
        Assert.Equal(replacement.Id, next.Identity);
        Assert.Equal(2, Spools().Length);
        Assert.Equal(2, engines.Sum(engine => engine.Exports.Count));
        await AssertReadableAsync(previous, original);
    }

    [Fact]
    public async Task ComparisonScope_RetainsLeaseAndRefreshesRetaggedReferenceNextScope()
    {
        DockerArchiveFixture original = DockerArchiveFixture.Create();
        DockerArchiveFixture replacement = DockerArchiveFixture.Create(scratch: true);
        DockerArchiveFixture current = original;
        List<FakeEngine> engines = [];
        ImageSourceFactory factory = Factory(() =>
        {
            FakeEngine engine = new(current);
            engines.Add(engine);
            return engine;
        });
        await using (ImageSourceFactory.ComparisonScope scope = factory.CreateComparisonScope())
        {
            ResolvedImage first = await scope.OpenAsync("docker://app", Options(), Token);
            await first.DisposeAsync();
            Assert.Single(Spools());
            await using ResolvedImage second = await factory.OpenAsync("docker://alias", Options(), Token);
            Assert.Same(first.Content, second.Content);
            Assert.Single(engines.SelectMany(engine => engine.Exports));
        }
        Assert.Empty(Spools());
        current = replacement;
        await using (ImageSourceFactory.ComparisonScope scope = factory.CreateComparisonScope())
        {
            await using ResolvedImage image = await scope.OpenAsync("docker://app", Options(), Token);
            Assert.Equal(replacement.Id, image.Identity);
        }
        Assert.Equal(2, engines.Sum(engine => engine.Exports.Count));
        Assert.Empty(Spools());
    }

    private ImageSourceFactory Factory(Func<IDockerImageEngine> engine) =>
        new(Mock.Of<IDockerRegistryClientFactory>(), new TestAppSettingsStore(new AppSettings(), settingsPath: ""),
            _ => engine(), directory);

    private string[] Spools() => Directory.Exists(directory)
        ? Directory.GetFiles(directory, "export.tar", SearchOption.AllDirectories) : [];

    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static async Task AssertReadableAsync(ResolvedImage image, DockerArchiveFixture fixture)
    {
        BlobDownloadResult result = await image.Content.GetRangeAsync(
            image.Resolved.Manifest.Layers[0].Digest!, 0, null, Token);
        await using Stream content = result.Content;
        using MemoryStream buffer = new();
        await content.CopyToAsync(buffer, Token);
        Assert.Equal(fixture.StoredLayer, buffer.ToArray());
    }

    public void Dispose()
    {
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private sealed class ControlledEngine(DockerArchiveFixture fixture, Func<CancellationToken, Task<Stream>> save,
        Func<string, ImageInspectResponse>? inspect = null, string daemonId = "daemon") : IDockerImageEngine
    {
        internal FakeEngine Inner { get; } = new(fixture);
        internal int Saves { get; private set; }
        public Task<string> GetDaemonIdentityAsync(CancellationToken cancellationToken) => Task.FromResult(daemonId);
        public async Task<ImageInspectResponse> InspectAsync(string reference, CancellationToken cancellationToken)
        {
            ImageInspectResponse result = await Inner.InspectAsync(reference, cancellationToken);
            return inspect is null ? result : inspect(reference);
        }
        public Task<Stream> SaveAsync(string imageId, CancellationToken cancellationToken)
        {
            Saves++;
            return save(cancellationToken);
        }
        public void Dispose() => Inner.Dispose();
    }

    private sealed class BlockingExport(byte[] bytes, TaskCompletionSource copying, TaskCompletionSource canceled,
        TaskCompletionSource drain) : MemoryStream(bytes)
    {
        internal bool Disposed { get; private set; }
        internal bool CopyCompleted { get; private set; }

        public override async Task CopyToAsync(Stream destination, int bufferSize, CancellationToken cancellationToken)
        {
            copying.TrySetResult();
            try
            {
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                canceled.TrySetResult();
                await drain.Task;
                throw;
            }
            finally
            {
                CopyCompleted = true;
            }
        }

        protected override void Dispose(bool disposing)
        {
            Disposed = true;
            base.Dispose(disposing);
        }
    }
}
