using Valleysoft.DockerRegistryClient.Models.Manifests.Oci;

namespace Valleysoft.Dredge.Commands.Referrer;

public class GetCommand : RegistryCommandBase<GetOptions>
{
    private readonly Stream standardOutput;

    public GetCommand(
        IDockerRegistryClientFactory dockerRegistryClientFactory,
        Stream? standardOutput = null)
        : base(
            "get",
            "Retrieves a payload from an OCI artifact referenced by an image",
            dockerRegistryClientFactory)
    {
        this.standardOutput = standardOutput ?? Console.OpenStandardOutput();
    }

    protected override Task ExecuteAsync(CancellationToken cancellationToken)
    {
        ImageName imageName = ImageName.Parse(Options.Image);
        return ExecuteCommandAsync(imageName.Registry, cancellationToken, async ct =>
        {
            using IDockerRegistryClient client =
                await DockerRegistryClientFactory.GetClientAsync(imageName.Registry);
            ResolvedArtifact artifact = await ArtifactHelper.ResolveAsync(
                client,
                imageName,
                Options.ArtifactDigest,
                ct);
            OciDescriptor payload =
                ArtifactHelper.SelectPayload(artifact.Manifest.Layers, Options.Payload);
            await using Stream payloadStream = await ArtifactHelper.OpenPayloadAsync(
                client,
                imageName.Repo,
                payload,
                ct);

            if (Options.OutputPath is null)
            {
                await payloadStream.CopyToAsync(standardOutput, ct);
                await standardOutput.FlushAsync(ct);
            }
            else
            {
                await using FileStream output = new(
                    Options.OutputPath,
                    FileMode.Create,
                    FileAccess.Write,
                    FileShare.None,
                    bufferSize: 81920,
                    useAsync: true);
                await payloadStream.CopyToAsync(output, ct);
            }
        });
    }
}
