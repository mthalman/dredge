using System.Text;
using System.Text.Json;
using Valleysoft.DockerRegistryClient;
using Valleysoft.DockerRegistryClient.Models.Manifests;
using Valleysoft.DockerRegistryClient.Models.Manifests.Oci;
using Valleysoft.Dredge.Commands;
using Valleysoft.Dredge.Explorer;
using Valleysoft.Dredge.Explorer.Tui;
using static Valleysoft.Dredge.Tests.LayerCacheTestContext;

namespace Valleysoft.Dredge.Tests;

public sealed class RegistryTargetRetentionTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task UniqueRegistryTargetsReleasePinsAndRevisitTheOriginalManifest(bool daemonBaseline)
    {
        await using Harness harness = await Harness.CreateAsync(daemonBaseline);
        ExplorerComparison first = await harness.Host.CompareAsync(
            "registry://registry.test/target0:tag", () => { }, Token);
        string original = first.Target.Resolved.ManifestInfo.DockerContentDigest;
        using IDisposable reader = harness.Host.RetainComparison(first);
        string firstBlob = harness.Registry.Images["target0"].BlobDigest;
        for (int i = 1; i < 8; i++)
        {
            harness.Registry.Add($"target{i}", $"target {i}");
            await harness.Host.CompareAsync($"registry://registry.test/target{i}:tag", () => { }, Token);
            await harness.Cache.Store.ClearAsync(Token);
            Assert.InRange(harness.TargetBlobCount, 1, 3);
            Assert.True(File.Exists(harness.Cache.Store.GetBlobPath(firstBlob)));
        }
        Assert.NotEmpty((await harness.Host.DiffAsync(first, "payload.txt", Token)).Lines!);
        reader.Dispose();
        await harness.Host.CompareAsync("registry://registry.test/target7:tag", () => { }, Token);
        await harness.Cache.Store.ClearAsync(Token);
        Assert.False(File.Exists(harness.Cache.Store.GetBlobPath(firstBlob)));

        harness.Registry.Add("target0", "moved tag");
        int resolutions = harness.Registry.Resolutions.Count;
        ExplorerComparison revisited = await harness.Host.CompareAsync(
            "registry://registry.test/target0:tag", () => { }, Token);
        Assert.Equal(original, revisited.Target.Resolved.ManifestInfo.DockerContentDigest);
        Assert.Equal(resolutions, harness.Registry.Resolutions.Count);
        Assert.NotEmpty((await harness.Host.DiffAsync(revisited, "payload.txt", Token)).Lines!);
        Assert.InRange(harness.TargetBlobCount, 1, 2);
        if (daemonBaseline)
        {
            Assert.InRange(harness.Registry.Clients.Count(client => !client.Disposed), 1, 2);
        }
        await harness.Host.DisposeAsync();
        await harness.Cache.Store.ClearAsync(Token);
        Assert.Equal(0, harness.TargetBlobCount);
        if (daemonBaseline)
        {
            Assert.All(harness.Registry.Clients, client => Assert.True(client.Disposed));
        }
        else
        {
            Assert.False(harness.BorrowedClient!.Disposed);
        }
    }

    [Fact]
    public async Task DisplayedAndQueuedRegistryTargetsSurviveUntilTheirReadersDrain()
    {
        await using Harness harness = await Harness.CreateAsync(daemonBaseline: true);
        ExplorerComparison displayed = await harness.Host.CompareAsync(
            "registry://registry.test/target0:tag", () => { }, Token);
        using IDisposable reader = harness.Host.RetainComparison(displayed);
        harness.Registry.Add("pending", "pending");
        using RetainedExplorerComparison pending = await harness.Host.CompareRetainedAsync(
            "registry://registry.test/pending:tag", () => { }, Token);
        for (int i = 1; i < 6; i++)
        {
            harness.Registry.Add($"target{i}", $"target {i}");
            await harness.Host.CompareAsync($"registry://registry.test/target{i}:tag", () => { }, Token);
        }
        Assert.NotEmpty((await harness.Host.DiffAsync(displayed, "payload.txt", Token)).Lines!);
        Assert.NotEmpty((await harness.Host.DiffAsync(pending.Comparison, "payload.txt", Token)).Lines!);
        Task closing = harness.Host.DisposeAsync().AsTask();
        try
        {
            Assert.False(closing.IsCompleted);
            reader.Dispose();
            Assert.False(closing.IsCompleted);
        }
        finally
        {
            reader.Dispose();
            pending.Dispose();
            await closing.WaitAsync(TimeSpan.FromSeconds(10), Token);
        }
        Assert.All(harness.Registry.Clients, client => Assert.True(client.Disposed));
        await harness.Cache.Store.ClearAsync(Token);
        Assert.Equal(0, harness.TargetBlobCount);
    }

    [Fact]
    public async Task FailedAndCanceledRegistryComparisonsDoNotAccumulateClientsOrPins()
    {
        await using Harness harness = await Harness.CreateAsync(daemonBaseline: true);
        await harness.Host.CompareAsync("registry://registry.test/target0:tag", () => { }, Token);
        for (int i = 1; i < 6; i++)
        {
            harness.Registry.Add($"target{i}", $"target {i}");
            string reference = $"registry://registry.test/target{i}:tag";
            if (i % 2 == 0)
            {
                using CancellationTokenSource canceled = CancellationTokenSource.CreateLinkedTokenSource(Token);
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                    harness.Host.CompareAsync(reference, canceled.Cancel, canceled.Token));
            }
            else
            {
                await Assert.ThrowsAsync<IOException>(() =>
                    harness.Host.CompareAsync(reference, () => throw new IOException("callback failed"), Token));
            }
            Assert.InRange(harness.Registry.Clients.Count(client => !client.Disposed), 1, 2);
            await harness.Cache.Store.ClearAsync(Token);
            Assert.InRange(harness.TargetBlobCount, 1, 2);
        }
    }

    private sealed class Harness : IAsyncDisposable
    {
        public LayerCacheTestContext Cache { get; } = new(maxBytes: 0);
        public Registry Registry { get; } = new();
        public RegistryClient? BorrowedClient { get; private set; }
        public ExplorerHost Host { get; private set; } = null!;
        private ExplorerSource baseline = null!;
        public int TargetBlobCount => Registry.AllBlobDigests.Count(digest => File.Exists(Cache.Store.GetBlobPath(digest)));

        public static async Task<Harness> CreateAsync(bool daemonBaseline)
        {
            Harness harness = new();
            harness.Registry.Add("baseline", "baseline");
            harness.Registry.Add("target0", "target 0");
            Image image = harness.Registry.Images["baseline"];
            string reference = daemonBaseline ? "docker://baseline:tag" : "registry://registry.test/baseline:tag";
            ImageSourceReference parsed = ImageSourceReference.Parse(reference);
            ResolvedImage source = new(parsed, parsed.GetImageName(), new(image.Info, image.Manifest),
                daemonBaseline ? null : image.Info,
                JsonHelper.Deserialize<Valleysoft.DockerRegistryClient.Models.Images.Image>(
                    Encoding.UTF8.GetString(image.Config))!, image.Config,
                new Content(image.Blob), image.Info.DockerContentDigest, "linux/amd64");
            harness.baseline = new()
            {
                Source = source, Image = source.Name, Config = source.Configuration,
                Resolved = source.Resolved, Platforms = [], RequestedReference = reference,
                Platform = new("linux", "amd64", null, null)
            };
            ExplorerSession session = await ExplorerSession.CreateAsync(
                null, harness.baseline, harness.Cache.Store, null, Token);
            ExplorerLayerIndexer indexer = ExplorerLayerIndexer.Create(null, harness.baseline, harness.Cache.Store);
            if (!daemonBaseline)
            {
                harness.BorrowedClient = harness.Registry.CreateClient();
            }
            harness.Host = new(harness.BorrowedClient?.Mock.Object, harness.Registry.Factory.Object,
                harness.baseline, harness.Cache.Store, ExplorerImage.FromSource(harness.baseline),
                indexer, new(null, null, false, ClipboardMode.Off, "", "")) { Session = session };
            return harness;
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                await Host.DisposeAsync();
            }
            finally
            {
                await baseline.DisposeAsync();
                await Cache.DisposeAsync();
            }
        }
    }

    private sealed record Image(byte[] Config, OciImageManifest Manifest, ManifestInfo Info, byte[] Blob)
    {
        public string BlobDigest => Manifest.Layers[0].Digest!;
    }

    private sealed class Registry
    {
        public Dictionary<string, Image> Images { get; } = [];
        public HashSet<string> AllBlobDigests { get; } = [];
        private readonly Dictionary<string, byte[]> configs = [];
        private readonly Dictionary<string, byte[]> blobs = [];
        public List<RegistryClient> Clients { get; } = [];
        public List<string> Resolutions { get; } = [];
        public Mock<IDockerRegistryClientFactory> Factory { get; } = new();

        public Registry()
        {
            Factory.Setup(value => value.GetClientAsync("registry.test", It.IsAny<CancellationToken>()))
                .Returns(() => Task.FromResult(CreateClient().Mock.Object));
        }

        public void Add(string repo, string payload)
        {
            byte[] blob = DockerArchiveFixture.BuildTar(("payload.txt", Encoding.UTF8.GetBytes(payload)));
            string digest = Digest(blob);
            byte[] config = JsonSerializer.SerializeToUtf8Bytes(new
            {
                os = "linux", architecture = "amd64",
                rootfs = new { type = "layers", diff_ids = new[] { digest } }
            });
            OciImageManifest manifest = new()
            {
                Config = new() { Digest = Digest(config) },
                Layers = [new OciDescriptor { Digest = digest, Size = blob.Length,
                    MediaType = DockerImageArchive.RawLayerMediaType }]
            };
            ManifestInfo info = new("application/vnd.oci.image.manifest.v1+json",
                Digest(Encoding.UTF8.GetBytes(repo + digest)), manifest);
            Images[repo] = new(config, manifest, info, blob);
            configs[Digest(config)] = config;
            blobs[digest] = blob;
            if (repo != "baseline")
            {
                AllBlobDigests.Add(digest);
            }
        }

        public RegistryClient CreateClient()
        {
            RegistryClient client = new();
            Clients.Add(client);
            client.Mock.Setup(value => value.Manifests.GetAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .Returns((string repo, string tag, CancellationToken _) =>
                {
                    ObjectDisposedException.ThrowIf(client.Disposed, client);
                    Resolutions.Add($"{repo}:{tag}");
                    return Task.FromResult(Images[repo].Info);
                });
            client.Mock.Setup(value => value.Blobs.GetAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .Returns((string _, string digest, CancellationToken _) =>
                    Task.FromResult<Stream>(new MemoryStream(configs[digest])));
            client.Mock.Setup(value => value.Blobs.GetRangeAsync(It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<long>(), It.IsAny<long?>(), It.IsAny<CancellationToken>()))
                .Returns((string _, string digest, long _, long? _, CancellationToken _) =>
                {
                    ObjectDisposedException.ThrowIf(client.Disposed, client);
                    byte[] blob = blobs[digest];
                    return Task.FromResult(new BlobDownloadResult(
                        new MemoryStream(blob), false, null, null, blob.Length));
                });
            client.Mock.Setup(value => value.Dispose()).Callback(() => client.Disposed = true);
            return client;
        }
    }

    private sealed class RegistryClient
    {
        public Mock<IDockerRegistryClient> Mock { get; } = new() { DefaultValue = DefaultValue.Mock };
        public bool Disposed { get; set; }
    }

    private sealed class Content(byte[] blob) : IImageContentSource
    {
        public Task<BlobDownloadResult> GetRangeAsync(
            string digest, long start, long? length, CancellationToken token) =>
            Task.FromResult(new BlobDownloadResult(new MemoryStream(blob), false, null, null, blob.Length));
    }
}
