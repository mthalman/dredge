using System.CommandLine;

namespace Valleysoft.Dredge.Commands.Referrer;

public class ReferrerCommand : Command
{
    public ReferrerCommand(IDockerRegistryClientFactory dockerRegistryClientFactory)
        : base("referrer", "Commands related to referrers")
    {
        Subcommands.Add(new ListCommand(dockerRegistryClientFactory));
    }
}
