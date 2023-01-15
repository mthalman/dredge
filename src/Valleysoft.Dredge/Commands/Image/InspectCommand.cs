using Newtonsoft.Json;
using Valleysoft.DockerRegistryClient;
using Valleysoft.DockerRegistryClient.Models;

namespace Valleysoft.Dredge.Commands.Image;

public class InspectCommand : CommandWithOptions<InspectOptions>
{
    public InspectCommand(IDockerRegistryClientFactory dockerRegistryClientFactory)
        : base("inspect", "Return low-level information on a container image", dockerRegistryClientFactory)
    {
    }

    protected override Task ExecuteAsync()
    {
        ImageName imageName = ImageName.Parse(Options.Image);
        return CommandHelper.ExecuteCommandAsync(imageName.Registry, async () =>
        {
            using IDockerRegistryClient client = await DockerRegistryClientFactory.GetClientAsync(imageName.Registry);
            ManifestInfo manifestInfo = await ManifestHelper.GetManifestInfoAsync(client, imageName, Options);

            DockerManifestV2 manifest = ManifestHelper.GetManifest(Options.Image, manifestInfo);
            string? digest = manifest.Config?.Digest;
            if (digest is null)
            {
                throw new NotSupportedException($"Could not resolve the image config digest of '{Options.Image}'.");
            }

            Stream blob = await client.Blobs.GetAsync(imageName.Repo, digest);
            using StreamReader reader = new(blob);
            string content = await reader.ReadToEndAsync();
            object json = JsonConvert.DeserializeObject(content);
            string output = JsonConvert.SerializeObject(json, Formatting.Indented);
            Console.Out.WriteLine(output);
        });
    }
}
