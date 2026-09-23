using Valleysoft.DockerCredsProvider;
using Valleysoft.DockerRegistryClient;
using Valleysoft.DockerRegistryClient.Credentials;

namespace Valleysoft.Dredge;

internal class DockerRegistryClientFactory : IDockerRegistryClientFactory
{
    private readonly IEnvironmentVariableProvider environmentVariableProvider;

    public DockerRegistryClientFactory()
        : this(new EnvironmentVariableProvider())
    {
    }

    internal DockerRegistryClientFactory(IEnvironmentVariableProvider environmentVariableProvider)
    {
        this.environmentVariableProvider = environmentVariableProvider;
    }

    public async Task<IDockerRegistryClient> GetClientAsync(string? registry, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        IRegistryClientCredentials? clientCreds;

        string? accessToken;
        string? username;
        string? password;

        if ((accessToken = environmentVariableProvider.GetVariable("DREDGE_TOKEN")) is not null)
        {
            clientCreds = new TokenCredentials(accessToken);
        }
        else if ((username = environmentVariableProvider.GetVariable("DREDGE_USERNAME")) is not null &&
            (password = environmentVariableProvider.GetVariable("DREDGE_PASSWORD")) is not null)
        {
            clientCreds = new BasicAuthenticationCredentials(username, password);
        }
        else
        {
            DockerCredentials creds;
            string authRegistry = DockerHubHelper.GetAuthRegistry(registry);
            try
            {
                creds = await GetCredentialsAsync(authRegistry, cancellationToken);
            }
            catch (Exception e) when (e is CredsNotFoundException || e is FileNotFoundException)
            {
                return new DockerRegistryClientWrapper(
                    CreateClient(DockerHubHelper.GetApiRegistry(registry), cancellationToken: cancellationToken));
            }

            if (creds.IdentityToken is not null)
            {
                clientCreds = new TokenCredentials(creds.IdentityToken);
            }
            else
            {
                clientCreds = new BasicAuthenticationCredentials(creds.Username, creds.Password);
            }
        }

        return new DockerRegistryClientWrapper(CreateClient(registry, clientCreds, cancellationToken));
    }

    protected virtual Task<DockerCredentials> GetCredentialsAsync(
        string registry,
        CancellationToken cancellationToken) =>
        CredsProvider.GetCredentialsAsync(registry, cancellationToken);

    private static RegistryClient CreateClient(
        string? registry,
        IRegistryClientCredentials? clientCreds = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        RegistryClient client = new(DockerHubHelper.GetApiRegistry(registry), clientCreds);
        client.HttpClient.Timeout = Timeout.InfiniteTimeSpan;
        return client;
    }
}
