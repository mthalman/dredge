using Newtonsoft.Json;
using Valleysoft.DockerRegistryClient;
using Valleysoft.DockerRegistryClient.Models.Manifests;
using Valleysoft.DockerRegistryClient.Models.Manifests.Oci;

namespace Valleysoft.Dredge.Commands.Referrer;

public class ListCommand : RegistryCommandBase<ListOptions>
{
    public ListCommand(IDockerRegistryClientFactory dockerRegistryClientFactory)
        : base("list", "Lists the referrers to a manifest", dockerRegistryClientFactory)
    {
    }

    protected override Task ExecuteAsync()
    {
        ImageName imageName = ImageName.Parse(Options.Image);
        return CommandHelper.ExecuteCommandAsync(imageName.Registry, async () =>
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
                ManifestInfo manifestInfo = await client.Manifests.GetAsync(imageName.Repo, imageName.Tag!);
                digest = manifestInfo.DockerContentDigest;
            }

            Page<OciImageIndex> indexPage = await client.Referrers.GetAsync(imageName.Repo, digest, Options.ArtifactType);
            initialIndex = indexPage.Value;
            while (indexPage.NextPageLink is not null)
            {
                Page<OciImageIndex> nextPage = await client.Referrers.GetAsync(imageName.Repo, digest, Options.ArtifactType);
                initialIndex.Manifests = initialIndex.Manifests
                    .Concat(nextPage.Value.Manifests)
                    .ToArray();
            }

            string output = JsonConvert.SerializeObject(initialIndex, JsonHelper.Settings);

            Console.Out.WriteLine(output);
        });
    }
}
