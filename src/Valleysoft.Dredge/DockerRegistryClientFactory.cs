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
            return new DockerRegistryClientWrapper(
                new DockerRegistryClient.DockerRegistryClient(DockerHubHelper.GetApiRegistry(registry)));
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

        return new DockerRegistryClientWrapper(
            new DockerRegistryClient.DockerRegistryClient(DockerHubHelper.GetApiRegistry(registry), clientCreds));
    }
}
