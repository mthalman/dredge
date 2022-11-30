using Microsoft.Rest;
using Valleysoft.DockerCredsProvider;

namespace Valleysoft.Dredge;

internal class DockerRegistryClientFactory : IDockerRegistryClientFactory
{
    public async Task<IDockerRegistryClient> GetClientAsync(string? registry)
    {
        DockerCredentials creds;

        try
        {
            creds = await CredsProvider.GetCredentialsAsync(DockerHubHelper.GetAuthRegistry(registry));
        }
        catch (CredsNotFoundException)
        {
            return new DockerRegistryClientWrapper(CreateClient(DockerHubHelper.GetApiRegistry(registry)));
        }
        ServiceClientCredentials clientCreds;
        if (creds.IdentityToken is not null)
        {
            clientCreds = new TokenCredentials(creds.IdentityToken);
        }
        else
        {
            clientCreds = new BasicAuthenticationCredentials()
            {
                UserName = creds.Username,
                Password = creds.Password
            };
        }

        return new DockerRegistryClientWrapper(CreateClient(registry, clientCreds));
    }

    private DockerRegistryClient.DockerRegistryClient CreateClient(string? registry, ServiceClientCredentials? clientCreds = null)
    {
        DockerRegistryClient.DockerRegistryClient client = new(DockerHubHelper.GetApiRegistry(registry), clientCreds);
        client.HttpClient.Timeout = new TimeSpan(0, 30, 0);
        return client;
    }
}
