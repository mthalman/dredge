using Newtonsoft.Json;
using System.CommandLine;
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
            Argument<string> repoArg = new("repo", "Name of the Docker repository");
            AddArgument(repoArg);

            this.SetHandler(ExecuteAsync, repoArg);
        }

        private Task ExecuteAsync(string repo)
        {
            ImageName imageName = ImageName.Parse(repo);
            return CommandHelper.ExecuteCommandAsync(imageName.Registry, async () =>
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

                Console.Out.WriteLine(output);
            });
        }
    }
}
