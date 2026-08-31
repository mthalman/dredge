using Newtonsoft.Json;
using Valleysoft.DockerRegistryClient.Models.Manifests;

namespace Valleysoft.Dredge.Commands.Image;

public class InspectCommand : RegistryCommandBase<InspectOptions>
{
    public InspectCommand(IDockerRegistryClientFactory dockerRegistryClientFactory)
        : base("inspect", "Return low-level information on a container image", dockerRegistryClientFactory)
    {
    }

    protected override Task ExecuteAsync(CancellationToken cancellationToken)
    {
        ImageName imageName = ImageName.Parse(Options.Image);
        return CommandHelper.ExecuteCommandAsync(imageName.Registry, cancellationToken, async ct =>
        {
            using IDockerRegistryClient client = await DockerRegistryClientFactory.GetClientAsync(imageName.Registry);
            IImageManifest manifest =
                (await ManifestHelper.GetResolvedManifestAsync(client, imageName, Options, ct)).Manifest;
            string? digest = (manifest.Config?.Digest) ??
                throw new NotSupportedException($"Could not resolve the image config digest of '{Options.Image}'.");
            Stream blob = await client.Blobs.GetAsync(imageName.Repo, digest, ct);
            using StreamReader reader = new(blob);
            string content = await reader.ReadToEndAsync(ct);
            object? json = JsonConvert.DeserializeObject(content) ??
                throw new Exception($"Unable to deserialize content into JSON:\n{content}");
            string output = JsonConvert.SerializeObject(json, JsonHelper.Settings);
            Console.Out.WriteLine(output);
        });
    }
}
