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
