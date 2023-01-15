using System.CommandLine;
using System.CommandLine.Invocation;

namespace Valleysoft.Dredge;

public abstract class CommandWithOptions<TOptions> : Command
    where TOptions : OptionsBase, new()
{
    public new TOptions Options { get; set; } = new();

    public IDockerRegistryClientFactory DockerRegistryClientFactory { get; }

    protected CommandWithOptions(string name, string description, IDockerRegistryClientFactory dockerRegistryClientFactory)
        : base(name, description)
    {
        DockerRegistryClientFactory = dockerRegistryClientFactory;
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
