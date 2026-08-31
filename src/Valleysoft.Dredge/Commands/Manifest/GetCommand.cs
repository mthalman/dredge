using Newtonsoft.Json;
using Valleysoft.DockerRegistryClient.Models.Manifests;

namespace Valleysoft.Dredge.Commands.Manifest;

public class GetCommand : RegistryCommandBase<GetOptions>
{
    public GetCommand(IDockerRegistryClientFactory dockerRegistryClientFactory, TextWriter? output = null)
        : base("get", "Queries a manifest", dockerRegistryClientFactory, output)
    {
    }

    protected override Task ExecuteAsync(CancellationToken cancellationToken)
    {
        ImageName imageName = ImageName.Parse(Options.Image);
        return ExecuteCommandAsync(imageName.Registry, cancellationToken, async ct =>
        {
            using IDockerRegistryClient client = await DockerRegistryClientFactory.GetClientAsync(imageName.Registry);

            ManifestInfo manifestInfo = await client.Manifests.GetAsync(
                imageName.Repo, (imageName.Tag ?? imageName.Digest)!, ct);

            string output = JsonConvert.SerializeObject(manifestInfo.Manifest, JsonHelper.Settings);

            Output.WriteLine(output);
        });
    }
}
