using Newtonsoft.Json;
using Valleysoft.DockerRegistryClient;
using Valleysoft.DockerRegistryClient.Models;
using Valleysoft.Dredge.Core;

namespace Valleysoft.Dredge.Commands.Manifest;

public class GetCommand : RegistryCommandBase<GetOptions>
{
    public GetCommand(IDockerRegistryClientFactory dockerRegistryClientFactory)
        : base("get", "Queries a manifest", dockerRegistryClientFactory)
    {
    }

    protected override Task ExecuteAsync()
    {
        ImageName imageName = ImageName.Parse(Options.Image);
        return CommandHelper.ExecuteCommandAsync(imageName.Registry, async () =>
        {
            using IDockerRegistryClient client = await DockerRegistryClientFactory.GetClientAsync(imageName.Registry);

            ManifestInfo manifestInfo = await client.Manifests.GetAsync(imageName.Repo, (imageName.Tag ?? imageName.Digest)!);

            string output = JsonConvert.SerializeObject(manifestInfo.Manifest, Formatting.Indented);

            Console.Out.WriteLine(output);
        });
    }
}
