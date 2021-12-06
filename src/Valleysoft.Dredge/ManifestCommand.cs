using Newtonsoft.Json;
using System.CommandLine;
using System.CommandLine.Invocation;
using System.CommandLine.IO;
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
            AddArgument(new Argument<string>("image", "Name of the Docker image (<image>, <image>:<tag>, or <image>@<digest>)"));
            Handler = CommandHandler.Create<string, IConsole>(ExecuteAsync);
        }

        private Task ExecuteAsync(string image, IConsole console)
        {
            ImageName imageName = ImageName.Parse(image);
            return CommandHelper.ExecuteCommandAsync(console, imageName.Registry, async () =>
            {
                using DockerRegistryClient.DockerRegistryClient client = await CommandHelper.GetRegistryClientAsync(imageName.Registry);

                ManifestInfo manifestInfo = await client.Manifests.GetAsync(imageName.Repo, (imageName.Tag ?? imageName.Digest)!);

                string output = JsonConvert.SerializeObject(manifestInfo.Manifest, Formatting.Indented);

                console.Out.WriteLine(output);
            });
        }
    }

    private class DigestCommand : Command
    {
        public DigestCommand() : base("digest", "Queries the digest of a Docker manifest")
        {
            AddArgument(new Argument<string>("image", "Name of the Docker image (<image> or <image>:<tag>"));
            Handler = CommandHandler.Create<string, IConsole>(ExecuteAsync);
        }

        private Task ExecuteAsync(string image, IConsole console)
        {
            ImageName imageName = ImageName.Parse(image);
            return CommandHelper.ExecuteCommandAsync(console, imageName.Registry, async () =>
            {
                using DockerRegistryClient.DockerRegistryClient client = await CommandHelper.GetRegistryClientAsync(imageName.Registry);

                string digest = await client.Manifests.GetDigestAsync(imageName.Repo, (imageName.Tag ?? imageName.Digest)!);

                console.Out.WriteLine(digest);
            });
        }
    }
}
