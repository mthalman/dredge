using Valleysoft.DockerRegistryClient;
using Valleysoft.DockerRegistryClient.Models;

namespace Valleysoft.Dredge.Commands.Image;

public class SaveLayersCommand : RegistryCommandBase<SaveLayersOptions>
{
    public SaveLayersCommand(IDockerRegistryClientFactory dockerRegistryClientFactory)
        : base("save-layers", "Saves an image's extracted layers to disk", dockerRegistryClientFactory)
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

            await ImageHelper.SaveImageLayersToDiskAsync(
                DockerRegistryClientFactory,
                Options.Image,
                Options.OutputPath,
                Options.LayerIndex,
                SaveLayersOptions.LayerIndexOptionName,
                Options.NoSquash,
                Options);
        });
    }
}
