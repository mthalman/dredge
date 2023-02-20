using System.CommandLine;
using Valleysoft.Dredge.Core;

namespace Valleysoft.Dredge.Commands.Manifest;

public class ManifestCommand : Command
{
    public ManifestCommand(IDockerRegistryClientFactory dockerRegistryClientFactory) : base("manifest", "Commands related to manifests")
    {
        AddCommand(new GetCommand(dockerRegistryClientFactory));
        AddCommand(new DigestCommand(dockerRegistryClientFactory));
        AddCommand(new ResolveCommand(dockerRegistryClientFactory));
    }
}
