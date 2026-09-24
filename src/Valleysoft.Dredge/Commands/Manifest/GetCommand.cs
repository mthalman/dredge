using Valleysoft.DockerRegistryClient.Models.Manifests;

namespace Valleysoft.Dredge.Commands.Manifest;

public class GetCommand : RegistryCommandBase<GetOptions>
{
    public GetCommand(IDockerRegistryClientFactory dockerRegistryClientFactory, TextWriter? output = null)
        : base("get", "Queries a manifest", dockerRegistryClientFactory, output)
    {
    }

    protected override Task ExecuteAsync(CancellationToken cancellationToken)
    {
        ImageName imageName = ImageName.Parse(Options.Image);
        return ExecuteCommandAsync(imageName.Registry, cancellationToken, async ct =>
        {
            using IDockerRegistryClient client = await DockerRegistryClientFactory.GetClientAsync(imageName.Registry, ct);

            ManifestInfo manifestInfo = await client.Manifests.GetAsync(
                imageName.Repo, (imageName.Tag ?? imageName.Digest)!, ct);

            if (manifestInfo.Manifest is RawManifest)
            {
                throw new NotSupportedException(
                    $"The image name '{imageName}' has a media type of '{manifestInfo.MediaType}' which is not supported.");
            }

            string output = JsonHelper.Serialize(manifestInfo.Manifest);

            Output.WriteLine(output);
        });
    }
}
