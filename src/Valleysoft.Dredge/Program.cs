using System.CommandLine;
using Valleysoft.Dredge;

RootCommand rootCmd = new("CLI for executing commands on a Docker registry's HTTP API.")
{
    new ImageCommand(),
    new ManifestCommand(),
    new RepoCommand(),
    new TagCommand(),
};

return rootCmd.Invoke(args);