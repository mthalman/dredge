using Valleysoft.DockerRegistryClient;
using Valleysoft.DockerRegistryClient.Models;
using Valleysoft.Dredge.Commands;

namespace Valleysoft.Dredge;

internal record ResolvedManifest(
    ManifestInfo ManifestInfo,
    DockerManifestV2 Manifest);

internal static class ManifestHelper
{
    public static async Task<ResolvedManifest> GetResolvedManifestAsync(
        IDockerRegistryClient client, ImageName imageName, PlatformOptionsBase options)
    {
        ManifestInfo manifestInfo = await client.Manifests.GetAsync(imageName.Repo, (imageName.Tag ?? imageName.Digest)!);
        if (manifestInfo.Manifest is ManifestList manifestList)
        {
            AppSettings settings = AppSettings.Load();
            string? os = GetPlatformValue(options.Os, settings.Platform.Os);
            string? osVersion = GetPlatformValue(options.OsVersion, settings.Platform.OsVersion);
            string? architecture = GetPlatformValue(options.Architecture, settings.Platform.Architecture);

            IEnumerable<ManifestReference> manifestRefs = manifestList.Manifests
                .Where(manifest =>
                    (os is null || manifest.Platform?.Os == os) &&
                    (osVersion is null || manifest.Platform?.OsVersion == osVersion) &&
                    (architecture is null || manifest.Platform?.Architecture == architecture));

            int manifestCount = manifestRefs.Count();

            if (manifestCount != 1)
            {
                throw new Exception(
                    $"Unable to resolve the manifest list tag to a single matching platform. Run \"dredge manifest get\" to view the underlying manifests of this tag. Use {PlatformOptionsBase.OsOptionName}, {PlatformOptionsBase.ArchOptionName}, and {PlatformOptionsBase.OsVersionOptionName} to specify the target platform to match.");
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
