using Valleysoft.DockerCredsProvider;

namespace Valleysoft.Dredge.Tests;

public class DockerRegistryClientFactoryTests
{
    [Fact]
    public async Task GetClientAsync_WithTokenEnvironmentVariable_CreatesUsableClient()
    {
        DictionaryEnvironmentVariableProvider environmentVariableProvider = new(
            new Dictionary<string, string>
            {
                ["DREDGE_TOKEN"] = "test-token"
            });
        DockerRegistryClientFactory factory = new(environmentVariableProvider);

        using IDockerRegistryClient client = await factory.GetClientAsync("registry.example");

        Assert.IsType<DockerRegistryClientWrapper>(client);
        Assert.NotNull(client.Blobs);
        Assert.NotNull(client.Catalog);
        Assert.NotNull(client.Manifests);
        Assert.NotNull(client.Tags);
        Assert.NotNull(client.Referrers);
        Assert.Equal(["DREDGE_TOKEN"], environmentVariableProvider.RequestedVariables);
    }

    [Fact]
    public async Task GetClientAsync_WithUsernameAndPassword_QueriesCredentialsInPriorityOrder()
    {
        DictionaryEnvironmentVariableProvider environmentVariableProvider = new(
            new Dictionary<string, string>
            {
                ["DREDGE_USERNAME"] = "user",
                ["DREDGE_PASSWORD"] = "password"
            });
        DockerRegistryClientFactory factory = new(environmentVariableProvider);

        using IDockerRegistryClient client = await factory.GetClientAsync("registry.example");

        Assert.IsType<DockerRegistryClientWrapper>(client);
        Assert.Equal(
            ["DREDGE_TOKEN", "DREDGE_USERNAME", "DREDGE_PASSWORD"],
            environmentVariableProvider.RequestedVariables);
    }

    [Fact]
    public async Task GetClientAsync_WhenCancellationIsRequestedDoesNotReadEnvironment()
    {
        DictionaryEnvironmentVariableProvider environmentVariableProvider = new(new Dictionary<string, string>());
        DockerRegistryClientFactory factory = new(environmentVariableProvider);
        using CancellationTokenSource cancellationTokenSource = new();
        cancellationTokenSource.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => factory.GetClientAsync("registry.example", cancellationTokenSource.Token));

        Assert.Empty(environmentVariableProvider.RequestedVariables);
    }

    [Fact]
    public async Task GetClientAsync_ForwardsCancellationTokenToCredentialLookup()
    {
        DictionaryEnvironmentVariableProvider environmentVariableProvider = new(new Dictionary<string, string>());
        CancellationToken observedToken = default;
        TestDockerRegistryClientFactory factory = new(
            environmentVariableProvider,
            (_, cancellationToken) =>
            {
                observedToken = cancellationToken;
                throw new OperationCanceledException(cancellationToken);
            });

        using CancellationTokenSource cancellationTokenSource = new();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => factory.GetClientAsync("registry.example", cancellationTokenSource.Token));

        Assert.Equal(cancellationTokenSource.Token, observedToken);
    }

    private sealed class TestDockerRegistryClientFactory(
        IEnvironmentVariableProvider environmentVariableProvider,
        Func<string, CancellationToken, Task<DockerCredentials>> getCredentials) :
        DockerRegistryClientFactory(environmentVariableProvider)
    {
        protected override Task<DockerCredentials> GetCredentialsAsync(
            string registry,
            CancellationToken cancellationToken) =>
            getCredentials(registry, cancellationToken);
    }

    private sealed class DictionaryEnvironmentVariableProvider : IEnvironmentVariableProvider
    {
        private readonly IReadOnlyDictionary<string, string> variables;

        public DictionaryEnvironmentVariableProvider(IReadOnlyDictionary<string, string> variables)
        {
            this.variables = variables;
        }

        public List<string> RequestedVariables { get; } = [];

        public string? GetVariable(string name)
        {
            RequestedVariables.Add(name);
            return variables.GetValueOrDefault(name);
        }
    }
}
