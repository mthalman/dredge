using Valleysoft.DockerRegistryClient;
using Valleysoft.DockerRegistryClient.Models.Manifests;
using Valleysoft.DockerRegistryClient.Models.Manifests.Oci;

namespace Valleysoft.Dredge.Commands.Referrer;

internal static class ReferrerHelper
{
    public static async Task<OciImageIndex> GetReferrersAsync(
        IDockerRegistryClient client,
        ImageName imageName,
        string? artifactType,
        CancellationToken cancellationToken,
        int? limit = null)
    {
        string digest;
        if (!string.IsNullOrEmpty(imageName.Digest))
        {
            digest = imageName.Digest;
        }
        else
        {
            ManifestInfo manifestInfo =
                await client.Manifests.GetAsync(imageName.Repo, imageName.Tag!, cancellationToken);
            digest = manifestInfo.DockerContentDigest;
        }

        Page<OciImageIndex> indexPage =
            await client.Referrers.GetAsync(imageName.Repo, digest, artifactType, cancellationToken);
        OciImageIndex index = indexPage.Value;
        List<ManifestReference> manifests = [];
        BoundedListHelper.AddItems(manifests, index.Manifests, limit);
        while (!BoundedListHelper.IsLimitReached(manifests, limit) &&
            indexPage.NextPageLink is not null)
        {
            indexPage = await client.Referrers.GetNextAsync(indexPage.NextPageLink, cancellationToken);
            BoundedListHelper.AddItems(manifests, indexPage.Value.Manifests, limit);
        }

        index.Manifests = manifests.ToArray();
        return index;
    }
}
