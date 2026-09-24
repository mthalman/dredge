using Valleysoft.DockerRegistryClient;
using Valleysoft.DockerRegistryClient.Models.Manifests;

namespace Valleysoft.Dredge.Commands.Manifest;

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
        : base("delete", "Deletes a manifest and all tags pointing to it, without deleting child manifests or layers",
            dockerRegistryClientFactory, output)
    {
        this.confirmation = confirmation;
    }

    protected override Task ExecuteAsync(CancellationToken cancellationToken)
    {
        ImageName image = ImageName.Parse(Options.Image);
        return ExecuteCommandAsync(image.Registry, cancellationToken, async ct =>
        {
            confirmation.EnsureAvailable(Options.Yes);
            ct.ThrowIfCancellationRequested();
            using IDockerRegistryClient client = await DockerRegistryClientFactory.GetClientAsync(image.Registry, ct);

            string digest;
            bool isList = false;
            if (Options.Yes)
            {
                digest = image.Digest ?? await client.Manifests.GetDigestAsync(image.Repo, image.Tag!, ct);
            }
            else
            {
                ManifestInfo info = await client.Manifests.GetAsync(image.Repo, image.Digest ?? image.Tag!, ct);
                digest = image.Digest ?? info.DockerContentDigest;
                isList = info.Manifest is IManifestList;
            }

            ImageName target = new(image.Registry, image.Repo, tag: null, digest);
            string kind = isList ? "manifest list/image index" : "manifest";
            string warning = isList
                ? "All tags pointing to it will be removed, making all platforms unavailable through those references. Child manifests and layers will not be deleted."
                : "All tags pointing to it will be removed. Layers will not be deleted.";
            await confirmation.ConfirmAsync(
                $"Delete {kind} '{target}' (requested '{image}')? {warning}",
                Options.Yes, Error, ct);

            await client.Manifests.DeleteAsync(image.Repo, digest, ct);
            Output.WriteLine($"Deleted manifest '{target}'.");
        });
    }
}
