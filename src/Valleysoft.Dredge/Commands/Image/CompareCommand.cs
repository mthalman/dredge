using System.CommandLine;

namespace Valleysoft.Dredge.Commands.Image;

public class CompareCommand : Command
{
    public CompareCommand(IDockerRegistryClientFactory dockerRegistryClientFactory)
        : base(
            "compare",
            "Compares two images. The layers comparison uses exit code 2 when differences are found; this is not a command failure.")
    {
        Subcommands.Add(new CompareLayersCommand(dockerRegistryClientFactory));
        Subcommands.Add(new CompareFilesCommand(dockerRegistryClientFactory));
        Subcommands.Add(new CompareMetadataCommand(dockerRegistryClientFactory));
    }
}
