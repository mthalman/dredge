using System.CommandLine;
using Valleysoft.Dredge.Commands;

namespace Valleysoft.Dredge.Tests;

public class CommandCancellationTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(5);

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

    public sealed class TestOptions : OptionsBase
    {
        protected override void GetValues()
        {
        }
    }
}
