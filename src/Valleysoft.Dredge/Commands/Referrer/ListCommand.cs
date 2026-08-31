using Newtonsoft.Json;
using Valleysoft.DockerRegistryClient;
using Valleysoft.DockerRegistryClient.Models.Manifests;
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

            OciImageIndex initialIndex;

            string digest;
            if (!string.IsNullOrEmpty(imageName.Digest))
            {
                digest = imageName.Digest;
            }
            else
            {
                ManifestInfo manifestInfo = await client.Manifests.GetAsync(imageName.Repo, imageName.Tag!, ct);
                digest = manifestInfo.DockerContentDigest;
            }

            Page<OciImageIndex> indexPage =
                await client.Referrers.GetAsync(imageName.Repo, digest, Options.ArtifactType, ct);
            initialIndex = indexPage.Value;
            while (indexPage.NextPageLink is not null)
            {
                Page<OciImageIndex> nextPage =
                    await client.Referrers.GetNextAsync(indexPage.NextPageLink, ct);
                initialIndex.Manifests =
                [
                    .. initialIndex.Manifests,
                    .. nextPage.Value.Manifests
                ];
                indexPage = nextPage;
            }

            string output = JsonConvert.SerializeObject(initialIndex, JsonHelper.Settings);

            Output.WriteLine(output);
        });
    }
}
