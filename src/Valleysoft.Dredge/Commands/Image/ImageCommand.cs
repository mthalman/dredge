using System.CommandLine;

namespace Valleysoft.Dredge.Commands.Image;

public class ImageCommand : Command
{
    public ImageCommand(IDockerRegistryClientFactory dockerRegistryClientFactory)
        : base("image", "Commands related to container images")
    {
        AddCommand(new CompareCommand(dockerRegistryClientFactory));
        AddCommand(new InspectCommand(dockerRegistryClientFactory));
        AddCommand(new OsCommand(dockerRegistryClientFactory));
        AddCommand(new SaveLayersCommand(dockerRegistryClientFactory));
        AddCommand(new DockerfileCommand(dockerRegistryClientFactory));
    }
}
