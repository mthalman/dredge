using System.CommandLine;
using System.CommandLine.Invocation;

namespace Valleysoft.Dredge.Commands;

public abstract class CommandWithOptions<TOptions> : Command
    where TOptions : OptionsBase, new()
{
    public new TOptions Options { get; set; } = new();

    protected CommandWithOptions(string name, string description)
        : base(name, description)
    {
        Options.SetCommandOptions(this);
        this.SetHandler(ExecuteAsyncCore);
    }

    private async Task ExecuteAsyncCore(InvocationContext context)
    {
        Options.SetParseResult(context.BindingContext.ParseResult);
        await ExecuteAsync();
    }

    protected abstract Task ExecuteAsync();
}
