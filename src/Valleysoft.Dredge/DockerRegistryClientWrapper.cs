﻿using Valleysoft.DockerRegistryClient;

namespace Valleysoft.Dredge;

internal class DockerRegistryClientWrapper : IDockerRegistryClient
{
    private readonly RegistryClient dockerRegistryClient;

    public DockerRegistryClientWrapper(RegistryClient dockerRegistryClient)
    {
        this.dockerRegistryClient = dockerRegistryClient;
    }

    public IBlobOperations Blobs => dockerRegistryClient.Blobs;

    public ICatalogOperations Catalog => dockerRegistryClient.Catalog;

    public IManifestOperations Manifests => dockerRegistryClient.Manifests;

    public ITagOperations Tags => dockerRegistryClient.Tags;

    public IReferrerOperations Referrers => dockerRegistryClient.Referrers;

    public void Dispose() => dockerRegistryClient.Dispose();
}
