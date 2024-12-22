using Valleysoft.DockerRegistryClient;

namespace Valleysoft.Dredge;

public interface IDockerRegistryClient : IDisposable
{
    IBlobOperations Blobs { get; }
    ICatalogOperations Catalog { get; }
    IManifestOperations Manifests { get; }
    IReferrerOperations Referrers { get; }
    ITagOperations Tags { get; }
}
