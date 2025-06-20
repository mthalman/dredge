using System.CommandLine;

namespace Valleysoft.Dredge.Commands.Image;

public class CompareCommand : Command
{
    public CompareCommand(IDockerRegistryClientFactory dockerRegistryClientFactory)
        : base("compare", "Compares two images")
    {
        Subcommands.Add(new CompareLayersCommand(dockerRegistryClientFactory));
        Subcommands.Add(new CompareFilesCommand(dockerRegistryClientFactory));
    }
}
