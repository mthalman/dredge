using System.Security.Authentication;
using Valleysoft.DockerRegistryClient;
using Valleysoft.DockerRegistryClient.Models;

namespace Valleysoft.Dredge.Tests;

[Trait("Category", "Integration")]
public sealed class RegistryAuthenticationIntegrationTests
{
    private readonly AuthenticatedRegistryFixture fixture;

    public RegistryAuthenticationIntegrationTests(AuthenticatedRegistryFixture fixture)
    {
        this.fixture = fixture;
    }

    [Fact]
    public async Task ProductionFactory_AuthenticatesToLiveRegistryWithEnvironmentCredentials()
    {
        await fixture.EnsureInitializedAsync();
        DictionaryEnvironmentVariableProvider environment = new(
            new Dictionary<string, string>
            {
                ["DREDGE_USERNAME"] = AuthenticatedRegistryFixture.Username,
                ["DREDGE_PASSWORD"] = AuthenticatedRegistryFixture.Password
            });
        DockerRegistryClientFactory factory = new(environment);

        using IDockerRegistryClient client =
            await factory.GetClientAsync(fixture.BaseUri.AbsoluteUri);
        Page<Catalog> catalog = await client.Catalog.GetAsync(
            null,
            TestContext.Current.CancellationToken);

        Assert.Empty(catalog.Value.RepositoryNames);
        Assert.Equal(
            ["DREDGE_TOKEN", "DREDGE_USERNAME", "DREDGE_PASSWORD"],
            environment.RequestedVariables);
    }

    [Fact]
    public async Task ProductionFactory_WithInvalidCredentialsIsRejectedByLiveRegistry()
    {
        await fixture.EnsureInitializedAsync();
        DictionaryEnvironmentVariableProvider environment = new(
            new Dictionary<string, string>
            {
                ["DREDGE_USERNAME"] = AuthenticatedRegistryFixture.Username,
                ["DREDGE_PASSWORD"] = "wrong-password"
            });
        DockerRegistryClientFactory factory = new(environment);

        using IDockerRegistryClient client =
            await factory.GetClientAsync(fixture.BaseUri.AbsoluteUri);

        await Assert.ThrowsAsync<AuthenticationException>(
            () => client.Catalog.GetAsync(
                null,
                TestContext.Current.CancellationToken));
    }

    private sealed class DictionaryEnvironmentVariableProvider(
        IReadOnlyDictionary<string, string> variables) : IEnvironmentVariableProvider
    {
        public List<string> RequestedVariables { get; } = [];

        public string? GetVariable(string name)
        {
            RequestedVariables.Add(name);
            return variables.GetValueOrDefault(name);
        }
    }
}
