using ICSharpCode.SharpZipLib.Tar;
using System.IO.Compression;
using System.Text;
using Valleysoft.DockerRegistryClient;
using Valleysoft.DockerRegistryClient.Models;
using ImageConfig = Valleysoft.DockerRegistryClient.Models.Image;

namespace Valleysoft.Dredge.Core;

public static class OsHelper
{
    public static async Task<OsInfo> GetOsInfoAsync(
        string imageName, IDockerRegistryClientFactory dockerRegistryClientFactory, AppSettings appSettings, PlatformOptions platformOptions)
    {
        ImageName parsedImageName = ImageName.Parse(imageName);
        using IDockerRegistryClient client = await dockerRegistryClientFactory.GetClientAsync(parsedImageName.Registry);
        DockerManifestV2 manifest = (await ManifestHelper.GetResolvedManifestAsync(client, parsedImageName, appSettings, platformOptions)).Manifest;

        string? configDigest = manifest.Config?.Digest;
        if (configDigest is null)
        {
            throw new NotSupportedException($"Could not resolve the image config digest of '{imageName}'.");
        }

        ImageConfig imageConfig = await client.Blobs.GetImageAsync(parsedImageName.Repo, configDigest);

        ManifestLayer baseLayer = manifest.Layers.First();
        if (baseLayer.Digest is null)
        {
            throw new Exception($"No digest was found for the base layer of '{imageName}'.");
        }

        if (imageConfig.Os.Equals("windows", StringComparison.OrdinalIgnoreCase))
        {
            var windowsOsInfo = await OsHelper.GetWindowsOsInfoAsync(imageConfig, baseLayer.Digest, dockerRegistryClientFactory);
            return new OsInfo { WindowsOsInfo = windowsOsInfo?.Info };
        }
        else
        {
            LinuxOsInfo? osInfo = await OsHelper.GetLinuxOsInfoAsync(client, parsedImageName, baseLayer.Digest);
            return new OsInfo { LinuxOsInfo = osInfo };
        }
    }

    private static async Task<LinuxOsInfo?> GetLinuxOsInfoAsync(IDockerRegistryClient client, ImageName imageName, string baseLayerDigest)
    {
        using Stream blobStream = await client.Blobs.GetAsync(imageName.Repo, baseLayerDigest);
        using GZipStream gZipStream = new(blobStream, CompressionMode.Decompress);

        // Can't use System.Formats.Tar.TarReader because it fails to read certain types of tarballs:
        // https://github.com/dotnet/runtime/issues/74316#issuecomment-1312227247

        using TarInputStream tarStream = new(gZipStream, Encoding.UTF8);
        TarEntry? entry = null;
        do
        {
            entry = tarStream.GetNextEntry();

            // Look for the os-release file (skip symlinks)
            if (entry is not null &&
                entry.Size > 0 &&
                (entry.Name == "etc/os-release" || entry.Name == "usr/lib/os-release"))
            {
                using MemoryStream memStream = new();
                tarStream.CopyEntryContents(memStream);
                memStream.Position = 0;
                using StreamReader reader = new(memStream);
                string content = await reader.ReadToEndAsync();
                return LinuxOsInfo.Parse(content);
            }
        } while (entry is not null);

        return null;
    }

    public static async Task<(WindowsOsInfo Info, string Repo)?> GetWindowsOsInfoAsync(ImageConfig imageConfig, string baseLayerDigest,
        IDockerRegistryClientFactory dockerRegistryClientFactory)
    {
        const string NanoServerRepo = "windows/nanoserver";
        const string ServerCoreRepo = "windows/servercore";
        const string ServerRepo = "windows/server";
        const string WindowsRepo = "windows";

        using IDockerRegistryClient mcrClient =
            await dockerRegistryClientFactory.GetClientAsync(RegistryHelper.McrRegistry);

        if (await mcrClient.Blobs.ExistsAsync(NanoServerRepo, baseLayerDigest))
        {
            return (new(WindowsType.NanoServer, imageConfig.OsVersion), NanoServerRepo);
        }
        else if (await mcrClient.Blobs.ExistsAsync(ServerCoreRepo, baseLayerDigest))
        {
            return (new(WindowsType.ServerCore, imageConfig.OsVersion), ServerCoreRepo);
        }
        else if (await mcrClient.Blobs.ExistsAsync(ServerRepo, baseLayerDigest))
        {
            return (new(WindowsType.Server, imageConfig.OsVersion), ServerRepo);
        }
        else if (await mcrClient.Blobs.ExistsAsync(WindowsRepo, baseLayerDigest))
        {
            return (new(WindowsType.Windows, imageConfig.OsVersion), WindowsRepo);
        }
        else
        {
            return null;
        }
    }
}
