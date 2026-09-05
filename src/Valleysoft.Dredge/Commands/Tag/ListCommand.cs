using Newtonsoft.Json;
using Valleysoft.DockerRegistryClient;
using Valleysoft.DockerRegistryClient.Models;

namespace Valleysoft.Dredge.Commands.Tag;

public class ListCommand : RegistryCommandBase<ListOptions>
{
    public ListCommand(IDockerRegistryClientFactory dockerRegistryClientFactory, TextWriter? output = null)
        : base("list", "Lists the tags contained in the container repository", dockerRegistryClientFactory, output)
    {
    }

    protected override Task ExecuteAsync(CancellationToken cancellationToken)
    {
        ImageName imageName = ImageName.Parse(Options.Repo);
        return ExecuteCommandAsync(imageName.Registry, cancellationToken, async ct =>
        {
            using IDockerRegistryClient client = await DockerRegistryClientFactory.GetClientAsync(imageName.Registry);

            List<string> tags = [];

            Page<RepositoryTags> tagsPage =
                await client.Tags.GetAsync(imageName.Repo, Options.Limit, ct);
            BoundedListHelper.AddItems(tags, tagsPage.Value.Tags, Options.Limit);
            while (!BoundedListHelper.IsLimitReached(tags, Options.Limit) &&
                tagsPage.NextPageLink is not null)
            {
                tagsPage = await client.Tags.GetNextAsync(tagsPage.NextPageLink, ct);
                BoundedListHelper.AddItems(tags, tagsPage.Value.Tags, Options.Limit);
            }

            tags.Sort();

            string output = JsonConvert.SerializeObject(tags, JsonHelper.Settings);

            Output.WriteLine(output);
        });
    }
}
