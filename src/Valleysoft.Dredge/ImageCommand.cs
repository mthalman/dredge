using Newtonsoft.Json;
using System.CommandLine;
using Valleysoft.DockerRegistryClient;
using Valleysoft.DockerRegistryClient.Models;

namespace Valleysoft.Dredge;

public class ImageCommand : Command
{
    public ImageCommand() : base("image", "Commands related to container images")
    {
        AddCommand(new InspectCommand());
    }

    private class InspectCommand : Command
    {
        private const string OsOptionName = "--os";
        private const string OsVersionOptionName = "--os-version";
        private const string ArchOptionName = "--arch";

        public InspectCommand() : base("inspect", "Return low-level information on a container image")
        {
            Argument<string> imageArg = new("image", "Name of the container image (<image>, <image>:<tag>, or <image>@<digest>)");
            AddArgument(imageArg);

            Option<string?> osOpt = new(OsOptionName, "Target OS of image (e.g. \"linux\", \"windows\")");
            AddOption(osOpt);

            Option<string?> osVersionOpt = new(OsVersionOptionName, "Target OS version of image (Windows only, e.g. \"10.0.20348.1129\")");
            AddOption(osVersionOpt);

            Option<string?> archOpt = new(ArchOptionName, "Target architecture of image (e.g. \"amd64\", \"arm64\")");
            AddOption(archOpt);

            this.SetHandler(ExecuteAsync, imageArg, osOpt, osVersionOpt, archOpt);
        }

        private Task ExecuteAsync(string image, string? os, string? osVersion, string? arch)
        {
            ImageName imageName = ImageName.Parse(image);
            return CommandHelper.ExecuteCommandAsync(imageName.Registry, async () =>
            {
                using DockerRegistryClient.DockerRegistryClient client = await CommandHelper.GetRegistryClientAsync(imageName.Registry);
                ManifestInfo manifestInfo = await client.Manifests.GetAsync(imageName.Repo, (imageName.Tag ?? imageName.Digest)!);
                
                if (manifestInfo.Manifest is ManifestList manifestList)
                {
                    ManifestReference? manifestRef = manifestList.Manifests
                        .FirstOrDefault(manifest =>
                            manifest.Platform?.Os == os &&
                            manifest.Platform?.OsVersion == osVersion &&
                            manifest.Platform?.Architecture == arch);
                    if (manifestRef is null)
                    {
                        throw new Exception(
                            $"Unable to resolve the manifest list tag to a matching platform. Run \"dredge manifest get\" to view the underlying manifests of this tag. Use {OsOptionName}, {ArchOptionName}, and {OsVersionOptionName} (Windows only) to specify the target platform to match.");
                    }

                    if (manifestRef.Digest is null)
                    {
                        throw new Exception($"Digest of resolved manifest is not set.");
                    }

                    manifestInfo = await client.Manifests.GetAsync(imageName.Repo, manifestRef.Digest);
                }
                
                if (manifestInfo.Manifest is not DockerManifestV2 manifest)
                {
                    throw new NotSupportedException(
                        $"The image name '{image}' has a media type of '{manifestInfo.MediaType}' which is not supported.");
                }

                string? digest = manifest.Config?.Digest;

                if (digest is null)
                {
                    throw new NotSupportedException($"Could not resolve the image config digest of {image}.");
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
}
