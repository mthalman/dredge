using Newtonsoft.Json;
using Valleysoft.DockerRegistryClient.Models.Manifests;

namespace Valleysoft.Dredge.Commands.Manifest;

public class GetCommand : RegistryCommandBase<GetOptions>
{
    public GetCommand(IDockerRegistryClientFactory dockerRegistryClientFactory, TextWriter? output = null)
        : base("get", "Queries a manifest", dockerRegistryClientFactory, output)
    {
    }

    protected override Task ExecuteAsync()
    {
        ImageName imageName = ImageName.Parse(Options.Image);
        return ExecuteCommandAsync(imageName.Registry, async () =>
        {
            using IDockerRegistryClient client = await DockerRegistryClientFactory.GetClientAsync(imageName.Registry);

            ManifestInfo manifestInfo = await client.Manifests.GetAsync(imageName.Repo, (imageName.Tag ?? imageName.Digest)!);

            string output = JsonConvert.SerializeObject(manifestInfo.Manifest, JsonHelper.Settings);

            Output.WriteLine(output);
        });
    }
}
