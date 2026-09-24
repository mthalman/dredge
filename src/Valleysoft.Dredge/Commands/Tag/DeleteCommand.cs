using Valleysoft.DockerRegistryClient;

namespace Valleysoft.Dredge.Commands.Tag;

public class DeleteCommand : RegistryCommandBase<DeleteOptions>
{
    private readonly DeleteConfirmation confirmation;

    public DeleteCommand(IDockerRegistryClientFactory dockerRegistryClientFactory, TextWriter? output = null)
        : this(dockerRegistryClientFactory, output, new DeleteConfirmation())
    {
    }

    internal DeleteCommand(
        IDockerRegistryClientFactory dockerRegistryClientFactory,
        TextWriter? output,
        DeleteConfirmation confirmation)
        : base("delete", "Deletes a tag without deleting its manifest or other tags", dockerRegistryClientFactory, output)
    {
        this.confirmation = confirmation;
    }

    protected override Task ExecuteAsync(CancellationToken cancellationToken)
    {
        ImageName image = ImageName.Parse(Options.Image);
        return ExecuteCommandAsync(image.Registry, cancellationToken, async ct =>
        {
            confirmation.EnsureAvailable(Options.Yes);
            await confirmation.ConfirmAsync(
                $"Delete tag '{image}'? The referenced manifest and other tags will not be deleted.",
                Options.Yes, Error, ct);

            using IDockerRegistryClient client = await DockerRegistryClientFactory.GetClientAsync(image.Registry, ct);
            await client.Manifests.DeleteTagAsync(image.Repo, image.Tag!, ct);
            Output.WriteLine($"Deleted tag '{image}'.");
        });
    }
}
