using System.CommandLine;

namespace Valleysoft.Dredge.Commands.Repo;

public class RepoCommand : Command
{
    public RepoCommand(IDockerRegistryClientFactory dockerRegistryClientFactory)
        : base("repo", "Commands related to container repositories")
    {
        Subcommands.Add(new ListCommand(dockerRegistryClientFactory));
    }
}
