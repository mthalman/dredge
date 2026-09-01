using System.Text.Json;

namespace Valleysoft.Dredge.Commands.Referrer;

public class InspectCommand : RegistryCommandBase<InspectOptions>
{
    public InspectCommand(
        IDockerRegistryClientFactory dockerRegistryClientFactory,
        TextWriter? output = null)
        : base(
            "inspect",
            "Inspects an OCI artifact referenced by an image",
            dockerRegistryClientFactory,
            output)
    {
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
            ArtifactInspection inspection =
                await ArtifactInspectionFactory.CreateAsync(client, artifact, ct);

            if (Options.OutputFormat == ArtifactInspectOutput.Json)
            {
                string json = JsonSerializer.Serialize(inspection, ArtifactInspectionJson.Options);
                Output.WriteLine(json);
            }
            else
            {
                ArtifactSummaryWriter.Write(Output, inspection);
            }
        });
    }
}
