using Microsoft.Rest;
using Valleysoft.DockerCredsProvider;
using Valleysoft.DockerRegistryClient;
using Valleysoft.Dredge.Core;

namespace Valleysoft.Dredge;

internal class DockerRegistryClientFactory : IDockerRegistryClientFactory
{
    public async Task<IDockerRegistryClient> GetClientAsync(string? registry)
    {
        ServiceClientCredentials? clientCreds;

        string? accessToken;
        string? username;
        string? password;

        if ((accessToken = Environment.GetEnvironmentVariable("DREDGE_TOKEN")) is not null)
        {
            clientCreds = new TokenCredentials(accessToken);
        }
        else if ((username = Environment.GetEnvironmentVariable("DREDGE_USERNAME")) is not null &&
            (password = Environment.GetEnvironmentVariable("DREDGE_PASSWORD")) is not null)
        {
            clientCreds = new BasicAuthenticationCredentials()
            {
                UserName = username,
                Password = password
            };
        }
        else
        {
            DockerCredentials creds;
            try
            {
                creds = await CredsProvider.GetCredentialsAsync(DockerHubHelper.GetAuthRegistry(registry));
            }
            catch (Exception e) when (e is CredsNotFoundException || e is FileNotFoundException)
            {
                return new DockerRegistryClientWrapper(CreateClient(DockerHubHelper.GetApiRegistry(registry)));
            }

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
        }

        return new DockerRegistryClientWrapper(CreateClient(registry, clientCreds));
    }

    private static RegistryClient CreateClient(string? registry, ServiceClientCredentials? clientCreds = null)
    {
        RegistryClient client = new(DockerHubHelper.GetApiRegistry(registry), clientCreds);
        client.HttpClient.Timeout = new TimeSpan(0, 30, 0);
        return client;
    }
}
