using Valleysoft.DockerRegistryClient;
using Valleysoft.DockerRegistryClient.Models;

namespace Valleysoft.Dredge.Core;

public record ResolvedManifest(
    ManifestInfo ManifestInfo,
    DockerManifestV2 Manifest);

public static class ManifestHelper
{
    public static async Task<ResolvedManifest> GetResolvedManifestAsync(
        IDockerRegistryClient client, ImageName imageName, AppSettings appSettings, PlatformOptions options)
    {
        ManifestInfo manifestInfo = await client.Manifests.GetAsync(imageName.Repo, (imageName.Tag ?? imageName.Digest)!);
        if (manifestInfo.Manifest is ManifestList manifestList)
        {
            string? os = GetPlatformValue(options.Os, appSettings.Platform.Os);
            string? osVersion = GetPlatformValue(options.OsVersion, appSettings.Platform.OsVersion);
            string? architecture = GetPlatformValue(options.Architecture, appSettings.Platform.Architecture);

            IEnumerable<ManifestReference> manifestRefs = manifestList.Manifests
                .Where(manifest =>
                    (os is null || manifest.Platform?.Os == os) &&
                    (osVersion is null || manifest.Platform?.OsVersion == osVersion) &&
                    (architecture is null || manifest.Platform?.Architecture == architecture));

            int manifestCount = manifestRefs.Count();

            if (manifestCount != 1)
            {
                throw new ManifestListResolutionException();
            }

            ManifestReference manifestRef = manifestRefs.First();

            if (manifestRef.Digest is null)
            {
                throw new Exception($"Digest of resolved manifest is not set.");
            }

            manifestInfo = await client.Manifests.GetAsync(imageName.Repo, manifestRef.Digest);
        }

        if (manifestInfo.Manifest is not DockerManifestV2 manifest)
        {
            throw new NotSupportedException(
                $"The image name '{imageName}' has a media type of '{manifestInfo.MediaType}' which is not supported.");
        }

        return new ResolvedManifest(manifestInfo, manifest);
    }

    private static string? GetPlatformValue(string? options, string settings)
    {
        if (!string.IsNullOrEmpty(options))
        {
            return options;
        }
        else if (!string.IsNullOrEmpty(settings))
        {
            return settings;
        }

        return null;
    }
}
