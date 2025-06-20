using System.CommandLine;

namespace Valleysoft.Dredge.Commands.Tag;

public class TagCommand : Command
{
    public TagCommand(IDockerRegistryClientFactory dockerRegistryClientFactory)
        : base("tag", "Commands related to container image tags")
    {
        Subcommands.Add(new ListCommand(dockerRegistryClientFactory));
    }
}
