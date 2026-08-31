using Newtonsoft.Json;
using Valleysoft.DockerRegistryClient.Models.Manifests;

namespace Valleysoft.Dredge.Commands.Manifest;

public class GetCommand : RegistryCommandBase<GetOptions>
{
    public GetCommand(IDockerRegistryClientFactory dockerRegistryClientFactory)
        : base("get", "Queries a manifest", dockerRegistryClientFactory)
    {
    }

    protected override Task ExecuteAsync(CancellationToken cancellationToken)
    {
        ImageName imageName = ImageName.Parse(Options.Image);
        return CommandHelper.ExecuteCommandAsync(imageName.Registry, cancellationToken, async ct =>
        {
            using IDockerRegistryClient client = await DockerRegistryClientFactory.GetClientAsync(imageName.Registry);

            ManifestInfo manifestInfo = await client.Manifests.GetAsync(
                imageName.Repo, (imageName.Tag ?? imageName.Digest)!, ct);

            string output = JsonConvert.SerializeObject(manifestInfo.Manifest, JsonHelper.Settings);

            Console.Out.WriteLine(output);
        });
    }
}
