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
        ImageName imageName = ImageName.Parse(Options.Image);
        return ExecuteCommandAsync(imageName.Registry, cancellationToken, async ct =>
        {
            using IDockerRegistryClient client =
                await DockerRegistryClientFactory.GetClientAsync(imageName.Registry, ct);
            await using LayerStore store = LayerStore.Create(paths);
            await using ImageFileSystem fileSystem =
                await ImageFileSystem.CreateAsync(client, imageName, Options, ct, store, Options.Path);
            await fileSystem.CopyFileToAsync(Options.Path, standardOutput, ct);
            await standardOutput.FlushAsync(ct);
        });
    }
}
