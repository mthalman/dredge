﻿using Valleysoft.DockerRegistryClient.Models.Manifests;

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
            IImageManifest manifest = (await ManifestHelper.GetResolvedManifestAsync(client, imageName, Options)).Manifest;
            string? digest = (manifest.Config?.Digest) ?? throw new NotSupportedException($"Could not resolve the image config digest of '{Options.Image}'.");
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
