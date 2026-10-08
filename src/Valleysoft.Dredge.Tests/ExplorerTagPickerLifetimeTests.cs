using System.Collections.Concurrent;
using Valleysoft.Dredge.Explorer;
using Valleysoft.Dredge.Explorer.Tui;

namespace Valleysoft.Dredge.Tests;

[Collection(ExplorerUiCollection.Name)]
public sealed class ExplorerTagPickerLifetimeTests
{
    private static CancellationToken TestToken => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ClosingWaitsForDelayedListingAndDoesNotStartDescriptions()
    {
        TaskCompletionSource<bool> entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource<IReadOnlyList<string>> release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken observed = default;
        int described = 0;
        ConcurrentQueue<Action> queued = new();
        FakeExplorerHost host = new()
        {
            ListTagsWork = token => { observed = token; entered.SetResult(true); return release.Task; },
            DescribeTagWork = (_, _) => { Interlocked.Increment(ref described); return Task.CompletedTask; },
        };
        TagPicker.Loading loading = new(host, ExplorerSamples.Image(), TestToken, queued.Enqueue, (_, _) => { }, () => { });
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10), TestToken);

        Task closing = loading.StopAsync();
        Assert.False(closing.IsCompleted);
        Assert.True(observed.IsCancellationRequested);
        release.SetResult(["late"]);
        await closing.WaitAsync(TimeSpan.FromSeconds(10), TestToken);
        Assert.Equal(0, described);
        Assert.Empty(queued);
    }

    [Fact]
    public async Task ClosingDrainsActiveDescriptionsBeforeDisposingTheirSemaphore()
    {
        TaskCompletionSource<bool> entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource<bool> release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int active = 0, finished = 0, startedAfterCancellation = 0;
        FakeExplorerHost host = new()
        {
            Tags = ["one", "two", "three", "four", "five"],
            DescribeTagWork = async (_, token) =>
            {
                if (token.IsCancellationRequested)
                {
                    Interlocked.Increment(ref startedAfterCancellation);
                }
                if (Interlocked.Increment(ref active) == 4)
                {
                    entered.SetResult(true);
                }
                await release.Task.ConfigureAwait(false);
                Interlocked.Increment(ref finished);
            },
        };
        TagPicker.Loading loading = new(host, ExplorerSamples.Image(), TestToken, _ => { }, (_, _) => { }, () => { });
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10), TestToken);

        Task closing = loading.StopAsync();
        Assert.False(closing.IsCompleted);
        release.SetResult(true);
        await closing.WaitAsync(TimeSpan.FromSeconds(10), TestToken);
        Assert.Equal(4, active);
        Assert.Equal(4, finished);
        Assert.Equal(0, startedAfterCancellation);
    }

    [Fact]
    public async Task ClosingRejectsCallbacksBeforeEnqueueAndAtDispatch()
    {
        TaskCompletionSource<bool> queuedFill = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource<bool> release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        ConcurrentQueue<Action> queued = new();
        int delivered = 0;
        FakeExplorerHost host = new() { Tags = ["one"], DescribeTagWork = (_, _) => release.Task };
        TagPicker.Loading loading = new(host, ExplorerSamples.Image(), TestToken,
            action => { queued.Enqueue(action); queuedFill.TrySetResult(true); },
            (_, _) => delivered++, () => delivered++);
        await queuedFill.Task.WaitAsync(TimeSpan.FromSeconds(10), TestToken);
        loading.Post(() => delivered++);

        Task closing = loading.StopAsync();
        int count = queued.Count;
        loading.Post(() => delivered++);
        Assert.Equal(count, queued.Count);
        release.SetResult(true);
        await closing.WaitAsync(TimeSpan.FromSeconds(10), TestToken);
        while (queued.TryDequeue(out Action? action))
        {
            action();
        }
        Assert.Equal(0, delivered);
    }

    [Fact]
    public async Task UnexpectedLateFaultIsObservedDuringClose()
    {
        TaskCompletionSource<bool> entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource<IReadOnlyList<string>> release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        FakeExplorerHost host = new()
        {
            ListTagsWork = _ => { entered.SetResult(true); return release.Task; },
        };
        TagPicker.Loading loading = new(host, ExplorerSamples.Image(), TestToken, _ => { }, (_, _) => { }, () => { });
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10), TestToken);
        Task closing = loading.StopAsync();
        release.SetException(new InvalidOperationException("late listing failure"));
        InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(() => closing);
        Assert.Equal("late listing failure", error.Message);
    }

    [Fact]
    public async Task DescriptionFailureRetainsItsDiagnostic()
    {
        ConcurrentQueue<Action> queued = new();
        TaskCompletionSource<bool> redrawn = new(TaskCreationOptions.RunContinuationsAsynchronously);
        IReadOnlyList<TagChoice>? choices = null;
        FakeExplorerHost host = new()
        {
            Tags = ["broken"],
            DescribeTagWork = (_, _) => Task.FromException(new InvalidOperationException("manifest unavailable")),
        };
        TagPicker.Loading loading = new(host, ExplorerSamples.Image(), TestToken, action =>
        {
            queued.Enqueue(action);
            if (queued.Count == 2)
            {
                redrawn.SetResult(true);
            }
        }, (items, _) => choices = items, () => { });
        await redrawn.Task.WaitAsync(TimeSpan.FromSeconds(10), TestToken);
        while (queued.TryDequeue(out Action? action))
        {
            action();
        }
        TagChoice choice = Assert.Single(choices!);
        Assert.True(choice.Failed);
        Assert.Contains("manifest unavailable", choice.Note);
        await loading.StopAsync();
    }

    [Fact]
    public void OwnerCancellationClosesModalAndDrainsLoading()
    {
        using CancellationTokenSource lifetime = new();
        TaskCompletionSource<bool> entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        bool finished = false;
        using ExplorerUiHarness ui = ExplorerWindowTests.Open(ExplorerSamples.Image(), new ExplorerState(),
            session => new FakeExplorerHost
            {
                Baseline = session,
                ListTagsWork = async token =>
                {
                    entered.SetResult(true);
                    try
                    {
                        await Task.Delay(Timeout.InfiniteTimeSpan, token).ConfigureAwait(false);
                        return [];
                    }
                    finally
                    {
                        finished = true;
                    }
                },
            }, out FakeExplorerHost host);
        Assert.True(ui.InDialog(() =>
            Assert.Throws<OperationCanceledException>(() =>
                TagPicker.Show(ui.App, host, ui.Window.Presenter.Image, null, lifetime.Token)),
            DialogStep.When("listing to start", () => entered.Task.IsCompleted, lifetime.Cancel)));
        Assert.True(finished);
    }
}
