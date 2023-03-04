namespace Valleysoft.Dredge.Core;

public interface IDockerRegistryClientFactory
{
    Task<IDockerRegistryClient> GetClientAsync(string? registry);
}
