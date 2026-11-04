using Valleysoft.DockerRegistryClient.Models.Images;
using Valleysoft.DockerRegistryClient.Models.Manifests;
using Valleysoft.DockerRegistryClient.Models.Manifests.Docker;
using Valleysoft.Dredge.Commands;

namespace Valleysoft.Dredge.Tests;

public class ComparisonAcquisitionTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Failure_CancelsAndDrainsSiblingBeforeRethrowing(bool failBase)
    {
        using CancellationTokenSource timeout = TimeoutToken();
        TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource canceled = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource drained = new(TaskCreationOptions.RunContinuationsAsynchronously);
        IOException expected = new("The pinned image disappeared.");
        Sources sources = new(async (reference, token) =>
        {
            if ((reference == "base") == failBase)
            {
                await started.Task.WaitAsync(token);
                throw expected;
            }
            started.SetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                throw new InvalidOperationException("The sibling was not canceled.");
            }
            finally
            {
                canceled.TrySetResult();
                await drained.Task;
            }
        });
        ComparisonCommand command = new(sources);
        Task<(ResolvedImage Base, ResolvedImage Target)> comparison = command.OpenAsync(timeout.Token);
        try
        {
            await canceled.Task.WaitAsync(timeout.Token);
            Assert.False(comparison.IsCompleted);
        }
        finally
        {
            drained.TrySetResult();
        }

        Assert.Same(expected, await Assert.ThrowsAsync<IOException>(() => comparison));
        Assert.Equal(2, sources.Tokens.Count);
        Assert.All(sources.Tokens, token => Assert.True(token.IsCancellationRequested));
        Assert.False(timeout.IsCancellationRequested);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Failure_DisposesAlreadyAcquiredSibling(bool failBase)
    {
        using CancellationTokenSource timeout = TimeoutToken();
        TaskCompletionSource<ResolvedImage> failure = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Owner owner = new();
        IOException expected = new("Acquisition failed.");
        Sources sources = new((reference, _) => (reference == "base") == failBase
            ? failure.Task : Task.FromResult(Image(owner)));
        ComparisonCommand command = new(sources);
        Task<(ResolvedImage Base, ResolvedImage Target)> comparison = command.OpenAsync(timeout.Token);
        failure.SetException(expected);

        Assert.Same(expected, await Assert.ThrowsAsync<IOException>(() => comparison));
        Assert.Equal(1, owner.Disposals);
    }

    [Fact]
    public async Task SuccessfulComparison_TransfersOwnershipToCaller()
    {
        using CancellationTokenSource timeout = TimeoutToken();
        Owner owner = new();
        ComparisonCommand command = new(new Sources((_, _) => Task.FromResult(Image(owner))));

        (ResolvedImage first, ResolvedImage second) = await command.OpenAsync(timeout.Token);

        Assert.Equal(0, owner.Disposals);
        await first.DisposeAsync();
        await second.DisposeAsync();
        Assert.Equal(2, owner.Disposals);
    }

    [Fact]
    public async Task CallerCancellation_DrainsBothAcquisitions()
    {
        using CancellationTokenSource timeout = TimeoutToken();
        using CancellationTokenSource caller = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
        int started = 0;
        int drained = 0;
        Sources sources = new(async (_, token) =>
        {
            Interlocked.Increment(ref started);
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                throw new InvalidOperationException("Acquisition must be canceled.");
            }
            finally
            {
                Interlocked.Increment(ref drained);
            }
        });
        ComparisonCommand command = new(sources);
        Task<(ResolvedImage Base, ResolvedImage Target)> comparison = command.OpenAsync(caller.Token);
        Assert.Equal(2, started);
        await caller.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => comparison);
        Assert.Equal(2, drained);
    }

    [Fact]
    public async Task SynchronousFailure_DoesNotStartSiblingOrMaskOriginalError()
    {
        IOException expected = new("Immediate acquisition failure.");
        int calls = 0;
        Sources sources = new((_, _) =>
        {
            calls++;
            throw expected;
        });

        Assert.Same(expected, await Assert.ThrowsAsync<IOException>(
            () => new ComparisonCommand(sources).OpenAsync(TestContext.Current.CancellationToken)));
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task CleanupFailure_IsReportedWithoutMaskingAcquisitionFailure()
    {
        using CancellationTokenSource timeout = TimeoutToken();
        TaskCompletionSource<ResolvedImage> failure = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Owner owner = new() { ThrowOnDispose = true };
        IOException expected = new("Acquisition failed.");
        Sources sources = new((reference, _) =>
            reference == "base" ? failure.Task : Task.FromResult(Image(owner)));
        using StringWriter errors = new();
        ComparisonCommand command = new(sources, errors);
        Task<(ResolvedImage Base, ResolvedImage Target)> comparison = command.OpenAsync(timeout.Token);
        failure.SetException(expected);

        Assert.Same(expected, await Assert.ThrowsAsync<IOException>(() => comparison));
        Assert.Equal(1, owner.Disposals);
        Assert.Contains("Disposal failed", errors.ToString());
    }

    [Fact]
    public async Task LateSuccessfulSibling_IsDrainedAndDisposed()
    {
        using CancellationTokenSource timeout = TimeoutToken();
        TaskCompletionSource<ResolvedImage> failure = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource<ResolvedImage> sibling = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource canceled = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Owner owner = new();
        IOException expected = new("Acquisition failed.");
        Sources sources = new((reference, _) => reference == "base" ? failure.Task : sibling.Task);
        Task<(ResolvedImage Base, ResolvedImage Target)> comparison = new ComparisonCommand(sources).OpenAsync(timeout.Token);
        using CancellationTokenRegistration registration = sources.Tokens[1].Register(() => canceled.TrySetResult());
        failure.SetException(expected);
        try
        {
            await canceled.Task.WaitAsync(timeout.Token);
            Assert.False(comparison.IsCompleted);
        }
        finally
        {
            sibling.TrySetResult(Image(owner));
        }

        Assert.Same(expected, await Assert.ThrowsAsync<IOException>(() => comparison));
        Assert.Equal(1, owner.Disposals);
    }

    [Fact]
    public async Task CancellationCallbackFailure_IsReportedWithoutMaskingAcquisitionFailure()
    {
        using CancellationTokenSource timeout = TimeoutToken();
        TaskCompletionSource<ResolvedImage> failure = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Owner owner = new();
        IOException expected = new("Acquisition failed.");
        Sources sources = new((reference, _) => reference == "base" ? failure.Task : Task.FromResult(Image(owner)));
        using StringWriter errors = new();
        Task<(ResolvedImage Base, ResolvedImage Target)> comparison =
            new ComparisonCommand(sources, errors).OpenAsync(timeout.Token);
        using CancellationTokenRegistration registration = sources.Tokens[0].Register(
            () => throw new IOException("Cancellation callback failed."));
        failure.SetException(expected);

        Assert.Same(expected, await Assert.ThrowsAsync<IOException>(() => comparison));
        Assert.Contains("Cancellation callback failed", errors.ToString());
        Assert.Equal(1, owner.Disposals);
    }

    private static CancellationTokenSource TimeoutToken()
    {
        CancellationTokenSource token =
            CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        token.CancelAfter(TimeSpan.FromSeconds(10));
        return token;
    }

    private static ResolvedImage Image(Owner owner)
    {
        ImageSourceReference reference = ImageSourceReference.Parse("docker://test:dev");
        DockerManifest manifest = new();
        ManifestInfo info = new("application/vnd.docker.distribution.manifest.v2+json", "manifest", manifest);
        return new(reference, reference.GetImageName(), new(info, manifest), null,
            new Image { Os = "linux", Architecture = "amd64" }, [], new UnusedContent(),
            "identity", "linux/amd64", owner);
    }

    private sealed class ComparisonCommand : RegistryCommandBase<PlatformOptionsBase>
    {
        private readonly TextWriter errors;

        public ComparisonCommand(IImageSourceFactory sources, TextWriter? errors = null)
            : base("test", "test comparison acquisition", Mock.Of<IDockerRegistryClientFactory>())
        {
            ImageSourceFactory = sources;
            this.errors = errors ?? TextWriter.Null;
        }

        public Task<(ResolvedImage Base, ResolvedImage Target)> OpenAsync(CancellationToken token) =>
            OpenComparisonAsync("base", "target", new PlatformOptionsBase(), token);

        protected override TextWriter Error => errors;
        protected override Task ExecuteAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class Sources(Func<string, CancellationToken, Task<ResolvedImage>> open) : IImageSourceFactory
    {
        public List<CancellationToken> Tokens { get; } = [];

        public Task<ResolvedImage> OpenAsync(
            string reference, PlatformOptionsBase options, CancellationToken cancellationToken,
            ImageSourceRequirements requirements = ImageSourceRequirements.LayerContent)
        {
            Tokens.Add(cancellationToken);
            return open(reference, cancellationToken);
        }
    }

    private sealed class Owner : IAsyncDisposable
    {
        public int Disposals { get; private set; }
        public bool ThrowOnDispose { get; init; }

        public ValueTask DisposeAsync()
        {
            Disposals++;
            return ThrowOnDispose
                ? ValueTask.FromException(new IOException("Disposal failed."))
                : ValueTask.CompletedTask;
        }
    }

    private sealed class UnusedContent : IImageContentSource
    {
        public Task<Valleysoft.DockerRegistryClient.BlobDownloadResult> GetRangeAsync(
            string digest, long start, long? length, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Acquisition tests must not read layer content.");
    }
}
