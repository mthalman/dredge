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
        ImageName imageName = ImageName.Parse(Options.Image);
        return ExecuteCommandAsync(imageName.Registry, cancellationToken, async ct =>
        {
            using IDockerRegistryClient client =
                await DockerRegistryClientFactory.GetClientAsync(imageName.Registry, ct);
            await using LayerStore store = LayerStore.Create(paths);
            await using ImageFileSystem fileSystem =
                await ImageFileSystem.CreateAsync(client, imageName, Options, ct, store, extractionPath: Options.Path);
            await fileSystem.ExtractAsync(Options.Path, Options.OutputPath, ct);
        });
    }
}
