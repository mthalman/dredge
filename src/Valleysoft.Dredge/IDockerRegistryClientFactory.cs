namespace Valleysoft.Dredge;

public interface IDockerRegistryClientFactory
{
    Task<IDockerRegistryClient> GetClientAsync(string? registry);
}
