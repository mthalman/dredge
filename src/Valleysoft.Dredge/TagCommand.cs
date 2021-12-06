using Newtonsoft.Json;
using System.CommandLine;
using System.CommandLine.Invocation;
using System.CommandLine.IO;
using Valleysoft.DockerRegistryClient;
using Valleysoft.DockerRegistryClient.Models;

namespace Valleysoft.Dredge;

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
            AddArgument(new Argument<string>("repo", "Name of the Docker repository"));
            Handler = CommandHandler.Create<string, IConsole>(ExecuteAsync);
        }

        private Task ExecuteAsync(string repo, IConsole console)
        {
            ImageName imageName = ImageName.Parse(repo);
            return CommandHelper.ExecuteCommandAsync(console, imageName.Registry, async() =>
            {
                using DockerRegistryClient.DockerRegistryClient client = await CommandHelper.GetRegistryClientAsync(imageName.Registry);

                List<string> tags = new();

                Page<RepositoryTags> tagsPage = await client.Tags.GetAsync(imageName.Repo);
                tags.AddRange(tagsPage.Value.Tags);
                while (tagsPage.NextPageLink is not null)
                {
                    tagsPage = await client.Tags.GetNextAsync(tagsPage.NextPageLink);
                    tags.AddRange(tagsPage.Value.Tags);
                }

                tags.Sort();

                string output = JsonConvert.SerializeObject(tags, Formatting.Indented);

                console.Out.WriteLine(output);
            });
        }
    }
}
