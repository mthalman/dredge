using Newtonsoft.Json;
using System.CommandLine;
using Valleysoft.DockerRegistryClient;
using Valleysoft.DockerRegistryClient.Models;

namespace Valleysoft.Dredge;

public class TagCommand : Command
{
    public TagCommand(IDockerRegistryClientFactory dockerRegistryClientFactory) : base("tag", "Commands related to container image tags")
    {
        AddCommand(new ListCommand(dockerRegistryClientFactory));
    }

    private class ListCommand : Command
    {
        private readonly IDockerRegistryClientFactory dockerRegistryClientFactory;

        public ListCommand(IDockerRegistryClientFactory dockerRegistryClientFactory) : base("list", "Lists the tag contained in the container repository")
        {
            Argument<string> repoArg = new("repo", "Name of the container repository");
            AddArgument(repoArg);

            this.SetHandler(ExecuteAsync, repoArg);
            this.dockerRegistryClientFactory = dockerRegistryClientFactory;
        }

        private Task ExecuteAsync(string repo)
        {
            ImageName imageName = ImageName.Parse(repo);
            return CommandHelper.ExecuteCommandAsync(imageName.Registry, async () =>
            {
                using IDockerRegistryClient client = await dockerRegistryClientFactory.GetClientAsync(imageName.Registry);

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
