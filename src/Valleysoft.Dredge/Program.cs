using System.CommandLine;
using Valleysoft.Dredge;

DockerRegistryClientFactory clientFactory = new();
RootCommand rootCmd = new("CLI for executing commands on a container registry's HTTP API.")
{
    new ImageCommand(clientFactory),
    new ManifestCommand(clientFactory),
    new RepoCommand(clientFactory),
    new TagCommand(clientFactory),
    new SettingsCommand(),
};

return rootCmd.Invoke(args);