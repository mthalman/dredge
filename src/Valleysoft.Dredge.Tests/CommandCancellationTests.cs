using System.CommandLine;
using Valleysoft.Dredge.Commands;

namespace Valleysoft.Dredge.Tests;

public class CommandCancellationTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(5);

    [Fact]
    public void RootInvocationWritesConciseErrorAndReturnsFailure()
    {
        FailureCommand command = new();
        using StringWriter error = new();

        int exitCode = CommandHelper.InvokeRootCommand(
            command.Parse([]),
            new InvocationConfiguration { Error = error });

        Assert.Equal(1, exitCode);
        Assert.Equal($"failure{Environment.NewLine}", error.ToString());
    }

    [Fact]
    public void CancellationAtRootReturnsFailureWithoutWritingError()
    {
        CancellationCommand command = new();
        using StringWriter error = new();

        int exitCode = CommandHelper.InvokeRootCommand(
            command.Parse([]),
            new InvocationConfiguration { Error = error });

        Assert.Equal(1, exitCode);
        Assert.Empty(error.ToString());
    }

    [Fact]
    public async Task CancellationPropagatesFromCommandHelper()
    {
        using CancellationTokenSource cancellationTokenSource = new();
        cancellationTokenSource.Cancel();
        bool executed = false;
        using StringWriter error = new();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            CommandHelper.ExecuteCommandAsync(
                registry: null,
                cancellationTokenSource.Token,
                ct =>
                {
                    executed = true;
                    ct.ThrowIfCancellationRequested();
                    return Task.CompletedTask;
                },
                error));

        Assert.True(executed);
        Assert.Empty(error.ToString());
    }

    [Fact]
    public async Task CancellationFromCommandIsFailureWhenInvocationTokenIsNotCanceled()
    {
        using StringWriter error = new();
        int? exitCode = null;

        await CommandHelper.ExecuteCommandAsync(
            registry: null,
            CancellationToken.None,
            ct => throw new OperationCanceledException("failure"),
            error,
            code => exitCode = code);

        Assert.Equal(1, exitCode);
        Assert.Equal($"failure{Environment.NewLine}", error.ToString());
    }

    [Fact]
    public async Task InvocationTokenIsPassedToCommand()
    {
        using CancellationTokenSource cancellationTokenSource = new();
        TestCommand command = new();

        Task<int> invocation = command
            .Parse([])
            .InvokeAsync(new InvocationConfiguration(), cancellationTokenSource.Token);

        await command.Started.Task.WaitAsync(TestTimeout, TestContext.Current.CancellationToken);
        cancellationTokenSource.Cancel();

        int exitCode = await invocation.WaitAsync(TestTimeout, TestContext.Current.CancellationToken);

        Assert.NotEqual(0, exitCode);
        Assert.True(command.CancellationToken.IsCancellationRequested);
    }

    private sealed class TestCommand : CommandWithOptions<TestOptions>
    {
        public TestCommand()
            : base("test", "Test command")
        {
        }

        public CancellationToken CancellationToken { get; private set; }
        public TaskCompletionSource Started { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override async Task ExecuteAsync(CancellationToken cancellationToken)
        {
            CancellationToken = cancellationToken;
            Started.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }
    }

    private sealed class FailureCommand : CommandWithOptions<TestOptions>
    {
        public FailureCommand()
            : base("failure", "Failure command")
        {
        }

        protected override Task ExecuteAsync(CancellationToken cancellationToken) =>
            throw new InvalidOperationException("failure");
    }

    private sealed class CancellationCommand : CommandWithOptions<TestOptions>
    {
        public CancellationCommand()
            : base("cancel", "Cancellation command")
        {
        }

        protected override Task ExecuteAsync(CancellationToken cancellationToken) =>
            throw new OperationCanceledException();
    }

    public sealed class TestOptions : OptionsBase
    {
        protected override void GetValues()
        {
        }
    }
}
