using Newtonsoft.Json;
using Valleysoft.DockerRegistryClient.Models.Manifests.Oci;

namespace Valleysoft.Dredge.Commands.Referrer;

public class ListCommand : RegistryCommandBase<ListOptions>
{
    public ListCommand(IDockerRegistryClientFactory dockerRegistryClientFactory, TextWriter? output = null)
        : base("list", "Lists the referrers to a manifest", dockerRegistryClientFactory, output)
    {
    }

    protected override Task ExecuteAsync(CancellationToken cancellationToken)
    {
        ImageName imageName = ImageName.Parse(Options.Image);
        return ExecuteCommandAsync(imageName.Registry, cancellationToken, async ct =>
        {
            using IDockerRegistryClient client = await DockerRegistryClientFactory.GetClientAsync(imageName.Registry);
            OciImageIndex index =
                await ReferrerHelper.GetReferrersAsync(
                    client,
                    imageName,
                    Options.ArtifactType,
                    ct,
                    Options.Limit);
            string output = JsonConvert.SerializeObject(index, JsonHelper.Settings);

            Output.WriteLine(output);
        });
    }
}
