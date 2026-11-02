using Valleysoft.DockerRegistryClient.Models.Manifests;

namespace Valleysoft.Dredge.Commands.Image;

public class InspectCommand : RegistryCommandBase<InspectOptions>
{
    public InspectCommand(IDockerRegistryClientFactory dockerRegistryClientFactory, TextWriter? output = null)
        : base("inspect", "Returns low-level information on a container image", dockerRegistryClientFactory, output)
    {
    }

    protected override Task ExecuteAsync(CancellationToken cancellationToken)
    {
        ImageSourceReference reference = ImageSourceReference.Parse(Options.Image);
        return ExecuteCommandAsync(reference.Registry, cancellationToken, async ct =>
        {
            string content;
            if (reference.IsDaemon)
            {
                await using ResolvedImage source = await OpenImageAsync(Options.Image, Options, ct,
                    requirements: ImageSourceRequirements.Metadata);
                source.Report(Error);
                content = Valleysoft.Dredge.ImageSourceFactory.DecodeConfiguration(source.ConfigurationJson);
            }
            else
            {
                ImageName image = ImageName.Parse(reference.Value);
                using IDockerRegistryClient client = await DockerRegistryClientFactory.GetClientAsync(image.Registry, ct);
                IImageManifest manifest =
                    (await ManifestHelper.GetResolvedManifestAsync(client, image, Options, ct)).Manifest;
                string digest = manifest.Config?.Digest ??
                    throw new NotSupportedException($"Could not resolve the image config digest of '{Options.Image}'.");
                using StreamReader reader = new(await client.Blobs.GetAsync(image.Repo, digest, ct));
                content = await reader.ReadToEndAsync(ct);
            }
            string output = JsonHelper.FormatJson(content) ??
                throw new Exception($"Unable to deserialize content into JSON:\n{content}");
            Output.WriteLine(output);
        });
    }
}
