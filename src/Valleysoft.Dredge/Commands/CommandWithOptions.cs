using System.CommandLine;

namespace Valleysoft.Dredge.Commands;

public abstract class CommandWithOptions<TOptions> : Command
    where TOptions : OptionsBase, new()
{
    public new TOptions Options { get; set; } = new();

    protected CommandWithOptions(string name, string description)
        : base(name, description)
    {
        Options.SetCommandOptions(this);
        this.SetAction(parseResult =>
        {
            Options.SetParseResult(parseResult);
            return ExecuteAsync();
        });
    }

    protected abstract Task ExecuteAsync();
}
