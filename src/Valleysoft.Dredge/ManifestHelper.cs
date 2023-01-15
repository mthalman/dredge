using Valleysoft.DockerRegistryClient.Models;

namespace Valleysoft.Dredge;

internal static class ManifestHelper
{
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
