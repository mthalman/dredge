using Newtonsoft.Json;
using System.CommandLine;
using System.CommandLine.Invocation;
using System.CommandLine.IO;
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
            AddOption(
                new Option<string>(
                    new string[] { "--registry", "-r" },
                    "Name of the Docker registry (by default, Docker Hub registry is used)"));
            Handler = CommandHandler.Create<string?, IConsole>(ExecuteAsync);
        }

        private Task ExecuteAsync(string? registry, IConsole console)
        {
            return CommandHelper.ExecuteCommandAsync(console, registry, async () =>
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

                console.Out.WriteLine(output);
            });
        }
    }
}
