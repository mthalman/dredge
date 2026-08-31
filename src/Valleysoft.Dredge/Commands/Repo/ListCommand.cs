using Newtonsoft.Json;
using Valleysoft.DockerRegistryClient;
using Valleysoft.DockerRegistryClient.Models;

namespace Valleysoft.Dredge.Commands.Repo;

public class ListCommand : RegistryCommandBase<ListOptions>
{
    public ListCommand(IDockerRegistryClientFactory dockerRegistryClientFactory, TextWriter? output = null)
        : base("list", "Lists the repositories contained in the container registry", dockerRegistryClientFactory, output)
    {
    }

    protected override Task ExecuteAsync()
    {
        return ExecuteCommandAsync(Options.Registry, async () =>
        {
            using IDockerRegistryClient client = await DockerRegistryClientFactory.GetClientAsync(Options.Registry);

            List<string> repoNames = [];

            Page<Catalog> catalogPage = await client.Catalog.GetAsync();
            repoNames.AddRange(catalogPage.Value.RepositoryNames);
            while (catalogPage.NextPageLink is not null)
            {
                catalogPage = await client.Catalog.GetNextAsync(catalogPage.NextPageLink);
                repoNames.AddRange(catalogPage.Value.RepositoryNames);
            }

            repoNames.Sort();

            string output = JsonConvert.SerializeObject(repoNames, JsonHelper.Settings);

            Output.WriteLine(output);
        });
    }
}
