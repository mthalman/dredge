using ICSharpCode.SharpZipLib.Tar;
using Newtonsoft.Json;
using System.CommandLine;
using System.IO.Compression;
using System.Text;
using Valleysoft.DockerRegistryClient;
using Valleysoft.DockerRegistryClient.Models;

namespace Valleysoft.Dredge;

public class ImageCommand : Command
{
    public ImageCommand() : base("image", "Commands related to container images")
    {
        AddCommand(new InspectCommand());
        AddCommand(new OsCommand());
    }

    private static DockerManifestV2 GetManifest(string image, ManifestInfo manifestInfo)
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

    private class InspectCommand : Command
    {
        public InspectCommand() : base("inspect", "Return low-level information on a container image")
        {
            Argument<string> imageArg = new("image", "Name of the container image (<image>, <image>:<tag>, or <image>@<digest>)");
            AddArgument(imageArg);

            this.SetHandler(ExecuteAsync, imageArg);
        }

        private Task ExecuteAsync(string image)
        {
            ImageName imageName = ImageName.Parse(image);
            return CommandHelper.ExecuteCommandAsync(imageName.Registry, async () =>
            {
                using DockerRegistryClient.DockerRegistryClient client = await CommandHelper.GetRegistryClientAsync(imageName.Registry);
                ManifestInfo manifestInfo = await client.Manifests.GetAsync(imageName.Repo, (imageName.Tag ?? imageName.Digest)!);

                DockerManifestV2 manifest = GetManifest(image, manifestInfo);
                string? digest = manifest.Config?.Digest;
                if (digest is null)
                {
                    throw new NotSupportedException($"Could not resolve the image config digest of '{image}'.");
                }

                Stream blob = await client.Blobs.GetAsync(imageName.Repo, digest);
                using StreamReader reader = new(blob);
                string content = await reader.ReadToEndAsync();
                object json = JsonConvert.DeserializeObject(content);
                string output = JsonConvert.SerializeObject(json, Formatting.Indented);
                Console.Out.WriteLine(output);
            });
        }
    }

    private class OsCommand : Command
    {
        public OsCommand() : base("os", "Gets OS info about the container image")
        {
            Argument<string> imageArg = new("image", "Name of the container image (<image>, <image>:<tag>, or <image>@<digest>)");
            AddArgument(imageArg);
            this.SetHandler(ExecuteAsync, imageArg);
        }

        private Task ExecuteAsync(string image)
        {
            ImageName imageName = ImageName.Parse(image);
            return CommandHelper.ExecuteCommandAsync(imageName.Registry, async () =>
            {
                using DockerRegistryClient.DockerRegistryClient client = await CommandHelper.GetRegistryClientAsync(imageName.Registry);
                ManifestInfo manifestInfo = await client.Manifests.GetAsync(imageName.Repo, (imageName.Tag ?? imageName.Digest)!);

                DockerManifestV2 manifest = GetManifest(image, manifestInfo);

                string? configDigest = manifest.Config?.Digest;
                if (configDigest is null)
                {
                    throw new NotSupportedException($"Could not resolve the image config digest of '{image}'.");
                }

                Image imageConfig = await client.Blobs.GetImageAsync(imageName.Repo, configDigest);

                ManifestLayer baseLayer = manifest.Layers.First();
                if (baseLayer.Digest is null)
                {
                    throw new Exception($"No digest was found for the base layer of '{image}'.");
                }

                object? osInfo;
                if (imageConfig.Os.Equals("windows", StringComparison.OrdinalIgnoreCase))
                {
                    osInfo = await GetWindowsOsInfoAsync(imageConfig, baseLayer.Digest);
                }
                else
                {
                    osInfo = await GetLinuxOsInfoAsync(client, imageName, baseLayer.Digest);
                }

                if (osInfo is null)
                {
                    throw new Exception("Unable to derive OS information from the image.");
                }

                string output = JsonConvert.SerializeObject(osInfo, new JsonSerializerSettings
                {
                    NullValueHandling = NullValueHandling.Ignore,
                    Formatting = Formatting.Indented
                });
                Console.Out.WriteLine(output);
            });
        }

        private static async Task<LinuxOsInfo?> GetLinuxOsInfoAsync(DockerRegistryClient.DockerRegistryClient client, ImageName imageName, string baseLayerDigest)
        {
            Stream blobStream = await client.Blobs.GetAsync(imageName.Repo, baseLayerDigest);
            GZipStream gZipStream = new(blobStream, CompressionMode.Decompress);

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

        private static async Task<WindowsOsInfo?> GetWindowsOsInfoAsync(Image imageConfig, string baseLayerDigest)
        {
            using DockerRegistryClient.DockerRegistryClient mcrClient =
                await CommandHelper.GetRegistryClientAsync("mcr.microsoft.com");

            if (await mcrClient.Blobs.ExistsAsync("windows/nanoserver", baseLayerDigest))
            {
                return new(WindowsType.NanoServer, imageConfig.OsVersion);
            }
            else if (await mcrClient.Blobs.ExistsAsync("windows/servercore", baseLayerDigest))
            {
                return new(WindowsType.ServerCore, imageConfig.OsVersion);
            }
            else if (await mcrClient.Blobs.ExistsAsync("windows/server", baseLayerDigest))
            {
                return new(WindowsType.Server, imageConfig.OsVersion);
            }
            else if (await mcrClient.Blobs.ExistsAsync("windows", baseLayerDigest))
            {
                return new(WindowsType.Windows, imageConfig.OsVersion);
            }
            else
            {
                return null;
            }
        }
    }
}
