using Valleysoft.DockerRegistryClient;

namespace Valleysoft.Dredge;

internal class DockerRegistryClientWrapper : IDockerRegistryClient
{
    private readonly DockerRegistryClient.DockerRegistryClient dockerRegistryClient;

    public DockerRegistryClientWrapper(DockerRegistryClient.DockerRegistryClient dockerRegistryClient)
    {
        this.dockerRegistryClient = dockerRegistryClient;
    }

    public IBlobOperations Blobs => dockerRegistryClient.Blobs;

    public ICatalogOperations Catalog => dockerRegistryClient.Catalog;

    public IManifestOperations Manifests => dockerRegistryClient.Manifests;

    public ITagOperations Tags => dockerRegistryClient.Tags;

    public void Dispose() => dockerRegistryClient.Dispose();
}
