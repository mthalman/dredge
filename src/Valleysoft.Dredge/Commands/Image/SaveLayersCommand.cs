using Valleysoft.DockerRegistryClient.Models.Manifests;

namespace Valleysoft.Dredge.Commands.Image;

public class SaveLayersCommand : RegistryCommandBase<SaveLayersOptions>
{
    public SaveLayersCommand(IDockerRegistryClientFactory dockerRegistryClientFactory)
        : base("save-layers", "Saves an image's extracted layers to disk", dockerRegistryClientFactory)
    {
    }

    protected override Task ExecuteAsync(CancellationToken cancellationToken)
    {
        ImageName imageName = ImageName.Parse(Options.Image);
        return ExecuteCommandAsync(imageName.Registry, cancellationToken, async ct =>
        {
            ValidateOutputPath();

            using IDockerRegistryClient client = await DockerRegistryClientFactory.GetClientAsync(imageName.Registry);
            IImageManifest manifest =
                (await ManifestHelper.GetResolvedManifestAsync(client, imageName, Options, ct)).Manifest;
            string? digest = (manifest.Config?.Digest) ?? throw new NotSupportedException($"Could not resolve the image config digest of '{Options.Image}'.");
            await ImageHelper.SaveImageLayersToDiskAsync(
                DockerRegistryClientFactory,
                Options.Image,
                Options.OutputPath,
                Options.LayerIndex,
                SaveLayersOptions.LayerIndexOptionName,
                Options.NoSquash,
                Options,
                ct,
                overwriteExisting: Options.Force);
        });
    }

    private void ValidateOutputPath()
    {
        ImageHelper.ValidateDestinationPath(Options.OutputPath);

        if (File.Exists(Options.OutputPath))
        {
            throw new IOException($"Output path '{Options.OutputPath}' is an existing file.");
        }

        if (!Options.Force &&
            Directory.Exists(Options.OutputPath) &&
            Directory.EnumerateFileSystemEntries(Options.OutputPath).Any())
        {
            throw new IOException(
                $"Output directory '{Options.OutputPath}' is not empty. Use '--force' to allow existing content to be overwritten or deleted.");
        }
    }
}
