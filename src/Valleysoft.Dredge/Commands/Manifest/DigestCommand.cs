using Valleysoft.DockerRegistryClient;

namespace Valleysoft.Dredge.Commands.Manifest;

public class DigestCommand : RegistryCommandBase<DigestOptions>
{
    public DigestCommand(IDockerRegistryClientFactory dockerRegistryClientFactory, TextWriter? output = null)
        : base("digest", "Queries the digest of a manifest", dockerRegistryClientFactory, output)
    {
    }

    protected override Task ExecuteAsync(CancellationToken cancellationToken)
    {
        ImageName imageName = ImageName.Parse(Options.Image);
        return ExecuteCommandAsync(imageName.Registry, cancellationToken, async ct =>
        {
            using IDockerRegistryClient client = await DockerRegistryClientFactory.GetClientAsync(imageName.Registry);

            string digest = await client.Manifests.GetDigestAsync(
                imageName.Repo, (imageName.Tag ?? imageName.Digest)!, ct);

            Output.WriteLine(digest);
        });
    }
}
