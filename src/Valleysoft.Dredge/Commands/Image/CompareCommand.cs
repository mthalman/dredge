using System.CommandLine;

namespace Valleysoft.Dredge.Commands.Image;

public class CompareCommand : Command
{
    public CompareCommand(IDockerRegistryClientFactory dockerRegistryClientFactory)
        : base("compare", "Compares two images")
    {
        AddCommand(new CompareLayersCommand(dockerRegistryClientFactory));
        AddCommand(new CompareFilesCommand(dockerRegistryClientFactory));
    }
}
