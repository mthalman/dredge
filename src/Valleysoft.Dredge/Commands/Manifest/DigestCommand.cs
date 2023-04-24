using Valleysoft.DockerRegistryClient;
using Valleysoft.Dredge.Core;

namespace Valleysoft.Dredge.Commands.Manifest;

public class DigestCommand : RegistryCommandBase<DigestOptions>
{
    public DigestCommand(IDockerRegistryClientFactory dockerRegistryClientFactory)
        : base("digest", "Queries the digest of a manifest", dockerRegistryClientFactory)
    {
    }

    protected override Task ExecuteAsync()
    {
        ImageName imageName = ImageName.Parse(Options.Image);
        return CommandHelper.ExecuteCommandAsync(imageName.Registry, async () =>
        {
            using IDockerRegistryClient client = await DockerRegistryClientFactory.GetClientAsync(imageName.Registry);

            string digest = await client.Manifests.GetDigestAsync(imageName.Repo, (imageName.Tag ?? imageName.Digest)!);

            Console.Out.WriteLine(digest);
        });
    }
}
