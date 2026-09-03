using System.CommandLine;

namespace Valleysoft.Dredge.Commands.Referrer;

public class ReferrerCommand : Command
{
    public ReferrerCommand(IDockerRegistryClientFactory dockerRegistryClientFactory)
        : base("referrer", "Commands related to referrers")
    {
        Subcommands.Add(new ListCommand(dockerRegistryClientFactory));
        Subcommands.Add(new CheckCommand(dockerRegistryClientFactory));
        Subcommands.Add(new InspectCommand(dockerRegistryClientFactory));
        Subcommands.Add(new GetCommand(dockerRegistryClientFactory));
    }
}
