using Newtonsoft.Json;
using System.CommandLine;
using Valleysoft.DockerRegistryClient;
using Valleysoft.DockerRegistryClient.Models;

namespace Valleysoft.Dredge;

public class ManifestCommand : Command
{
    public ManifestCommand() : base("manifest", "Commands related to Docker manifests")
    {
        AddCommand(new GetCommand());
        AddCommand(new DigestCommand());
    }

    private class GetCommand : Command
    {
        public GetCommand() : base("get", "Queries a Docker manifest")
        {
            Argument<string> imageArg = new("image", "Name of the Docker image (<image>, <image>:<tag>, or <image>@<digest>)");
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

                string output = JsonConvert.SerializeObject(manifestInfo.Manifest, Formatting.Indented);

                Console.Out.WriteLine(output);
            });
        }
    }

    private class DigestCommand : Command
    {
        public DigestCommand() : base("digest", "Queries the digest of a Docker manifest")
        {
            Argument<string> imageArg = new("image", "Name of the Docker image (<image> or <image>:<tag>");
            AddArgument(imageArg);
            this.SetHandler(ExecuteAsync, imageArg);
        }

        private Task ExecuteAsync(string image)
        {
            ImageName imageName = ImageName.Parse(image);
            return CommandHelper.ExecuteCommandAsync(imageName.Registry, async () =>
            {
                using DockerRegistryClient.DockerRegistryClient client = await CommandHelper.GetRegistryClientAsync(imageName.Registry);

                string digest = await client.Manifests.GetDigestAsync(imageName.Repo, (imageName.Tag ?? imageName.Digest)!);

                Console.Out.WriteLine(digest);
            });
        }
    }
}
