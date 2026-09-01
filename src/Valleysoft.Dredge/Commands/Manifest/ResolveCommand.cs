using Valleysoft.DockerRegistryClient.Models.Manifests;

namespace Valleysoft.Dredge.Commands.Manifest;

public class ResolveCommand : RegistryCommandBase<SetOptions>
{
    public ResolveCommand(IDockerRegistryClientFactory dockerRegistryClientFactory, TextWriter? output = null)
        : base("resolve", "Resolves a manifest to a target platform's fully-qualified image digest", dockerRegistryClientFactory, output)
    {
    }

    protected override Task ExecuteAsync(CancellationToken cancellationToken)
    {
        ImageName imageName = ImageName.Parse(Options.Image);
        return ExecuteCommandAsync(imageName.Registry, cancellationToken, async ct =>
        {
            using IDockerRegistryClient client = await DockerRegistryClientFactory.GetClientAsync(imageName.Registry);
            ManifestInfo manifestInfo =
                (await ManifestHelper.GetResolvedManifestAsync(client, imageName, Options, ct)).ManifestInfo;
            ImageName fullyQualifiedDigest = new(imageName.Registry, imageName.Repo, tag: null, manifestInfo.DockerContentDigest);

            Output.WriteLine(fullyQualifiedDigest.ToString());
        });
    }
}
