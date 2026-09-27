using System.Formats.Tar;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Valleysoft.DockerRegistryClient;
using Valleysoft.DockerRegistryClient.Models.Images;
using Valleysoft.DockerRegistryClient.Models.Manifests;
using Valleysoft.DockerRegistryClient.Models.Manifests.Oci;
using Valleysoft.Dredge.Commands;
using Valleysoft.Dredge.Explorer;
using Valleysoft.Dredge.Explorer.Tui;

namespace Valleysoft.Dredge.Tests;

public sealed class ExplorerDefenseHostTests : IAsyncDisposable
{
    private readonly string cachePath = Path.Combine(Directory.GetCurrentDirectory(),
        "host-defense-cache-" + Guid.NewGuid().ToString("N"));
    private readonly List<LayerStore> stores = [];
    private readonly List<ExplorerHost> hosts = [];
    private static readonly ImageName Image = ImageName.Parse("registry.test/repo:current");
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task PackageDiagnosticsAreStructuredAndRetainedAcrossCachedReads()
    {
        TestImage image = await CreateAsync(Archive(
            ("node_modules/good/package.json", Encoding.UTF8.GetBytes("""{"name":"good","version":"1"}""")),
            ("node_modules/bad/package.json", Encoding.UTF8.GetBytes("{")),
            ("node_modules/invalid/package.json", [0xff]),
            ("node_modules/large/package.json", new byte[InstalledPackageReader.MaxPackageManifestBytes + 1])));

        await image.Session.EnsurePackagesAsync(Token);
        InstalledPackageMetadata metadata = image.Session.Packages;
        Assert.Equal(["good"], metadata.Ecosystems[InstalledPackageEcosystem.Npm].Packages.Keys);
        Assert.Equal(["node_modules/bad/package.json", "node_modules/invalid/package.json", "node_modules/large/package.json"],
            metadata.Diagnostics.Select(item => item.Path).Order(StringComparer.Ordinal));
        Assert.All(metadata.Diagnostics, item => Assert.NotEmpty(item.Message));
        await image.Session.EnsurePackagesAsync(Token);
        Assert.Same(metadata, image.Session.Packages);
    }

    private async Task<TestImage> CreateAsync(params byte[][] blobs)
    {
        LayerStore store = new(Path.Combine(cachePath, stores.Count.ToString()));
        stores.Add(store);
        Mock<IDockerRegistryClient> client = new() { DefaultValue = DefaultValue.Mock };
        Dictionary<int, StoredLayerIndex> indexes = [];
        for (int i = 0; i < blobs.Length; i++)
        {
            byte[] blob = blobs[i];
            string digest = LayerCacheTestContext.Digest(blob);
            using MemoryStream input = new(blob);
            indexes.Add(i, new(digest, blob.Length,
                await ImageLayerScanner.ScanAsync(input, new(i, digest), Token)));
            client.Setup(c => c.Blobs.GetRangeAsync(Image.Repo, digest, 0,
                It.IsAny<long?>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => new BlobDownloadResult(new MemoryStream(blob), false, null, null, blob.Length));
        }
        OciImageManifest manifest = new()
        {
            Config = new OciDescriptor { Digest = "sha256:config" },
            Layers = indexes.Values.Select(index => new OciDescriptor
            {
                Digest = index.Digest, Size = index.BlobLength
            }).ToArray()
        };
        ManifestInfo info = new("application/vnd.oci.image.manifest.v1+json",
            LayerCacheTestContext.Digest(JsonSerializer.SerializeToUtf8Bytes(manifest)), manifest);
        client.Setup(c => c.Manifests.GetAsync(Image.Repo, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(info);
        client.Setup(c => c.Blobs.GetAsync(Image.Repo, "sha256:config", It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new MemoryStream(Encoding.UTF8.GetBytes("""{"os":"linux","architecture":"amd64"}""")));
        ExplorerSource source = new()
        {
            Image = Image, Resolved = new(info, manifest),
            Config = new Image { Os = "linux", Architecture = "amd64" },
            Platforms = [], Platform = new("linux", "amd64", null, null)
        };
        ExplorerSession session = await ExplorerSession.CreateAsync(client.Object, source, store, indexes, Token);
        return new(session, source, client, store);
    }

    private ExplorerHost Host(TestImage image, IDockerRegistryClientFactory? factory = null)
    {
        ExplorerHost host = new(image.Client.Object, factory ?? Mock.Of<IDockerRegistryClientFactory>(),
            image.Source, image.Store, ExplorerImage.FromSource(image.Source),
            new ExplorerLayerIndexer(0, (_, _, _) => throw new InvalidOperationException()),
            new ExplorerOptions(null, null, false, ClipboardMode.Off, KeyMap.Default, "", ""));
        host.Session = image.Session;
        hosts.Add(host);
        return host;
    }

    private static byte[] Blob(params (string Path, string Content)[] files) =>
        Archive(files.Select(file => (file.Path, Encoding.UTF8.GetBytes(file.Content))).ToArray());

    private static byte[] Archive(params (string Path, byte[] Content)[] files)
    {
        using MemoryStream result = new();
        using (GZipStream gzip = new(result, CompressionMode.Compress, leaveOpen: true))
        using (TarWriter writer = new(gzip, leaveOpen: true))
        {
            foreach ((string path, byte[] content) in files)
            {
                using MemoryStream data = new(content);
                writer.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, path) { DataStream = data });
            }
        }
        return result.ToArray();
    }

    public async ValueTask DisposeAsync()
    {
        foreach (ExplorerHost host in hosts)
        {
            await host.DisposeAsync();
        }
        foreach (LayerStore store in stores)
        {
            await store.DisposeAsync();
        }
        if (Directory.Exists(cachePath))
        {
            Directory.Delete(cachePath, recursive: true);
        }
    }

    private sealed record TestImage(ExplorerSession Session, ExplorerSource Source,
        Mock<IDockerRegistryClient> Client, LayerStore Store);
}
