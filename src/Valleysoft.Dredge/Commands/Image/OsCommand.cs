using ICSharpCode.SharpZipLib.Tar;
using Newtonsoft.Json;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using Valleysoft.DockerRegistryClient;
using Valleysoft.DockerRegistryClient.Models.Manifests;
using ImageConfig = Valleysoft.DockerRegistryClient.Models.Images.Image;

namespace Valleysoft.Dredge.Commands.Image;

public partial class OsCommand : RegistryCommandBase<OsOptions>
{
    private static readonly Regex osReleaseRegex = OsReleaseRegex();

    public OsCommand(IDockerRegistryClientFactory dockerRegistryClientFactory)
        : base("os", "Gets OS info about the container image", dockerRegistryClientFactory)
    {
    }

    protected override Task ExecuteAsync()
    {
        ImageName imageName = ImageName.Parse(Options.Image);
        return CommandHelper.ExecuteCommandAsync(imageName.Registry, async () =>
        {
            using IDockerRegistryClient client = await DockerRegistryClientFactory.GetClientAsync(imageName.Registry);
            IImageManifest manifest = (await ManifestHelper.GetResolvedManifestAsync(client, imageName, Options)).Manifest;

            string? configDigest = (manifest.Config?.Digest) ?? throw new NotSupportedException($"Could not resolve the image config digest of '{Options.Image}'.");
            ImageConfig imageConfig = await client.Blobs.GetImageAsync(imageName.Repo, configDigest);

            IDescriptor baseLayer = manifest.Layers.First();
            if (baseLayer.Digest is null)
            {
                throw new Exception($"No digest was found for the base layer of '{Options.Image}'.");
            }

            object? osInfo;
            if (imageConfig.Os.Equals("windows", StringComparison.OrdinalIgnoreCase))
            {
                var windowsOsInfo = await GetWindowsOsInfoAsync(imageConfig, baseLayer.Digest, DockerRegistryClientFactory);
                osInfo = windowsOsInfo?.Info;
            }
            else
            {
                osInfo = await GetLinuxOsInfoAsync(client, imageName, baseLayer.Digest);
            }

            if (osInfo is null)
            {
                throw new Exception("Unable to derive OS information from the image.");
            }

            string output = JsonConvert.SerializeObject(osInfo, JsonHelper.SettingsNoCamelCase);
            Console.Out.WriteLine(output);
        });
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
                (osReleaseRegex.IsMatch(entry.Name)))
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

    internal static async Task<(WindowsOsInfo Info, string Repo)?> GetWindowsOsInfoAsync(ImageConfig imageConfig, string baseLayerDigest,
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

    [GeneratedRegex(@"(\./)?(etc|usr/lib)/os-release")]
    private static partial Regex OsReleaseRegex();
}
