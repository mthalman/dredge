using Valleysoft.DockerRegistryClient;

namespace Valleysoft.Dredge.Core;

public interface IDockerRegistryClient : IDisposable
{
    IBlobOperations Blobs { get; }
    ICatalogOperations Catalog { get; }
    IManifestOperations Manifests { get; }
    ITagOperations Tags { get; }
}
