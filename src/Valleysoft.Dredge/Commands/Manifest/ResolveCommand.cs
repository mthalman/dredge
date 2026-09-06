using Valleysoft.DockerRegistryClient.Models.Manifests;

namespace Valleysoft.Dredge.Commands.Manifest;

public class ResolveCommand : RegistryCommandBase<SetOptions>
{
    private readonly IAppSettingsStore settingsStore;

    public ResolveCommand(IDockerRegistryClientFactory dockerRegistryClientFactory, TextWriter? output = null)
        : this(dockerRegistryClientFactory, output, new AppSettingsStore())
    {
    }

    internal ResolveCommand(
        IDockerRegistryClientFactory dockerRegistryClientFactory,
        TextWriter? output,
        IAppSettingsStore settingsStore)
        : base("resolve", "Resolves a manifest to a target platform's fully-qualified image digest", dockerRegistryClientFactory, output)
    {
        this.settingsStore = settingsStore;
    }

    protected override Task ExecuteAsync(CancellationToken cancellationToken)
    {
        ImageName imageName = ImageName.Parse(Options.Image);
        return ExecuteCommandAsync(imageName.Registry, cancellationToken, async ct =>
        {
            using IDockerRegistryClient client = await DockerRegistryClientFactory.GetClientAsync(imageName.Registry);
            ManifestInfo manifestInfo =
                (await ManifestHelper.GetResolvedManifestAsync(
                    client,
                    imageName,
                    Options,
                    ct,
                    settingsStore)).ManifestInfo;
            ImageName fullyQualifiedDigest = new(imageName.Registry, imageName.Repo, tag: null, manifestInfo.DockerContentDigest);

            Output.WriteLine(fullyQualifiedDigest.ToString());
        });
    }
}
