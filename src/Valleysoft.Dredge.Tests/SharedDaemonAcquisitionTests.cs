using Docker.DotNet.Models;
using Valleysoft.DockerRegistryClient;
using Valleysoft.Dredge.Commands;
using FakeEngine = Valleysoft.Dredge.Tests.ImageSourceFactoryTests.FakeEngine;

namespace Valleysoft.Dredge.Tests;

public sealed class SharedDaemonAcquisitionTests : IDisposable
{
    private readonly string directory = Path.GetFullPath(Path.Combine(".test-shared-daemon", Guid.NewGuid().ToString("N")));
    private readonly string cache = Path.GetFullPath(Path.Combine(".test-shared-indexes", Guid.NewGuid().ToString("N")));

    [Theory]
    [InlineData("docker://base:tag", true)]
    [InlineData("docker://base:tag", false)]
    [InlineData("docker://alias:tag", true)]
    [InlineData("docker://alias:tag", false)]
    public async Task IdenticalIds_ShareOneSpoolWithIndependentLeases(string target, bool disposeBaseFirst)
    {
        DockerArchiveFixture fixture = DockerArchiveFixture.Create();
        List<FakeEngine> engines = [];
        ImageSourceFactory factory = Factory(() =>
        {
            FakeEngine engine = new(fixture);
            engines.Add(engine);
            return engine;
        });
        ComparisonCommand command = new(factory);

        (ResolvedImage first, ResolvedImage second) = await command.OpenAsync(target, Token);
        await using ResolvedImage baseOwner = first;
        await using ResolvedImage targetOwner = second;

        Assert.Equal(2, engines.Count);
        Assert.Equal(["base:tag", fixture.Id], engines[0].Inspections);
        Assert.Equal([target["docker://".Length..], fixture.Id], engines[1].Inspections);
        Assert.Single(engines.SelectMany(engine => engine.Exports));
        Assert.All(engines, engine => Assert.True(engine.Disposed));
        Assert.Same(first.Content, second.Content);
        Assert.Equal("docker://base:tag", first.Reference.ToString());
        Assert.Equal(target, second.Reference.ToString());
        Assert.Single(Spools());
        using StringWriter report = new();
        second.Report(report);
        Assert.Contains(target, report.ToString());

        ResolvedImage disposed = disposeBaseFirst ? first : second;
        ResolvedImage retained = disposeBaseFirst ? second : first;
        await disposed.DisposeAsync();
        await disposed.DisposeAsync();
        Assert.Single(Spools());
        await AssertReadableAsync(retained, fixture);
        await retained.DisposeAsync();
        Assert.Empty(Spools());
        await retained.DisposeAsync();
    }

    [Fact]
    public async Task ConcurrentAliases_JoinInFlightExportAfterIndependentInspections()
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
        Task<(ResolvedImage Base, ResolvedImage Target)> pending = new ComparisonCommand(factory).OpenAsync("docker://alias", Token);
        try
        {
            Assert.False(pending.IsCompleted);
            Assert.Equal(2, engines.Count);
            Assert.All(engines, engine => Assert.Equal(2, engine.Inner.Inspections.Count));
            Assert.Equal(1, engines.Sum(engine => engine.Saves));
        }
        finally
        {
            release.TrySetResult();
        }

        (ResolvedImage first, ResolvedImage second) = await pending.WaitAsync(TimeSpan.FromSeconds(10), Token);
        await first.DisposeAsync();
        await AssertReadableAsync(second, fixture);
        await second.DisposeAsync();
        Assert.Empty(Spools());
    }

    [Fact]
    public async Task CanceledExportInitiatorStopsWaitingWhileSiblingKeepsSharedArchiveAlive()
    {
        DockerArchiveFixture fixture = DockerArchiveFixture.Create();
        TaskCompletionSource copying = Signal();
        TaskCompletionSource canceled = Signal();
        TaskCompletionSource drain = Signal();
        TaskCompletionSource release = Signal();
        BlockingExport export = new(fixture.Export, copying, canceled, release, drain);
        ControlledEngine firstEngine = new(fixture, _ => Task.FromResult<Stream>(export));
        ControlledEngine secondEngine = new(fixture, _ => throw new InvalidOperationException("The shared export must not run twice."));
        int calls = 0;
        ImageSourceFactory factory = Factory(() => Interlocked.Increment(ref calls) == 1 ? firstEngine : secondEngine);
        using CancellationTokenSource caller = CancellationTokenSource.CreateLinkedTokenSource(Token);

        Task<ResolvedImage> initiating = factory.OpenAsync("docker://base:tag", Options(), caller.Token);
        await copying.Task.WaitAsync(TimeSpan.FromSeconds(10), Token);
        Task<ResolvedImage> sibling = factory.OpenAsync("docker://base:tag", Options(), Token);
        try
        {
            await caller.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                initiating.WaitAsync(TimeSpan.FromSeconds(2), Token));
            Assert.False(sibling.IsCompleted);
            Assert.False(canceled.Task.IsCompleted);
        }
        finally
        {
            release.TrySetResult();
            drain.TrySetResult();
        }

        await using ResolvedImage image = await sibling.WaitAsync(TimeSpan.FromSeconds(10), Token);
        await AssertReadableAsync(image, fixture);
        await image.DisposeAsync();
        Assert.Empty(Spools());
    }

    [Fact]
    public async Task SeparateComparisons_InspectMutableReferencesAgainAndDoNotRetainArchives()
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
        ComparisonCommand command = new(factory);
        (ResolvedImage first, ResolvedImage second) = await command.OpenAsync("docker://base:tag", Token);
        Assert.Equal(original.Id, first.Identity);
        await first.DisposeAsync();
        await second.DisposeAsync();
        Assert.Empty(Spools());
        current = replacement;

        (first, second) = await command.OpenAsync("docker://base:tag", Token);
        Assert.Equal(replacement.Id, first.Identity);
        Assert.Equal(replacement.Id, second.Identity);
        Assert.Equal(4, engines.Count);
        Assert.Equal(2, engines.Sum(engine => engine.Exports.Count));
        await first.DisposeAsync();
        await second.DisposeAsync();
        Assert.Empty(Spools());
    }

    [Fact]
    public async Task DifferentImmutableIds_DoNotShare()
    {
        DockerArchiveFixture firstFixture = DockerArchiveFixture.Create();
        DockerArchiveFixture secondFixture = DockerArchiveFixture.Create(scratch: true);
        int calls = 0;
        List<FakeEngine> engines = [];
        ImageSourceFactory factory = Factory(() =>
        {
            FakeEngine engine = new(calls++ == 0 ? firstFixture : secondFixture);
            engines.Add(engine);
            return engine;
        });
        (ResolvedImage first, ResolvedImage second) = await new ComparisonCommand(factory).OpenAsync("docker://alias", Token);
        await using ResolvedImage baseOwner = first;
        await using ResolvedImage targetOwner = second;

        Assert.NotEqual(first.Identity, second.Identity);
        Assert.NotSame(first.Content, second.Content);
        Assert.Equal(2, engines.Sum(engine => engine.Exports.Count));
        Assert.Equal(2, Spools().Length);
    }

    [Theory]
    [InlineData("daemon")]
    [InlineData("platform")]
    [InlineData("immutable-id")]
    public async Task InvalidSecondInspection_IsNotHiddenBySharedArchive(string mismatch)
    {
        DockerArchiveFixture fixture = DockerArchiveFixture.Create();
        int calls = 0;
        FakeEngine second = new(fixture)
        {
            DaemonId = mismatch == "daemon" ? "changed-daemon" : "daemon",
            Architecture = mismatch == "platform" ? "arm64" : "amd64",
            ImageId = mismatch == "immutable-id" ? "invalid" : fixture.Id
        };
        ImageSourceFactory factory = Factory(() => calls++ == 0 ? new FakeEngine(fixture) : second);
        PlatformOptionsBase options = Options();
        options.Architecture = "amd64";

        await Assert.ThrowsAnyAsync<Exception>(() => new ComparisonCommand(factory)
            .OpenAsync("docker://alias", Token, options));

        Assert.Empty(second.Exports);
        Assert.Equal(mismatch == "daemon" ? 0 : mismatch == "immutable-id" ? 1 : 2, second.Inspections.Count);
        Assert.Empty(Spools());
    }

    [Fact]
    public async Task DifferentPinnedPlatforms_DoNotShareEvenWithoutPlatformFilter()
    {
        DockerArchiveFixture fixture = DockerArchiveFixture.Create();
        FakeEngine second = new(fixture) { Architecture = "arm64" };
        int calls = 0;
        ImageSourceFactory factory = Factory(() => calls++ == 0 ? new FakeEngine(fixture) : second);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            new ComparisonCommand(factory).OpenAsync("docker://alias", Token));

        Assert.Single(second.Exports);
        Assert.Empty(Spools());
    }

    [Fact]
    public async Task ChangedImmutableInspection_IsRejectedBeforeSharing()
    {
        DockerArchiveFixture fixture = DockerArchiveFixture.Create();
        int calls = 0;
        ControlledEngine second = new(fixture, _ => throw new InvalidOperationException("Must not export."),
            reference => new ImageInspectResponse
            {
                ID = reference == fixture.Id ? "sha256:" + new string('a', 64) : fixture.Id,
                Os = "linux",
                Architecture = "amd64"
            });
        ImageSourceFactory factory = Factory(() => calls++ == 0 ? new FakeEngine(fixture) : second);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            new ComparisonCommand(factory).OpenAsync("docker://alias", Token));

        Assert.Equal(2, second.Inner.Inspections.Count);
        Assert.Equal(0, second.Saves);
        Assert.Empty(Spools());
    }

    [Fact]
    public async Task StrongerRequirements_DoNotReuseMetadataOnlyVerification()
    {
        DockerArchiveFixture fixture = DockerArchiveFixture.Create(corruptLayer: true);
        List<FakeEngine> engines = [];
        ImageSourceFactory factory = Factory(() =>
        {
            FakeEngine engine = new(fixture);
            engines.Add(engine);
            return engine;
        });
        await using ImageSourceFactory.ComparisonScope scope = factory.CreateComparisonScope();
        await using ResolvedImage metadata = await scope.OpenAsync(
            "docker://base:tag", Options(), Token, ImageSourceRequirements.Metadata);

        await Assert.ThrowsAsync<InvalidDataException>(() => scope.OpenAsync(
            "docker://alias", Options(), Token, ImageSourceRequirements.LayerContent));

        Assert.Equal(2, engines.Sum(engine => engine.Exports.Count));
        Assert.All(engines, engine => Assert.Equal(2, engine.Inspections.Count));
        Assert.Single(Spools());
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, false, false)]
    [InlineData(true, true, false)]
    [InlineData(false, false, true)]
    [InlineData(false, true, true)]
    [InlineData(true, false, true)]
    [InlineData(true, true, true)]
    public async Task EquivalentContentRequirementsShareAnArchive(bool scoped, bool reverse, bool gzip)
    {
        DockerArchiveFixture fixture = DockerArchiveFixture.Create(oci: gzip, gzip: gzip);
        List<FakeEngine> engines = [];
        ImageSourceFactory factory = Factory(() =>
        {
            FakeEngine engine = new(fixture);
            engines.Add(engine);
            return engine;
        });
        await using ImageSourceFactory.ComparisonScope? scope = scoped ? factory.CreateComparisonScope() : null;
        ImageSourceRequirements firstLevel = reverse ? ImageSourceRequirements.LinuxFileSystem : ImageSourceRequirements.LayerContent;
        ImageSourceRequirements secondLevel = reverse ? ImageSourceRequirements.LayerContent : ImageSourceRequirements.LinuxFileSystem;
        await using ResolvedImage first = scope is null
            ? await factory.OpenAsync("docker://base:tag", Options(), Token, firstLevel)
            : await scope.OpenAsync("docker://base:tag", Options(), Token, firstLevel);
        await using ResolvedImage second = scope is null
            ? await factory.OpenAsync("docker://alias:tag", Options(), Token, secondLevel)
            : await scope.OpenAsync("docker://alias:tag", Options(), Token, secondLevel);

        Assert.Same(first.Content, second.Content);
        Assert.Single(engines.SelectMany(engine => engine.Exports));
        Assert.All(engines, engine => Assert.Equal(2, engine.Inspections.Count));
        Assert.Single(Spools());
        Assert.Equal(reverse, ((DockerImageArchive)first.Content).GetPreparedIndex(
            first.Resolved.Manifest.Layers[0].Digest!, fixture.StoredLayer.Length) is not null);
        await first.DisposeAsync();
        await AssertReadableAsync(second, fixture);
    }

    [Fact]
    public async Task EachOperand_UsesItsRequiredWindowsArchive()
    {
        DockerArchiveFixture fixture = DockerArchiveFixture.Create(oci: true, gzip: true, windows: true);
        List<FakeEngine> engines = [];
        ImageSourceFactory factory = Factory(() =>
        {
            FakeEngine engine = new(fixture) { Os = "windows" };
            engines.Add(engine);
            return engine;
        });
        await using ImageSourceFactory.ComparisonScope scope = factory.CreateComparisonScope();
        await using ResolvedImage metadata = await scope.OpenAsync(
            "docker://base:tag", Options(), Token, ImageSourceRequirements.Metadata);
        await using ResolvedImage filesystem = await scope.OpenAsync(
            "docker://alias", Options(), Token, ImageSourceRequirements.LinuxFileSystem);

        Assert.Equal(2, engines.Count);
        Assert.Equal(2, engines[1].Inspections.Count);
        Assert.Single(engines[0].Exports);
        Assert.Single(engines[1].Exports);
        Assert.Equal(2, Spools().Length);
        await AssertReadableAsync(filesystem, fixture);
    }

    [Fact]
    public async Task FailedExport_CanBeRetriedWithinScopeWithoutDeadlock()
    {
        DockerArchiveFixture fixture = DockerArchiveFixture.Create();
        int calls = 0;
        ImageSourceFactory factory = Factory(() => new FakeEngine(fixture) { FailExport = calls++ == 0 });
        await using (ImageSourceFactory.ComparisonScope scope = factory.CreateComparisonScope())
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                scope.OpenAsync("docker://base:tag", Options(), Token).WaitAsync(TimeSpan.FromSeconds(10), Token));
            Assert.Empty(Spools());
            await using ResolvedImage result = await scope.OpenAsync("docker://base:tag", Options(), Token);
            await AssertReadableAsync(result, fixture);
        }
        Assert.Equal(2, calls);
        Assert.Empty(Spools());
    }

    [Fact]
    public async Task ScopeDisposal_ReleasesEveryOwnerAndAggregatesCleanupFailures()
    {
        DockerArchiveFixture fixture = DockerArchiveFixture.Create();
        ImageSourceFactory factory = Factory(() => new FakeEngine(fixture));
        await using ImageSourceFactory.ComparisonScope scope = factory.CreateComparisonScope();
        List<string> snapshots = [];
        foreach (ImageSourceRequirements requirements in Enum.GetValues<ImageSourceRequirements>())
        {
            ResolvedImage image = await scope.OpenAsync("docker://base:tag", Options(), Token, requirements);
            await image.DisposeAsync();
            snapshots = [.. Directory.GetDirectories(directory)];
        }
        Assert.Equal(2, snapshots.Count);
        Directory.Delete(snapshots[0], recursive: true);
        Directory.Delete(snapshots[1], recursive: true);

        AggregateException error = await Assert.ThrowsAsync<AggregateException>(() => scope.DisposeAsync().AsTask());

        Assert.Equal(2, error.InnerExceptions.Count);
        Assert.All(error.InnerExceptions, exception => Assert.IsType<DirectoryNotFoundException>(exception));
        Assert.Empty(Directory.GetFileSystemEntries(directory));
        await scope.DisposeAsync();
    }

    [Theory]
    [InlineData("docker://base:tag")]
    [InlineData("registry://registry.test/app:tag")]
    public async Task DisposedScope_RejectsNewAcquisitionsBeforeAccessingSources(string reference)
    {
        int engines = 0;
        ImageSourceFactory factory = Factory(() =>
        {
            engines++;
            return new FakeEngine(DockerArchiveFixture.Create());
        });
        ImageSourceFactory.ComparisonScope scope = factory.CreateComparisonScope();
        await scope.DisposeAsync();
        await scope.DisposeAsync();

        await Assert.ThrowsAsync<ObjectDisposedException>(() => scope.OpenAsync(reference, Options(), Token));

        Assert.Equal(0, engines);
        Assert.Empty(Spools());
    }

    [Theory]
    [InlineData(1)]
    [InlineData(8)]
    [InlineData(64)]
    public async Task ScopeDisposal_DrainsPendingExportWithoutIssuingLeaseAfterDisposal(int acquisitions)
    {
        DockerArchiveFixture fixture = DockerArchiveFixture.Create();
        TaskCompletionSource release = Signal();
        bool pause = false;
        ImageSourceFactory factory = Factory(() => new ControlledEngine(fixture, async token =>
        {
            if (pause)
            {
                await release.Task.WaitAsync(token);
            }
            return new MemoryStream(fixture.Export);
        }));
        await using ImageSourceFactory.ComparisonScope scope = factory.CreateComparisonScope();
        await using (ResolvedImage first = await scope.OpenAsync(
            "docker://base:tag", Options(), Token, ImageSourceRequirements.Metadata)) { }
        pause = true;
        Task<ResolvedImage>[] pending = [.. Enumerable.Range(0, acquisitions)
            .Select(index => scope.OpenAsync($"docker://alias:{index}", Options(), Token))];
        Task disposing = scope.DisposeAsync().AsTask();
        try
        {
            Assert.All(pending, operation => Assert.False(operation.IsCompleted));
            Assert.False(disposing.IsCompleted);
        }
        finally
        {
            release.TrySetResult();
        }

        await Task.WhenAll(pending.Select(operation => Assert.ThrowsAsync<ObjectDisposedException>(() =>
            operation.WaitAsync(TimeSpan.FromSeconds(10), Token))));
        await disposing.WaitAsync(TimeSpan.FromSeconds(10), Token);
        Assert.Empty(Spools());
    }

    [Fact]
    public async Task ComparisonCleanupFailure_IsReportedWithoutMaskingAcquisitionFailure()
    {
        DockerArchiveFixture fixture = DockerArchiveFixture.Create();
        IOException expected = new("The second acquisition failed.");
        TaskCompletionSource firstDisposed = Signal();
        ControlledEngine first = new(fixture, _ => Task.FromResult<Stream>(new MemoryStream(fixture.Export)),
            onDispose: () => firstDisposed.TrySetResult());
        ControlledEngine second = new(fixture, _ => throw new InvalidOperationException("Must not export."),
            reference =>
            {
                if (reference == fixture.Id)
                {
                    Directory.Delete(Assert.Single(Directory.GetDirectories(directory)), recursive: true);
                    throw expected;
                }
                return new ImageInspectResponse { ID = fixture.Id, Os = "linux", Architecture = "amd64" };
            }, beforeInspect: () => firstDisposed.Task.WaitAsync(TimeSpan.FromSeconds(10), Token));
        int calls = 0;
        ImageSourceFactory factory = Factory(() => calls++ == 0 ? first : second);
        using StringWriter errors = new();
        ComparisonCommand command = new(factory, errors);

        IOException actual = await Assert.ThrowsAsync<IOException>(() => command.OpenAsync("docker://alias", Token));

        Assert.Same(expected, actual);
        Assert.Contains("Failed to dispose a comparison image after acquisition failed:", errors.ToString());
        Assert.True(second.Inner.Disposed);
        Assert.Empty(Spools());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SharedAcquisitionFailure_DrainsExportAndDeletesSpool(bool cancel)
    {
        DockerArchiveFixture fixture = DockerArchiveFixture.Create();
        TaskCompletionSource copying = Signal();
        TaskCompletionSource canceled = Signal();
        TaskCompletionSource drain = Signal();
        TaskCompletionSource release = Signal();
        using CancellationTokenSource caller = CancellationTokenSource.CreateLinkedTokenSource(Token);
        BlockingExport export = new(cancel ? fixture.Export : new byte[1024], copying, canceled, release, drain);
        List<ControlledEngine> engines = [];
        ImageSourceFactory factory = Factory(() =>
        {
            ControlledEngine engine = new(fixture, _ => Task.FromResult<Stream>(export));
            engines.Add(engine);
            return engine;
        });
        Task<(ResolvedImage Base, ResolvedImage Target)> pending =
            new ComparisonCommand(factory).OpenAsync("docker://alias", caller.Token);
        try
        {
            await copying.Task.WaitAsync(TimeSpan.FromSeconds(10), Token);
            Assert.Equal(2, engines.Count);
            Assert.All(engines, engine => Assert.Equal(2, engine.Inner.Inspections.Count));
            Assert.Equal(1, engines.Sum(engine => engine.Saves));
            Assert.Single(Spools());
            if (cancel)
            {
                await caller.CancelAsync();
                await canceled.Task.WaitAsync(TimeSpan.FromSeconds(10), Token);
                Assert.False(pending.IsCompleted);
            }
        }
        finally
        {
            release.TrySetResult();
            drain.TrySetResult();
        }

        if (cancel)
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                pending.WaitAsync(TimeSpan.FromSeconds(10), Token));
        }
        else
        {
            await Assert.ThrowsAsync<InvalidDataException>(() => pending.WaitAsync(TimeSpan.FromSeconds(10), Token));
        }
        await Task.WhenAll(engines.Select(engine => engine.DisposedTask)).WaitAsync(TimeSpan.FromSeconds(10), Token);
        Assert.True(export.Disposed);
        Assert.All(engines, engine => Assert.True(engine.Inner.Disposed));
        Assert.Empty(Spools());

        ImageSourceFactory retry = Factory(() => new FakeEngine(fixture));
        (ResolvedImage first, ResolvedImage second) = await new ComparisonCommand(retry).OpenAsync("docker://alias", Token);
        await first.DisposeAsync();
        await AssertReadableAsync(second, fixture);
        await second.DisposeAsync();
        Assert.Empty(Spools());
    }

    private ImageSourceFactory Factory(Func<IDockerImageEngine> engine) =>
        new(Mock.Of<IDockerRegistryClientFactory>(), new TestAppSettingsStore(new AppSettings(), settingsPath: ""),
            _ => engine(), directory, indexStoreFactory: () => new LayerStore(cache));

    private string[] Spools()
    {
        if (!Directory.Exists(directory))
        {
            return [];
        }
        string[] spools = Directory.GetFiles(directory, "export.tar", SearchOption.AllDirectories);
        Assert.Equal(Directory.GetDirectories(directory).Length, spools.Length);
        return spools;
    }

    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private static PlatformOptionsBase Options() => new() { DockerHost = "http://localhost:2375" };
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static async Task AssertReadableAsync(ResolvedImage image, DockerArchiveFixture fixture)
    {
        string digest = image.Resolved.Manifest.Layers[0].Digest!;
        BlobDownloadResult result = await image.Content.GetRangeAsync(digest, 0, null, Token);
        await using Stream content = result.Content;
        using MemoryStream buffer = new();
        await content.CopyToAsync(buffer, Token);
        Assert.Equal(fixture.StoredLayer, buffer.ToArray());
    }

    public void Dispose()
    {
        if (Directory.Exists(cache))
        {
            Directory.Delete(cache, recursive: true);
        }
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private sealed class ComparisonCommand : RegistryCommandBase<PlatformOptionsBase>
    {
        private readonly TextWriter errors;

        internal ComparisonCommand(IImageSourceFactory factory, TextWriter? errors = null)
            : base("test", "shared daemon acquisition", Mock.Of<IDockerRegistryClientFactory>())
        {
            ImageSourceFactory = factory;
            this.errors = errors ?? TextWriter.Null;
        }

        internal Task<(ResolvedImage Base, ResolvedImage Target)> OpenAsync(
            string target, CancellationToken token, PlatformOptionsBase? options = null) =>
            OpenComparisonAsync("docker://base:tag", target, options ?? SharedDaemonAcquisitionTests.Options(), token);

        protected override TextWriter Error => errors;
        protected override Task ExecuteAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class ControlledEngine(DockerArchiveFixture fixture, Func<CancellationToken, Task<Stream>> save,
        Func<string, ImageInspectResponse>? inspect = null, Func<Task>? beforeInspect = null,
        Action? onDispose = null) : IDockerImageEngine
    {
        internal FakeEngine Inner { get; } = new(fixture);
        internal int Saves { get; private set; }
        private readonly TaskCompletionSource disposed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal Task DisposedTask => disposed.Task;
        public Task<string> GetDaemonIdentityAsync(CancellationToken cancellationToken) => Inner.GetDaemonIdentityAsync(cancellationToken);
        public async Task<ImageInspectResponse> InspectAsync(string reference, CancellationToken cancellationToken)
        {
            if (beforeInspect is not null)
            {
                await beforeInspect();
            }
            ImageInspectResponse result = await Inner.InspectAsync(reference, cancellationToken);
            return inspect is null ? result : inspect(reference);
        }
        public Task<Stream> SaveAsync(string imageId, CancellationToken cancellationToken)
        {
            Saves++;
            return save(cancellationToken);
        }
        public void Dispose()
        {
            Inner.Dispose();
            onDispose?.Invoke();
            disposed.TrySetResult();
        }
    }

    private sealed class BlockingExport(byte[] bytes, TaskCompletionSource copying, TaskCompletionSource canceled,
        TaskCompletionSource release, TaskCompletionSource drain) : MemoryStream(bytes)
    {
        internal bool Disposed { get; private set; }

        public override async Task CopyToAsync(Stream destination, int bufferSize, CancellationToken cancellationToken)
        {
            copying.TrySetResult();
            try
            {
                await release.Task.WaitAsync(cancellationToken);
                await base.CopyToAsync(destination, bufferSize, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                canceled.TrySetResult();
                await drain.Task;
                throw;
            }
        }

        protected override void Dispose(bool disposing)
        {
            Disposed = true;
            base.Dispose(disposing);
        }
    }
}
