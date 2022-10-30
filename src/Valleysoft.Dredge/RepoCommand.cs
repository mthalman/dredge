using Newtonsoft.Json;
using System.CommandLine;
using Valleysoft.DockerRegistryClient;
using Valleysoft.DockerRegistryClient.Models;

namespace Valleysoft.Dredge;

public class RepoCommand : Command
{
    public RepoCommand() : base("repo", "Commands related to Docker repositories")
    {
        AddCommand(new ListCommand());
    }

    private class ListCommand : Command
    {
        public ListCommand() : base("list", "Lists the repositories contained in the Docker registry")
        {
            Argument<string> registryArg = new("registry", "Name of the Docker registry");
            AddArgument(registryArg);

            this.SetHandler(ExecuteAsync, registryArg);
        }

        private Task ExecuteAsync(string registry)
        {
            return CommandHelper.ExecuteCommandAsync(registry, async () =>
            {
                using DockerRegistryClient.DockerRegistryClient client = await CommandHelper.GetRegistryClientAsync(registry);

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
