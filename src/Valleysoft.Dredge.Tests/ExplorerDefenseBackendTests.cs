using System.Formats.Tar;
using System.IO.Compression;
using System.Text;
using Valleysoft.DockerRegistryClient;
using Valleysoft.DockerRegistryClient.Models.Images;
using Valleysoft.DockerRegistryClient.Models.Manifests;
using Valleysoft.DockerRegistryClient.Models.Manifests.Oci;
using Valleysoft.Dredge.Commands;

namespace Valleysoft.Dredge.Tests;

public sealed class ExplorerDefenseBackendTests : IAsyncDisposable
{
    private readonly string cachePath = Path.Combine(Directory.GetCurrentDirectory(),
        "backend-defense-cache-" + Guid.NewGuid().ToString("N"));
    private readonly List<LayerStore> stores = [];
    private static readonly ImageName Image = ImageName.Parse("registry.test/repo:tag");

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MetadataTransportFailureIsAdvisoryAndOtherLayersRemainReadable(bool responseBody)
    {
        byte[][] blobs =
        [
            Blob(("one", "one"), ("two", "two")),
            Blob(("readable", "value"))
        ];
        (ImageFileSystem files, Mock<IDockerRegistryClient> client, _) = await CreateAsync(blobs);
        HttpRequestException failure = new("connection reset");
        var request = client.Setup(c => c.Blobs.GetRangeAsync(Image.Repo,
            LayerCacheTestContext.Digest(blobs[0]), 0, It.IsAny<long?>(), It.IsAny<CancellationToken>()));
        if (responseBody)
        {
            request.ReturnsAsync(new BlobDownloadResult(new FailedReadStream(failure), false, null, null, null));
        }
        else
        {
            request.ThrowsAsync(failure);
        }

        List<(string Path, byte[]? Content, Exception? Error)> results = [];
        await foreach (var result in files.ReadFilesAsync(
            [("one", 100), ("two", 100), ("readable", 100)], TestContext.Current.CancellationToken))
        {
            results.Add(result);
        }

        Assert.Equal(3, results.Count);
        Assert.All(results.Take(2), result =>
        {
            Assert.Null(result.Content);
            Assert.Same(failure, result.Error);
        });
        Assert.Null(results[2].Error);
        Assert.Equal("value", Encoding.UTF8.GetString(results[2].Content!));
    }

    [Fact]
    public async Task MetadataTransportCancellationPropagates()
    {
        byte[] blob = Blob(("one", "one"));
        (ImageFileSystem files, Mock<IDockerRegistryClient> client, _) = await CreateAsync([blob]);
        client.Setup(c => c.Blobs.GetRangeAsync(Image.Repo, LayerCacheTestContext.Digest(blob),
            0, It.IsAny<long?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationCanceledException());

        await Assert.ThrowsAsync<OperationCanceledException>(async () =>
        {
            await foreach (var result in files.ReadFilesAsync([("one", 100)], TestContext.Current.CancellationToken))
            {
                Assert.Fail("Cancellation must not become a metadata result.");
            }
        });
    }

    private async Task<(ImageFileSystem Files, Mock<IDockerRegistryClient> Client, ResolvedManifest Resolved)>
        CreateAsync(byte[][] blobs)
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
            LayerChanges changes = await ImageLayerScanner.ScanAsync(input, new(i, digest),
                TestContext.Current.CancellationToken);
            indexes.Add(i, new(digest, blob.Length, changes));
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
        ResolvedManifest resolved = new(new ManifestInfo("application/vnd.oci.image.manifest.v1+json",
            LayerCacheTestContext.Digest(Encoding.UTF8.GetBytes(string.Join(",", indexes.Values.Select(i => i.Digest)))),
            manifest), manifest);
        ImageFileSystem files = await ImageFileSystem.CreateAsync(client.Object, Image, new PlatformOptionsBase(),
            TestContext.Current.CancellationToken, store, resolvedManifest: resolved,
            imageConfig: new Image { Os = "linux", Architecture = "amd64" },
            requireLayerIndexes: true, layerIndexes: indexes);
        return (files, client, resolved);
    }

    private static byte[] Blob(params (string Path, string Content)[] files)
    {
        using MemoryStream result = new();
        using (GZipStream gzip = new(result, CompressionMode.Compress, leaveOpen: true))
        using (TarWriter writer = new(gzip, leaveOpen: true))
        {
            foreach ((string path, string content) in files)
            {
                using MemoryStream data = new(Encoding.UTF8.GetBytes(content));
                writer.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, path) { DataStream = data });
            }
        }
        return result.ToArray();
    }

    public async ValueTask DisposeAsync()
    {
        foreach (LayerStore store in stores)
        {
            await store.DisposeAsync();
        }
        if (Directory.Exists(cachePath))
        {
            Directory.Delete(cachePath, recursive: true);
        }
    }

    private sealed class FailedReadStream(Exception failure) : MemoryStream
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            ValueTask.FromException<int>(failure);
    }
}
