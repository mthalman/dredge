using System.Collections.Generic;
using System.CommandLine;
using System.CommandLine.Invocation;
using System.CommandLine.IO;
using System.Threading.Tasks;
using Valleysoft.DockerRegistryClient.Models;
using Newtonsoft.Json;

namespace Valleysoft.DockerRegistryClient.Cli
{
    public class TagCommand : Command
    {
        public TagCommand() : base("tag", "Commands related to Docker tags")
        {
            AddCommand(new ListCommand());
        }

        private class ListCommand : Command
        {
            public ListCommand() : base("list", "Lists the tag contained in the Docker repository")
            {
                AddArgument(CommandHelper.GetRepositoryArgument());
                AddOption(CommandHelper.GetRegistryOption());
                Handler = CommandHandler.Create<string, string?, IConsole>(ExecuteAsync);
            }

            private Task ExecuteAsync(string repository, string? registry, IConsole console)
            {
                return CommandHelper.ExecuteCommandAsync(console, async () =>
                {
                    using DockerRegistryClient client = await CommandHelper.GetRegistryClientAsync(registry);

                    List<string> tags = new List<string>();

                    Page<RepositoryTags> tagsPage = await client.Tags.GetAsync(repository);
                    tags.AddRange(tagsPage.Value.Tags);
                    while (tagsPage.NextPageLink is not null)
                    {
                        tagsPage = await client.Tags.GetNextAsync(tagsPage.NextPageLink);
                        tags.AddRange(tagsPage.Value.Tags);
                    }

                    string output = JsonConvert.SerializeObject(tags, Formatting.Indented);

                    console.Out.WriteLine(output);
                });
            }
        }
    }
}
