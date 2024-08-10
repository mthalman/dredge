using Valleysoft.DockerRegistryClient.Models.Manifests;

namespace Valleysoft.Dredge.Commands.Manifest;

public class ResolveCommand : RegistryCommandBase<SetOptions>
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
            ManifestInfo manifestInfo = (await ManifestHelper.GetResolvedManifestAsync(client, imageName, Options)).ManifestInfo;
            ImageName fullyQualifiedDigest = new(imageName.Registry, imageName.Repo, tag: null, manifestInfo.DockerContentDigest);

            Console.Out.WriteLine(fullyQualifiedDigest.ToString());
        });
    }
}
