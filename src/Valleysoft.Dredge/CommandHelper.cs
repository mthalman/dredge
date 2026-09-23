using System.CommandLine;
using System.Globalization;
using Valleysoft.DockerRegistryClient;
using Valleysoft.DockerRegistryClient.Models;

namespace Valleysoft.Dredge;

internal static class CommandHelper
{
    public static int InvokeRootCommand(
        ParseResult parseResult,
        InvocationConfiguration? configuration = null)
    {
        configuration ??= new InvocationConfiguration();
        configuration.EnableDefaultExceptionHandler = false;

        try
        {
            return parseResult.Invoke(configuration);
        }
        catch (OperationCanceledException e) when (e.CancellationToken.IsCancellationRequested)
        {
            return 1;
        }
        catch (Exception e)
        {
            WriteError(e, registry: null, configuration.Error);
            return 1;
        }
    }

    public static async Task ExecuteCommandAsync(
        string? registry,
        CancellationToken cancellationToken,
        Func<CancellationToken, Task> execute,
        TextWriter? errorWriter = null,
        Action<int>? exit = null,
        TimeSpan? operationTimeout = null)
    {
        TimeSpan timeout = operationTimeout ?? GetOperationTimeout();
        using CancellationTokenSource? timeoutCancellationSource = timeout == Timeout.InfiniteTimeSpan || timeout <= TimeSpan.Zero
            ? null
            : CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        if (timeoutCancellationSource is not null)
        {
            timeoutCancellationSource.CancelAfter(timeout);
        }

        CancellationToken operationCancellationToken = timeoutCancellationSource?.Token ?? cancellationToken;

        try
        {
            await execute(operationCancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException) when (
            timeoutCancellationSource is not null &&
            timeoutCancellationSource.IsCancellationRequested &&
            !cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"The operation timed out after {GetOperationTimeoutDescription(timeout)}.");
        }
        catch (Exception e)
        {
            WriteError(e, registry, errorWriter);
            (exit ?? Environment.Exit)(1);
        }
    }

    private static TimeSpan GetOperationTimeout()
    {
        TimeSpan? timeout = AppSettings.Load().Operations.GetTimeout();
        return timeout ?? Timeout.InfiniteTimeSpan;
    }

    private static string GetOperationTimeoutDescription(TimeSpan timeout)
    {
        if (timeout == Timeout.InfiniteTimeSpan || timeout <= TimeSpan.Zero)
        {
            return "the configured timeout";
        }

        return timeout switch
        {
            { TotalDays: >= 1 } => $"{timeout.TotalDays.ToString("0.###", CultureInfo.InvariantCulture)} day(s)",
            { TotalHours: >= 1 } => $"{timeout.TotalHours.ToString("0.###", CultureInfo.InvariantCulture)} hour(s)",
            { TotalMinutes: >= 1 } => $"{timeout.TotalMinutes.ToString("0.###", CultureInfo.InvariantCulture)} minute(s)",
            _ => $"{timeout.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture)} second(s)"
        };
    }

    private static void WriteError(Exception e, string? registry, TextWriter? errorWriter)
    {
        ConsoleColor savedColor = Console.ForegroundColor;
        try
        {
            Console.ForegroundColor = ConsoleColor.Red;

            string message = e.Message;
            if (e is RegistryException dockerRegistryException)
            {
                Error? error = dockerRegistryException.Errors.FirstOrDefault();
                if (error?.Code == "UNAUTHORIZED")
                {
                    string loginCommand = "docker login";
                    if (registry is not null)
                    {
                        loginCommand += $" {registry}";
                    }

                    message = $"The repository does not exist or may require authentication. If authentication is required, ensure that your credentials are stored for the registry by running '{loginCommand}'.";
                }
                else
                {
                    message = error?.Message ?? message;
                }
            }

            (errorWriter ?? Console.Error).WriteLine(message);
        }
        finally
        {
            Console.ForegroundColor = savedColor;
        }
    }
}
