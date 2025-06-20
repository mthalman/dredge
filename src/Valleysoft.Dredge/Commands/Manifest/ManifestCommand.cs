using System.CommandLine;

namespace Valleysoft.Dredge.Commands.Manifest;

public class ManifestCommand : Command
{
    public ManifestCommand(IDockerRegistryClientFactory dockerRegistryClientFactory) : base("manifest", "Commands related to manifests")
    {
        Subcommands.Add(new GetCommand(dockerRegistryClientFactory));
        Subcommands.Add(new DigestCommand(dockerRegistryClientFactory));
        Subcommands.Add(new ResolveCommand(dockerRegistryClientFactory));
    }
}
