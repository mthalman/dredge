namespace Valleysoft.Dredge.Commands.Image;

public class ExtractCommand : RegistryCommandBase<ExtractOptions>
{
    private readonly IDredgePathProvider paths;

    public ExtractCommand(IDockerRegistryClientFactory dockerRegistryClientFactory)
        : this(dockerRegistryClientFactory, new DredgePathProvider())
    {
    }

    internal ExtractCommand(IDockerRegistryClientFactory dockerRegistryClientFactory, IDredgePathProvider paths)
        : base(
            "extract",
            "Extracts a file or directory from an image filesystem",
            dockerRegistryClientFactory)
    {
        this.paths = paths;
    }

    protected override Task ExecuteAsync(CancellationToken cancellationToken)
    {
        ImageSourceReference reference = ImageSourceReference.Parse(Options.Image);
        return ExecuteCommandAsync(reference.Registry, cancellationToken, async ct =>
        {
            await using ResolvedImage source = await OpenImageAsync(Options.Image, Options, ct,
                requirements: Options.Path.Trim('/', '\\').Length > 0 ?
                    ImageSourceRequirements.LinuxLayerContent : ImageSourceRequirements.LinuxFileSystem);
            source.Report(Error);
            await using LayerStore store = LayerStore.Create(paths);
            await using ImageFileSystem fileSystem =
                await ImageFileSystem.CreateAsync(source, ct, store, extractionPath: Options.Path);
            await fileSystem.ExtractAsync(Options.Path, Options.OutputPath, ct);
        });
    }
}
