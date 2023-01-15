using System.CommandLine;

namespace Valleysoft.Dredge.Commands.Tag;

public class TagCommand : Command
{
    public TagCommand(IDockerRegistryClientFactory dockerRegistryClientFactory)
        : base("tag", "Commands related to container image tags")
    {
        AddCommand(new ListCommand(dockerRegistryClientFactory));
    }
}
