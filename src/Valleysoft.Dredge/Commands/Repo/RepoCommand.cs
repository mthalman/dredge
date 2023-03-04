using System.CommandLine;
using Valleysoft.Dredge.Core;

namespace Valleysoft.Dredge.Commands.Repo;

public class RepoCommand : Command
{
    public RepoCommand(IDockerRegistryClientFactory dockerRegistryClientFactory)
        : base("repo", "Commands related to container repositories")
    {
        AddCommand(new ListCommand(dockerRegistryClientFactory));
    }
}
