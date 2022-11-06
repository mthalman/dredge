using Newtonsoft.Json;
using System.CommandLine;
using Valleysoft.DockerRegistryClient;
using Valleysoft.DockerRegistryClient.Models;

namespace Valleysoft.Dredge;

public class ManifestCommand : Command
{
    public ManifestCommand() : base("manifest", "Commands related to manifests")
    {
        AddCommand(new GetCommand());
        AddCommand(new DigestCommand());
        AddCommand(new ResolveCommand());
    }

    private class GetCommand : Command
    {
        public GetCommand() : base("get", "Queries a manifest")
        {
            Argument<string> imageArg = new("name", "Name of the manifest (<name>, <name>:<tag>, or <name>@<digest>)");
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
        public DigestCommand() : base("digest", "Queries the digest of a manifest")
        {
            Argument<string> imageArg = new("name", "Name of the manifest (<name> or <name>:<tag>)");
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

    private class ResolveCommand : Command
    {
        private const string OsOptionName = "--os";
        private const string OsVersionOptionName = "--os-version";
        private const string ArchOptionName = "--arch";

        public ResolveCommand() : base("resolve", "Resolves a manifest to a target platform's fully-qualified image digest")
        {
            Argument<string> imageArg = new("name", "Name of the manifest (<name>, <name>:<tag>, or <name>@<digest>)");
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
                        .SingleOrDefault(manifest =>
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

                ImageName fullyQualifiedDigest = new(imageName.Registry, imageName.Repo, tag: null, manifestInfo.DockerContentDigest);

                Console.Out.WriteLine(fullyQualifiedDigest.ToString());
            });
        }
    }
}
