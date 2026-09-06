using System.Formats.Tar;
using System.IO.Compression;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Valleysoft.DockerRegistryClient;

namespace Valleysoft.Dredge.Tests;

public class RegistryFixture : IAsyncLifetime
{
    protected const ushort RegistryPort = 5000;
    private readonly Lazy<Task> initialization;
    private readonly Func<string, Task<RegistryInstance>> registryStarter;
    private IAsyncDisposable? container;
    private string? fixtureDirectory;

    public RegistryFixture()
        : this(StartRegistryContainerAsync)
    {
    }

    internal RegistryFixture(Func<string, Task<RegistryInstance>> registryStarter)
    {
        this.registryStarter = registryStarter;
        initialization = new(StartRegistryAsync);
    }

    public string Registry { get; private set; } = null!;
    public Uri BaseUri { get; private set; } = null!;

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public Task EnsureInitializedAsync() => initialization.Value;

    private async Task StartRegistryAsync()
    {
        fixtureDirectory = Path.Combine(
            Path.GetTempPath(),
            $"dredge-registry-{Guid.NewGuid():N}");
        Directory.CreateDirectory(fixtureDirectory);
        string configPath = Path.Combine(fixtureDirectory, "config.json");
        await File.WriteAllTextAsync(
            configPath,
            """
            {
              "distSpecVersion": "1.1.1",
              "storage": {
                "rootDirectory": "/var/lib/registry"
              },
              "http": {
                "address": "0.0.0.0",
                "port": "5000"
              },
              "log": {
                "level": "warn"
              }
            }
            """);

        RegistryInstance instance = await registryStarter(configPath);
        container = instance.Container;
        Registry = instance.Registry;
        BaseUri = new Uri($"http://{Registry}/");
    }

    private static Task<RegistryInstance> StartRegistryContainerAsync(string configPath) =>
        StartContainerAsync(
            new ContainerBuilder("registry:3.1.1")
                .WithPortBinding(RegistryPort, true)
                .WithWaitStrategy(
                    Wait.ForUnixContainer().UntilHttpRequestIsSucceeded(
                        request => request.ForPort(RegistryPort).ForPath("/v2/")))
                .Build());

    internal static Task<RegistryInstance> StartZotContainerAsync(string configPath) =>
        StartContainerAsync(
            new ContainerBuilder("ghcr.io/project-zot/zot:v2.1.18")
                .WithPortBinding(RegistryPort, true)
                .WithResourceMapping(configPath, "/etc/zot/")
                .WithWaitStrategy(
                    Wait.ForUnixContainer().UntilHttpRequestIsSucceeded(
                        request => request.ForPort(RegistryPort).ForPath("/v2/")))
                .Build());

    internal static async Task<RegistryInstance> StartContainerAsync(IContainer container)
    {
        try
        {
            await container.StartAsync();
            return new(
                container,
                $"{container.Hostname}:{container.GetMappedPublicPort(RegistryPort)}");
        }
        catch
        {
            await container.DisposeAsync();
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (initialization.IsValueCreated && !initialization.Value.IsCompleted)
        {
            await initialization.Value.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }

        if (container is not null)
        {
            await container.DisposeAsync();
        }
        if (fixtureDirectory is not null && Directory.Exists(fixtureDirectory))
        {
            Directory.Delete(fixtureDirectory, recursive: true);
        }
    }

    internal sealed record RegistryInstance(
        IAsyncDisposable Container,
        string Registry);

    public RegistryClient CreateClient() => new(BaseUri.AbsoluteUri);

    public IDockerRegistryClientFactory CreateClientFactory() =>
        new RegistryClientFactory(BaseUri);

    public string GetRepositoryName(string testName) =>
        $"integration/{testName.ToLowerInvariant().Replace('_', '-')}-{Guid.NewGuid():N}";

    public async Task<BlobSeed> UploadBlobAsync(string repository, byte[] content)
    {
        string digest = GetDigest(content);
        using HttpClient client = new() { BaseAddress = BaseUri };
        using ByteArrayContent requestContent = new(content);
        requestContent.Headers.ContentType = new("application/octet-stream");
        using HttpResponseMessage response = await client.PostAsync(
            $"v2/{repository}/blobs/uploads/?digest={Uri.EscapeDataString(digest)}",
            requestContent,
            TestContext.Current.CancellationToken);
        if (response.StatusCode == System.Net.HttpStatusCode.Accepted)
        {
            Uri uploadLocation = response.Headers.Location ??
                throw new InvalidDataException(
                    "Registry accepted the blob upload without returning an upload location.");
            UriBuilder finalLocation = new(new Uri(BaseUri, uploadLocation));
            string existingQuery = finalLocation.Query.TrimStart('?');
            string digestQuery = $"digest={Uri.EscapeDataString(digest)}";
            finalLocation.Query = string.IsNullOrEmpty(existingQuery)
                ? digestQuery
                : $"{existingQuery}&{digestQuery}";
            using ByteArrayContent finalContent = new(content);
            finalContent.Headers.ContentType = new("application/octet-stream");
            using HttpResponseMessage finalResponse = await client.PutAsync(
                finalLocation.Uri,
                finalContent,
                TestContext.Current.CancellationToken);
            finalResponse.EnsureSuccessStatusCode();
        }
        else
        {
            response.EnsureSuccessStatusCode();
        }
        return new BlobSeed(digest, content.LongLength);
    }

    public async Task<LayerSeed> UploadLayerAsync(
        string repository,
        params LayerEntry[] entries)
    {
        using MemoryStream tar = new();
        using (TarWriter writer = new(tar, TarEntryFormat.Pax, leaveOpen: true))
        {
            foreach (LayerEntry definition in entries)
            {
                PaxTarEntry entry = new(definition.Type, definition.Path)
                {
                    Mode = (UnixFileMode)Convert.ToInt32("755", 8),
                    ModificationTime = DateTimeOffset.FromUnixTimeSeconds(1)
                };
                if (definition.LinkTarget is not null)
                {
                    entry.LinkName = definition.LinkTarget;
                }
                if (definition.Content is not null)
                {
                    entry.DataStream = new MemoryStream(definition.Content);
                }
                writer.WriteEntry(entry);
            }
        }

        byte[] tarBytes = tar.ToArray();
        using MemoryStream compressed = new();
        using (GZipStream gzip = new(compressed, CompressionLevel.SmallestSize, leaveOpen: true))
        {
            await gzip.WriteAsync(tarBytes, TestContext.Current.CancellationToken);
        }

        BlobSeed blob = await UploadBlobAsync(repository, compressed.ToArray());
        string diffId = GetDigest(tarBytes);
        return new LayerSeed(blob.Digest, blob.Size, diffId);
    }

    public async Task<ImageSeed> PutImageAsync(
        string repository,
        string reference,
        IReadOnlyList<LayerSeed> layers,
        string architecture = "amd64",
        string os = "linux",
        object? config = null,
        object[]? history = null)
    {
        object imageConfig = new
        {
            architecture,
            os,
            config = config ?? new
            {
                Env = new[] { "APP_ENV=integration" },
                Labels = new Dictionary<string, string> { ["test"] = "true" }
            },
            rootfs = new
            {
                type = "layers",
                diff_ids = layers.Select(layer => layer.DiffId).ToArray()
            },
            history = history ?? layers
                .Select((_, index) => (object)new
                {
                    created_by = $"/bin/sh -c echo layer-{index}",
                    empty_layer = false
                })
                .ToArray()
        };
        BlobSeed configBlob = await UploadBlobAsync(
            repository,
            JsonSerializer.SerializeToUtf8Bytes(imageConfig));
        object manifest = new
        {
            schemaVersion = 2,
            mediaType = ManifestMediaTypes.OciManifestSchema1,
            config = new
            {
                mediaType = "application/vnd.oci.image.config.v1+json",
                size = configBlob.Size,
                digest = configBlob.Digest
            },
            layers = layers.Select(layer => new
            {
                mediaType = "application/vnd.oci.image.layer.v1.tar+gzip",
                size = layer.Size,
                digest = layer.Digest
            })
        };
        ManifestSeed manifestSeed = await PutManifestSeedAsync(
            repository,
            reference,
            ManifestMediaTypes.OciManifestSchema1,
            manifest);
        return new ImageSeed(repository, reference, manifestSeed, configBlob, layers);
    }

    public Task<ManifestSeed> PutIndexAsync(
        string repository,
        string reference,
        params (ImageSeed Image, string Os, string Architecture)[] images) =>
        PutIndexAsync(
            repository,
            reference,
            images
                .Select(item => new PlatformImageSeed(
                    item.Image,
                    item.Os,
                    item.Architecture))
                .ToArray());

    public Task<ManifestSeed> PutIndexAsync(
        string repository,
        string reference,
        params PlatformImageSeed[] images) =>
        PutManifestSeedAsync(
            repository,
            reference,
            ManifestMediaTypes.OciImageIndex1,
            new
            {
                schemaVersion = 2,
                mediaType = ManifestMediaTypes.OciImageIndex1,
                manifests = images.Select(item => new
                {
                    mediaType = ManifestMediaTypes.OciManifestSchema1,
                    size = item.Image.Manifest.Size,
                    digest = item.Image.Manifest.Digest,
                    platform = CreatePlatform(item)
                })
            });

    private static Dictionary<string, string> CreatePlatform(PlatformImageSeed item)
    {
        Dictionary<string, string> platform = new()
        {
            ["os"] = item.Os,
            ["architecture"] = item.Architecture
        };
        if (item.OsVersion is not null)
        {
            platform["os.version"] = item.OsVersion;
        }
        return platform;
    }

    public async Task<ArtifactSeed> PutArtifactAsync(
        ImageSeed subject,
        string reference,
        string artifactType,
        string payloadMediaType,
        byte[] payload)
    {
        BlobSeed emptyConfig = await UploadBlobAsync(subject.Repository, Encoding.UTF8.GetBytes("{}"));
        BlobSeed payloadBlob = await UploadBlobAsync(subject.Repository, payload);
        object manifest = new
        {
            schemaVersion = 2,
            mediaType = ManifestMediaTypes.OciManifestSchema1,
            artifactType,
            config = new
            {
                mediaType = "application/vnd.oci.empty.v1+json",
                size = emptyConfig.Size,
                digest = emptyConfig.Digest
            },
            layers = new[]
            {
                new
                {
                    mediaType = payloadMediaType,
                    size = payloadBlob.Size,
                    digest = payloadBlob.Digest
                }
            },
            subject = new
            {
                mediaType = ManifestMediaTypes.OciManifestSchema1,
                size = subject.Manifest.Size,
                digest = subject.Manifest.Digest
            }
        };
        ManifestSeed artifact = await PutManifestSeedAsync(
            subject.Repository,
            reference,
            ManifestMediaTypes.OciManifestSchema1,
            manifest);
        return new ArtifactSeed(artifact, payloadBlob, artifactType);
    }

    public async Task<string> PutManifestAsync(string repository, string reference, object manifest)
    {
        ManifestSeed seed = await PutManifestSeedAsync(
            repository,
            reference,
            ManifestMediaTypes.OciManifestSchema1,
            manifest);
        return seed.Digest;
    }

    private async Task<ManifestSeed> PutManifestSeedAsync(
        string repository,
        string reference,
        string mediaType,
        object manifest)
    {
        string json = JsonSerializer.Serialize(manifest);
        using HttpClient client = new() { BaseAddress = BaseUri };
        using HttpRequestMessage request = new(
            HttpMethod.Put,
            $"v2/{repository}/manifests/{reference}")
        {
            Content = new ByteArrayContent(Encoding.UTF8.GetBytes(json))
        };
        request.Content.Headers.ContentType = new(mediaType);

        using HttpResponseMessage response = await client.SendAsync(
            request,
            TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();

        return new(
            response.Headers.GetValues("Docker-Content-Digest").Single(),
            Encoding.UTF8.GetByteCount(json));
    }

    private static string GetDigest(byte[] content) =>
        $"sha256:{Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant()}";

    private sealed class RegistryClientFactory(Uri baseUri) : IDockerRegistryClientFactory
    {
        public Task<IDockerRegistryClient> GetClientAsync(string? registry)
        {
            Assert.Equal(baseUri.Authority, registry);
            IDockerRegistryClient client = new DockerRegistryClientWrapper(
                new RegistryClient(baseUri.AbsoluteUri));
            return Task.FromResult(client);
        }
    }
}

public sealed class ZotRegistryFixture : RegistryFixture
{
    public ZotRegistryFixture()
        : base(StartZotContainerAsync)
    {
    }
}

public sealed class AuthenticatedRegistryFixture : RegistryFixture
{
    public const string Username = "test-user";
    public const string Password = "test-password";

    public AuthenticatedRegistryFixture()
        : base(StartAuthenticatedRegistryAsync)
    {
    }

    private static async Task<RegistryInstance> StartAuthenticatedRegistryAsync(string configPath)
    {
        string authDirectory = Path.GetDirectoryName(configPath)!;
        string htpasswdPath = Path.Combine(authDirectory, "htpasswd");
        await File.WriteAllTextAsync(
            htpasswdPath,
            $"{Username}:$2b$05$ncBkeU8pzdoTwEBH.amKf.lBLtR4OMYznHTMD/f4iKEBiQnIwhW8m");

        IContainer container = new ContainerBuilder("registry:3.1.1")
            .WithPortBinding(RegistryPort, true)
            .WithResourceMapping(htpasswdPath, "/auth/htpasswd")
            .WithEnvironment("REGISTRY_AUTH", "htpasswd")
            .WithEnvironment("REGISTRY_AUTH_HTPASSWD_REALM", "Dredge Test Registry")
            .WithEnvironment("REGISTRY_AUTH_HTPASSWD_PATH", "/auth/htpasswd/htpasswd")
            .WithWaitStrategy(
                Wait.ForUnixContainer().UntilInternalTcpPortIsAvailable(RegistryPort))
            .Build();
        return await StartContainerAsync(container);
    }
}

public sealed record BlobSeed(string Digest, long Size);

public sealed record LayerSeed(string Digest, long Size, string DiffId);

public sealed record ManifestSeed(string Digest, long Size);

public sealed record PlatformImageSeed(
    ImageSeed Image,
    string Os,
    string Architecture,
    string? OsVersion = null);

public sealed record ImageSeed(
    string Repository,
    string Reference,
    ManifestSeed Manifest,
    BlobSeed Config,
    IReadOnlyList<LayerSeed> Layers);

public sealed record ArtifactSeed(
    ManifestSeed Manifest,
    BlobSeed Payload,
    string ArtifactType);

public sealed record LayerEntry(
    TarEntryType Type,
    string Path,
    byte[]? Content,
    string? LinkTarget)
{
    public static LayerEntry File(string path, string content) =>
        File(path, Encoding.UTF8.GetBytes(content));

    public static LayerEntry File(string path, byte[] content) =>
        new(TarEntryType.RegularFile, path, content, null);

    public static LayerEntry Directory(string path) =>
        new(TarEntryType.Directory, path, null, null);

    public static LayerEntry SymbolicLink(string path, string target) =>
        new(TarEntryType.SymbolicLink, path, null, target);
}
