using System;
using System.Collections.Generic;
using System.CommandLine;
using System.CommandLine.Invocation;
using System.CommandLine.IO;
using System.Threading.Tasks;
using Valleysoft.DockerRegistryClient.Models;
using Newtonsoft.Json;

namespace Valleysoft.DockerRegistryClient.Cli
{
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
                AddOption(CommandHelper.GetRegistryOption());
                Handler = CommandHandler.Create<string?, IConsole>(ExecuteAsync);
            }

            private Task ExecuteAsync(string? registry, IConsole console)
            {
                return CommandHelper.ExecuteCommandAsync(console, async () =>
                {
                    using DockerRegistryClient client = await CommandHelper.GetRegistryClientAsync(registry);

                    List<string> repoNames = new List<string>();

                    Page<Catalog> catalogPage = await client.Catalog.GetAsync();
                    repoNames.AddRange(catalogPage.Value.RepositoryNames);
                    while (catalogPage.NextPageLink is not null)
                    {
                        catalogPage = await client.Catalog.GetNextAsync(catalogPage.NextPageLink);
                        repoNames.AddRange(catalogPage.Value.RepositoryNames);
                    }

                    string output = JsonConvert.SerializeObject(repoNames, Formatting.Indented);

                    console.Out.WriteLine(output);
                });
            }
        }
    }
}
