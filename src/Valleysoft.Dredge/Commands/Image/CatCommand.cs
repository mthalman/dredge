namespace Valleysoft.Dredge.Commands.Image;

public class CatCommand : RegistryCommandBase<CatOptions>
{
    private readonly Stream standardOutput;

    public CatCommand(
        IDockerRegistryClientFactory dockerRegistryClientFactory,
        Stream? standardOutput = null)
        : base(
            "cat",
            "Writes a file from an image filesystem to standard output",
            dockerRegistryClientFactory)
    {
        this.standardOutput = standardOutput ?? Console.OpenStandardOutput();
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
            await fileSystem.CopyFileToAsync(Options.Path, standardOutput, ct);
            await standardOutput.FlushAsync(ct);
        });
    }
}
