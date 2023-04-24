using Valleysoft.Dredge.Core;

namespace Valleysoft.Dredge.Commands;

public abstract class RegistryCommandBase<TOptions> : CommandWithOptions<TOptions>
    where TOptions : OptionsBase, new()
{
    public IDockerRegistryClientFactory DockerRegistryClientFactory { get; }

    protected RegistryCommandBase(string name, string description, IDockerRegistryClientFactory dockerRegistryClientFactory)
        : base(name, description)
    {
        DockerRegistryClientFactory = dockerRegistryClientFactory;
    }
}
