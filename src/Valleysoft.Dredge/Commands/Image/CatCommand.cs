namespace Valleysoft.Dredge.Commands.Image;

public class CatCommand : RegistryCommandBase<CatOptions>
{
    private readonly Stream standardOutput;
    private readonly IDredgePathProvider paths;

    public CatCommand(
        IDockerRegistryClientFactory dockerRegistryClientFactory,
        Stream? standardOutput = null)
        : this(dockerRegistryClientFactory, standardOutput, new DredgePathProvider())
    {
    }

    internal CatCommand(IDockerRegistryClientFactory dockerRegistryClientFactory,
        Stream? standardOutput, IDredgePathProvider paths)
        : base(
            "cat",
            "Writes a file from an image filesystem to standard output",
            dockerRegistryClientFactory)
    {
        this.standardOutput = standardOutput ?? Console.OpenStandardOutput();
        this.paths = paths;
    }

    protected override Task ExecuteAsync(CancellationToken cancellationToken)
    {
        ImageSourceReference reference = ImageSourceReference.Parse(Options.Image);
        return ExecuteCommandAsync(reference.Registry, cancellationToken, async ct =>
        {
            await using ResolvedImage source = await OpenImageAsync(Options.Image, Options, ct,
                requirements: Options.Layer is null ? ImageSourceRequirements.LinuxLayerContent :
                    ImageSourceRequirements.LinuxFileSystem);
            source.Report(Error);
            LayerIndexOption.ValidateLayer(
                Options.Layer, source.Resolved.Manifest.Layers.Length);
            await using LayerStore store = LayerStore.Create(paths);
            await using ImageFileSystem fileSystem =
                await ImageFileSystem.CreateAsync(
                    source, ct, store,
                    contentPath: Options.Layer is null ? Options.Path : null,
                    maxLayerIndex: Options.Layer);
            await CopyFileAsync(fileSystem, ct);
        });
    }

    private async Task CopyFileAsync(ImageFileSystem fileSystem, CancellationToken cancellationToken)
    {
        await fileSystem.CopyFileToAsync(Options.Path, standardOutput, cancellationToken);
        await standardOutput.FlushAsync(cancellationToken);
    }
}
