using Valleysoft.DockerRegistryClient;
using Valleysoft.DockerRegistryClient.Models;
using Valleysoft.Dredge.Commands;

namespace Valleysoft.Dredge;

internal static class ManifestHelper
{
    public static async Task<ManifestInfo> GetManifestInfoAsync(IDockerRegistryClient client, ImageName imageName, PlatformOptionsBase options)
    {
        ManifestInfo manifestInfo = await client.Manifests.GetAsync(imageName.Repo, (imageName.Tag ?? imageName.Digest)!);
        if (manifestInfo.Manifest is ManifestList manifestList)
        {
            ManifestReference? manifestRef = manifestList.Manifests
            .SingleOrDefault(manifest =>
                    manifest.Platform?.Os == options.Os &&
                    manifest.Platform?.OsVersion == options.OsVersion &&
                    manifest.Platform?.Architecture == options.Architecture);
            if (manifestRef is null)
            {
                throw new Exception(
                    $"Unable to resolve the manifest list tag to a matching platform. Run \"dredge manifest get\" to view the underlying manifests of this tag. Use {PlatformOptionsBase.OsOptionName}, {PlatformOptionsBase.ArchOptionName}, and {PlatformOptionsBase.OsVersionOptionName} (Windows only) to specify the target platform to match.");
            }

            if (manifestRef.Digest is null)
            {
                throw new Exception($"Digest of resolved manifest is not set.");
            }

            manifestInfo = await client.Manifests.GetAsync(imageName.Repo, manifestRef.Digest);
        }

        return manifestInfo;
    }

    public static DockerManifestV2 GetManifest(string image, ManifestInfo manifestInfo)
    {
        if (manifestInfo.Manifest is ManifestList)
        {
            throw new NotSupportedException(
                $"The name '{image}' is a manifest list and doesn't directly refer to an image. Resolve the manifest name to an image first by using the \"dredge manifest resolve\" command.");
        }

        if (manifestInfo.Manifest is not DockerManifestV2 manifest)
        {
            throw new NotSupportedException(
                $"The image name '{image}' has a media type of '{manifestInfo.MediaType}' which is not supported.");
        }

        return manifest;
    }
}
