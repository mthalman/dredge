using System.CommandLine;

namespace Valleysoft.Dredge.Commands.Image;

public class ImageCommand : Command
{
    public ImageCommand(IDockerRegistryClientFactory dockerRegistryClientFactory)
        : base("image", "Commands related to container images")
    {
        Subcommands.Add(new CompareCommand(dockerRegistryClientFactory));
        Subcommands.Add(new InspectCommand(dockerRegistryClientFactory));
        Subcommands.Add(new OsCommand(dockerRegistryClientFactory));
        Subcommands.Add(new SaveLayersCommand(dockerRegistryClientFactory));
        Subcommands.Add(new DockerfileCommand(dockerRegistryClientFactory));
    }
}
