using Spectre.Console;

namespace Valleysoft.Dredge.Commands;

internal sealed class DeleteConfirmation
{
    private readonly Func<bool> isInputRedirected;
    private readonly Func<string, TextWriter, CancellationToken, Task<bool>> prompt;

    public DeleteConfirmation()
        : this(() => Console.IsInputRedirected, PromptAsync)
    {
    }

    internal DeleteConfirmation(
        Func<bool> isInputRedirected,
        Func<string, TextWriter, CancellationToken, Task<bool>> prompt)
    {
        this.isInputRedirected = isInputRedirected;
        this.prompt = prompt;
    }

    public void EnsureAvailable(bool yes)
    {
        if (!yes && isInputRedirected())
        {
            throw new InvalidOperationException(
                "Deletion requires confirmation. Pass '--yes' when standard input is redirected.");
        }
    }

    public async Task ConfirmAsync(
        string message,
        bool yes,
        TextWriter error,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        bool confirmed = yes || await prompt(message, error, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (!confirmed)
        {
            throw new InvalidOperationException("Deletion canceled.");
        }
    }

    private static Task<bool> PromptAsync(
        string message,
        TextWriter error,
        CancellationToken cancellationToken)
    {
        IAnsiConsole console = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Out = new AnsiConsoleOutput(error),
            Interactive = InteractionSupport.Yes
        });
        return PromptAsync(message, console, cancellationToken);
    }

    internal static Task<bool> PromptAsync(
        string message,
        IAnsiConsole console,
        CancellationToken cancellationToken) =>
        new ConfirmationPrompt(Markup.Escape(message))
        {
            DefaultValue = false,
            RequireEnter = false
        }.ShowAsync(console, cancellationToken);
}
