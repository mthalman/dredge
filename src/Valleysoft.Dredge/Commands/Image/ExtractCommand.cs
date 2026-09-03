namespace Valleysoft.Dredge.Commands.Image;

public class ExtractCommand : RegistryCommandBase<ExtractOptions>
{
    public ExtractCommand(IDockerRegistryClientFactory dockerRegistryClientFactory)
        : base(
            "extract",
            "Extracts a file or directory from an image filesystem",
            dockerRegistryClientFactory)
    {
    }

    protected override Task ExecuteAsync(CancellationToken cancellationToken)
    {
        ImageName imageName = ImageName.Parse(Options.Image);
        return ExecuteCommandAsync(imageName.Registry, cancellationToken, async ct =>
        {
            using IDockerRegistryClient client =
                await DockerRegistryClientFactory.GetClientAsync(imageName.Registry);
            ImageFileSystem fileSystem =
                await ImageFileSystem.CreateAsync(client, imageName, Options, ct);
            await fileSystem.ExtractAsync(Options.Path, Options.OutputPath, ct);
        });
    }
}
