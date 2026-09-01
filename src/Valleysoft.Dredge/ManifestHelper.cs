using Valleysoft.DockerRegistryClient.Models.Manifests;
using Valleysoft.Dredge.Commands;

namespace Valleysoft.Dredge;

internal record ResolvedManifest(
    ManifestInfo ManifestInfo,
    IImageManifest Manifest);

internal static class ManifestHelper
{
    public static async Task<ResolvedManifest> GetResolvedManifestAsync(
        IDockerRegistryClient client,
        ImageName imageName,
        PlatformOptionsBase options,
        CancellationToken cancellationToken = default,
        AppSettings? settings = null)
    {
        ManifestInfo manifestInfo = await client.Manifests.GetAsync(
            imageName.Repo, (imageName.Tag ?? imageName.Digest)!, cancellationToken);
        return await GetResolvedManifestAsync(
            client,
            imageName,
            options,
            manifestInfo,
            () => (settings ??= AppSettings.Load()).Platform,
            cancellationToken);
    }

    public static async Task<ResolvedManifest> GetResolvedManifestAsync(
        IDockerRegistryClient client,
        ImageName imageName,
        PlatformOptionsBase options,
        ManifestInfo manifestInfo,
        CancellationToken cancellationToken)
    {
        return await GetResolvedManifestAsync(
            client,
            imageName,
            options,
            manifestInfo,
            () => AppSettings.Load().Platform,
            cancellationToken);
    }

    internal static async Task<ResolvedManifest> GetResolvedManifestAsync(
        IDockerRegistryClient client,
        ImageName imageName,
        PlatformOptionsBase options,
        ManifestInfo manifestInfo,
        Func<PlatformSettings> platformSettingsProvider,
        CancellationToken cancellationToken)
    {
        if (manifestInfo.Manifest is IManifestList manifestList)
        {
            string? os = options.Os;
            string? osVersion = options.OsVersion;
            string? architecture = options.Architecture;
            if (string.IsNullOrEmpty(os) ||
                string.IsNullOrEmpty(osVersion) ||
                string.IsNullOrEmpty(architecture))
            {
                PlatformSettings settings = platformSettingsProvider();
                os = GetPlatformValue(os, settings.Os);
                osVersion = GetPlatformValue(osVersion, settings.OsVersion);
                architecture = GetPlatformValue(architecture, settings.Architecture);
            }

            IEnumerable<IManifestReference> manifestRefs = manifestList.Manifests
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

            IManifestReference manifestRef = manifestRefs.First();

            if (manifestRef.Digest is null)
            {
                throw new Exception($"Digest of resolved manifest is not set.");
            }

            manifestInfo = await client.Manifests.GetAsync(
                imageName.Repo, manifestRef.Digest, cancellationToken);
        }

        if (manifestInfo.Manifest is not IImageManifest manifest)
        {
            throw new NotSupportedException(
                $"The image name '{imageName}' has a media type of '{manifestInfo.MediaType}' which is not supported.");
        }

        return new ResolvedManifest(manifestInfo, manifest);
    }

    public static Task<ResolvedManifest> GetResolvedManifestAsync(
        IDockerRegistryClient client,
        ImageName imageName,
        PlatformOptionsBase options,
        AppSettings settings) =>
        GetResolvedManifestAsync(client, imageName, options, cancellationToken: default, settings);

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
