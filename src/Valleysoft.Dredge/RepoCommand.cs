using Newtonsoft.Json;
using System.CommandLine;
using Valleysoft.DockerRegistryClient;
using Valleysoft.DockerRegistryClient.Models;

namespace Valleysoft.Dredge;

public class RepoCommand : Command
{
    public RepoCommand(IDockerRegistryClientFactory dockerRegistryClientFactory) : base("repo", "Commands related to container repositories")
    {
        AddCommand(new ListCommand(dockerRegistryClientFactory));
    }

    private class ListCommand : Command
    {
        private readonly IDockerRegistryClientFactory dockerRegistryClientFactory;

        public ListCommand(IDockerRegistryClientFactory dockerRegistryClientFactory) : base("list", "Lists the repositories contained in the container registry")
        {
            Argument<string> registryArg = new("registry", "Name of the container registry");
            AddArgument(registryArg);

            this.SetHandler(ExecuteAsync, registryArg);
            this.dockerRegistryClientFactory = dockerRegistryClientFactory;
        }

        private Task ExecuteAsync(string registry)
        {
            return CommandHelper.ExecuteCommandAsync(registry, async () =>
            {
                using IDockerRegistryClient client = await dockerRegistryClientFactory.GetClientAsync(registry);

                List<string> repoNames = new();

                Page<Catalog> catalogPage = await client.Catalog.GetAsync();
                repoNames.AddRange(catalogPage.Value.RepositoryNames);
                while (catalogPage.NextPageLink is not null)
                {
                    catalogPage = await client.Catalog.GetNextAsync(catalogPage.NextPageLink);
                    repoNames.AddRange(catalogPage.Value.RepositoryNames);
                }

                repoNames.Sort();

                string output = JsonConvert.SerializeObject(repoNames, Formatting.Indented);

                Console.Out.WriteLine(output);
            });
        }
    }
}
