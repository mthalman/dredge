using System.CommandLine;
using Valleysoft.Dredge;

RootCommand rootCmd = new("CLI for executing commands on a Docker registry's HTTP API.")
{
    new RepoCommand(),
    new TagCommand(),
    new ManifestCommand()
};

return rootCmd.Invoke(args);