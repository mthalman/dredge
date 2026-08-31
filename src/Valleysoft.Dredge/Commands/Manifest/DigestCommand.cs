using Valleysoft.DockerRegistryClient;

namespace Valleysoft.Dredge.Commands.Manifest;

public class DigestCommand : RegistryCommandBase<DigestOptions>
{
    public DigestCommand(IDockerRegistryClientFactory dockerRegistryClientFactory, TextWriter? output = null)
        : base("digest", "Queries the digest of a manifest", dockerRegistryClientFactory, output)
    {
    }

    protected override Task ExecuteAsync()
    {
        ImageName imageName = ImageName.Parse(Options.Image);
        return ExecuteCommandAsync(imageName.Registry, async () =>
        {
            using IDockerRegistryClient client = await DockerRegistryClientFactory.GetClientAsync(imageName.Registry);

            string digest = await client.Manifests.GetDigestAsync(imageName.Repo, (imageName.Tag ?? imageName.Digest)!);

            Output.WriteLine(digest);
        });
    }
}
