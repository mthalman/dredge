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

    protected override Task ExecuteAsync(CancellationToken cancellationToken)
    {
        return ExecuteCommandAsync(Options.Registry, cancellationToken, async ct =>
        {
            using IDockerRegistryClient client = await DockerRegistryClientFactory.GetClientAsync(Options.Registry);

            List<string> repoNames = [];

            Page<Catalog> catalogPage = await client.Catalog.GetAsync(Options.Limit, ct);
            BoundedListHelper.AddItems(repoNames, catalogPage.Value.RepositoryNames, Options.Limit);
            while (!BoundedListHelper.IsLimitReached(repoNames, Options.Limit) &&
                catalogPage.NextPageLink is not null)
            {
                catalogPage = await client.Catalog.GetNextAsync(catalogPage.NextPageLink, ct);
                BoundedListHelper.AddItems(repoNames, catalogPage.Value.RepositoryNames, Options.Limit);
            }

            repoNames.Sort();

            string output = JsonConvert.SerializeObject(repoNames, JsonHelper.Settings);

            Output.WriteLine(output);
        });
    }
}
