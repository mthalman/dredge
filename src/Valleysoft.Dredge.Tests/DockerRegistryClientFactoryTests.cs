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
