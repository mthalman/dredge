using System.CommandLine;
using Valleysoft.DockerRegistryClient;
using Valleysoft.DockerRegistryClient.Models;

namespace Valleysoft.Dredge.Commands.Manifest;

public class ResolveCommand : CommandWithOptions<ResolveOptions>
{
    public ResolveCommand(IDockerRegistryClientFactory dockerRegistryClientFactory)
        : base("resolve", "Resolves a manifest to a target platform's fully-qualified image digest", dockerRegistryClientFactory)
    {
    }

    protected override Task ExecuteAsync()
    {
        ImageName imageName = ImageName.Parse(Options.Image);
        return CommandHelper.ExecuteCommandAsync(imageName.Registry, async () =>
        {
            using IDockerRegistryClient client = await DockerRegistryClientFactory.GetClientAsync(imageName.Registry);
            ManifestInfo manifestInfo = await client.Manifests.GetAsync(imageName.Repo, (imageName.Tag ?? imageName.Digest)!);

            if (manifestInfo.Manifest is ManifestList manifestList)
            {
                ManifestReference? manifestRef = manifestList.Manifests
                    .SingleOrDefault(manifest =>
                        manifest.Platform?.Os == Options.Os &&
                        manifest.Platform?.OsVersion == Options.OsVersion &&
                        manifest.Platform?.Architecture == Options.Architecture);
                if (manifestRef is null)
                {
                    throw new Exception(
                        $"Unable to resolve the manifest list tag to a matching platform. Run \"dredge manifest get\" to view the underlying manifests of this tag. Use {ResolveOptions.OsOptionName}, {ResolveOptions.ArchOptionName}, and {ResolveOptions.OsVersionOptionName} (Windows only) to specify the target platform to match.");
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
