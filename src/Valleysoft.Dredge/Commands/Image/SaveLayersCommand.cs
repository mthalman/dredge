using Valleysoft.DockerRegistryClient.Models.Manifests;

namespace Valleysoft.Dredge.Commands.Image;

public class SaveLayersCommand : RegistryCommandBase<SaveLayersOptions>
{
    private readonly IDredgePathProvider pathProvider;

    public SaveLayersCommand(IDockerRegistryClientFactory dockerRegistryClientFactory)
        : this(dockerRegistryClientFactory, new DredgePathProvider())
    {
    }

    internal SaveLayersCommand(
        IDockerRegistryClientFactory dockerRegistryClientFactory,
        IDredgePathProvider pathProvider)
        : base("save-layers", "Saves an image's extracted layers to disk", dockerRegistryClientFactory)
    {
        this.pathProvider = pathProvider;
    }

    protected override Task ExecuteAsync(CancellationToken cancellationToken)
    {
        ImageSourceReference reference = ImageSourceReference.Parse(Options.Image);
        return ExecuteCommandAsync(reference.Registry, cancellationToken, async ct =>
        {
            ValidateOutputPath();

            await using ResolvedImage? source = reference.IsDaemon
                ? await OpenImageAsync(Options.Image, Options, ct,
                    requirements: ImageSourceRequirements.LinuxLayerContent)
                : null;
            source?.Report(Error);
            await ImageHelper.SaveImageLayersToDiskAsync(
                DockerRegistryClientFactory,
                Options.Image,
                Options.OutputPath,
                Options.LayerIndex,
                SaveLayersOptions.LayerIndexOptionName,
                Options.NoSquash,
                Options,
                ct,
                pathProvider,
                overwriteExisting: Options.Force,
                resolvedImage: source);
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
