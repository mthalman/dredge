using System.CommandLine;
using Valleysoft.Dredge;
using Valleysoft.Dredge.Commands.Image;
using Valleysoft.Dredge.Commands.Manifest;
using Valleysoft.Dredge.Commands.Repo;
using Valleysoft.Dredge.Commands.Settings;
using Valleysoft.Dredge.Commands.Tag;

DockerRegistryClientFactory clientFactory = new();
RootCommand rootCmd = new("CLI for executing commands on a container registry's HTTP API.")
{
    new ImageCommand(clientFactory),
    new ManifestCommand(clientFactory),
    new RepoCommand(clientFactory),
    new TagCommand(clientFactory),
    new SettingsCommand(clientFactory),
};

return rootCmd.Invoke(args);
