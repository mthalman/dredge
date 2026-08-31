namespace Valleysoft.Dredge.Commands;

public abstract class RegistryCommandBase<TOptions> : CommandWithOptions<TOptions>
    where TOptions : OptionsBase, new()
{
    public IDockerRegistryClientFactory DockerRegistryClientFactory { get; }
    protected TextWriter Output { get; }

    protected RegistryCommandBase(
        string name,
        string description,
        IDockerRegistryClientFactory dockerRegistryClientFactory,
        TextWriter? output = null)
        : base(name, description)
    {
        DockerRegistryClientFactory = dockerRegistryClientFactory;
        Output = output ?? Console.Out;
    }

    protected Task ExecuteCommandAsync(
        string? registry,
        CancellationToken cancellationToken,
        Func<CancellationToken, Task> execute) =>
        CommandHelper.ExecuteCommandAsync(registry, cancellationToken, execute, Error, Exit);

    protected virtual TextWriter Error => Console.Error;

    protected virtual void Exit(int exitCode) => Environment.Exit(exitCode);
}
