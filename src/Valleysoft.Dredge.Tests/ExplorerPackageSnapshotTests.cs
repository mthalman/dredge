using System.Collections;
using System.Formats.Tar;
using System.IO.Compression;
using System.Text;
using Valleysoft.DockerRegistryClient;
using Valleysoft.DockerRegistryClient.Models.Images;
using Valleysoft.DockerRegistryClient.Models.Manifests;
using Valleysoft.DockerRegistryClient.Models.Manifests.Oci;
using Valleysoft.Dredge.Commands;

namespace Valleysoft.Dredge.Tests;

public sealed class ExplorerPackageSnapshotTests : IAsyncDisposable
{
    private readonly string cachePath = Path.Combine(Directory.GetCurrentDirectory(), "package-snapshot-" + Guid.NewGuid().ToString("N"));
    private LayerStore? store;
    private readonly List<CountedPaths> replayCounts = [];
    private static readonly ImageName Image = ImageName.Parse("registry.test/repo:tag");
    private CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task SequentialLayersReplayEachPrefixLayerOnlyOnce()
    {
        byte[][] layers = [.. Enumerable.Range(0, 8).Select(i => Layer(($"unrelated-{i}", i.ToString(), TarEntryType.RegularFile)))];
        (ExplorerSession session, _) = await CreateAsync(layers);
        await using ImageFileSystem files = session.Files;
        replayCounts.ForEach(count => count.Counts = 0);

        for (int layer = 0; layer < layers.Length; layer++)
        {
            await session.GetPackagesAsync(layer, Token);
        }

        Assert.Equal(7, replayCounts.Sum(count => count.Counts));
        Assert.Equal([1, 1, 1, 1, 1, 1, 1, 0], replayCounts.Select(count => count.Counts));
    }

    [Fact]
    public async Task SequentialAndArbitraryInventoriesMatchIndependentSnapshots()
    {
        const string first = """{"name":"first","version":"1"}""";
        const string second = """{"name":"second","version":"2"}""";
        const string deps = """{"runtimeTarget":{"name":"net"},"targets":{"net":{"Example/1":{}}},"libraries":{"Example/1":{"type":"package"}}}""";
        byte[][] layers =
        [
            Layer(("targets/a", first, TarEntryType.RegularFile),
                ("targets/b", second, TarEntryType.RegularFile),
                ("node_modules/alias/package.json", "/targets/a", TarEntryType.SymbolicLink),
                ("node_modules/bad/package.json", "{", TarEntryType.RegularFile),
                ("app/App.deps.json", deps, TarEntryType.RegularFile),
                ("var/lib/dpkg/status", "Package: dpkg\nStatus: install ok installed\nVersion: 1\n", TarEntryType.RegularFile)),
            Layer(("node_modules/alias/package.json", "/targets/b", TarEntryType.SymbolicLink),
                ("node_modules/kept/package.json", "targets/a", TarEntryType.HardLink),
                ("app/.nupkg.metadata", "{}", TarEntryType.RegularFile)),
            Layer(("app/.wh..nupkg.metadata", "", TarEntryType.RegularFile),
                ("node_modules/.wh.bad", "", TarEntryType.RegularFile),
                ("targets/a", second, TarEntryType.RegularFile)),
            Layer(("node_modules/.wh..wh..opq", "", TarEntryType.RegularFile),
                ("python/example-1.dist-info/METADATA", "Name: Example\nVersion: 1\n", TarEntryType.RegularFile)),
            Layer(("other", "value", TarEntryType.RegularFile))
        ];
        (ExplorerSession session, _) = await CreateAsync(layers);
        await using ImageFileSystem files = session.Files;
        InstalledPackageMetadata[] actual = new InstalledPackageMetadata[layers.Length];
        for (int layer = 0; layer < layers.Length; layer++)
        {
            actual[layer] = await session.GetPackagesAsync(layer, Token);
        }
        for (int layer = 0; layer < layers.Length; layer++)
        {
            await using ImageFileSystem snapshot = files.CreateLayerSnapshot(layer, Token);
            AssertMetadataEqual(await InstalledPackageReader.ReadAsync(snapshot, Token), actual[layer]);
        }
        Assert.Equal(["2"], actual[1].Ecosystems[InstalledPackageEcosystem.Npm].Packages["second"]);
        Assert.Equal(["1"], actual[2].Ecosystems[InstalledPackageEcosystem.Npm].Packages["first"]);
        Assert.Empty(actual[1].Ecosystems[InstalledPackageEcosystem.NuGet].Packages);
        Assert.NotEmpty(actual[2].Ecosystems[InstalledPackageEcosystem.NuGet].Packages);
        Assert.Empty(actual[3].Ecosystems[InstalledPackageEcosystem.Npm].Packages);
        Assert.Single(actual[0].Diagnostics);
        Assert.Empty(actual[2].Diagnostics);
        Assert.Same(actual[0], await session.GetPackagesAsync(0, Token));
        Assert.Same(actual[4], await session.GetPackagesAsync(4, Token));
    }

    [Fact]
    public async Task UnchangedManifestIsReadOnceAcrossLayers()
    {
        byte[][] layers =
        [
            Layer(("node_modules/pkg/package.json", """{"name":"pkg","version":"1"}""", TarEntryType.RegularFile)),
            .. Enumerable.Range(1, 5).Select(i => Layer(($"unrelated-{i}", i.ToString(), TarEntryType.RegularFile)))
        ];
        (ExplorerSession session, Mock<IDockerRegistryClient> client) = await CreateAsync(layers);
        await using ImageFileSystem files = session.Files;
        for (int layer = 0; layer < layers.Length; layer++)
        {
            Assert.Equal(["1"], (await session.GetPackagesAsync(layer, Token))
                .Ecosystems[InstalledPackageEcosystem.Npm].Packages["pkg"]);
        }
        client.Verify(c => c.Blobs.GetRangeAsync(Image.Repo, LayerCacheTestContext.Digest(layers[0]),
            0, It.IsAny<long?>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task CanceledReplayIsDiscardedAndConcurrentRequestsRemainCorrect()
    {
        byte[][] layers = [.. Enumerable.Range(0, 5).Select(i => Layer(($"file-{i}", i.ToString(), TarEntryType.RegularFile)))];
        (ExplorerSession session, _) = await CreateAsync(layers);
        await using ImageFileSystem files = session.Files;
        await session.GetPackagesAsync(0, Token);
        using CancellationTokenSource canceled = new();
        replayCounts[2].OnEnumerate = canceled.Cancel;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => session.GetPackagesAsync(3, canceled.Token));
        replayCounts[2].OnEnumerate = null;
        Task<InstalledPackageMetadata>[] requests = [session.GetPackagesAsync(3, Token),
            session.GetPackagesAsync(1, Token), session.GetPackagesAsync(4, Token)];
        await Task.WhenAll(requests);
        Assert.All(requests, task => Assert.Empty(task.Result.Diagnostics));
        using CancellationTokenSource alreadyCanceled = new();
        alreadyCanceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => session.GetPackagesAsync(1, alreadyCanceled.Token));
    }

    private static void AssertMetadataEqual(InstalledPackageMetadata expected, InstalledPackageMetadata actual)
    {
        Assert.Equal(expected.UnavailableReason, actual.UnavailableReason);
        Assert.Equal(expected.Diagnostics, actual.Diagnostics);
        foreach (InstalledPackageEcosystem ecosystem in Enum.GetValues<InstalledPackageEcosystem>())
        {
            var before = expected.Ecosystems[ecosystem];
            var after = actual.Ecosystems[ecosystem];
            Assert.Equal(before.Availability, after.Availability);
            Assert.Equal(before.Packages.Keys, after.Packages.Keys);
            foreach (string key in before.Packages.Keys) Assert.Equal(before.Packages[key], after.Packages[key]);
        }
        Assert.Equal(expected.NpmPackageRoots.Keys, actual.NpmPackageRoots.Keys);
        foreach (string key in expected.NpmPackageRoots.Keys)
            Assert.Equal(expected.NpmPackageRoots[key], actual.NpmPackageRoots[key]);
    }

    private async Task<(ExplorerSession Session, Mock<IDockerRegistryClient> Client)> CreateAsync(byte[][] blobs)
    {
        store = new(cachePath);
        Mock<IDockerRegistryClient> client = new() { DefaultValue = DefaultValue.Mock };
        Dictionary<int, StoredLayerIndex> indexes = [];
        for (int i = 0; i < blobs.Length; i++)
        {
            byte[] blob = blobs[i];
            string digest = LayerCacheTestContext.Digest(blob);
            using MemoryStream input = new(blob);
            LayerChanges changes = await ImageLayerScanner.ScanAsync(input, new(i, digest), Token);
            StoredLayerIndex index = new(digest, blob.Length, changes);
            CountedPaths whiteouts = new(index.Changes.Whiteouts);
            replayCounts.Add(whiteouts);
            indexes.Add(i, index with { Changes = index.Changes with { Whiteouts = whiteouts } });
            client.Setup(c => c.Blobs.GetRangeAsync(Image.Repo, digest, 0,
                    It.IsAny<long?>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((string _, string _, long _, long? length, CancellationToken _) =>
                    new BlobDownloadResult(new MemoryStream(blob[..checked((int)length!.Value)]),
                        true, 0, length - 1, blob.Length));
        }
        OciImageManifest manifest = new()
        {
            Config = new OciDescriptor { Digest = "sha256:config" },
            Layers = [.. indexes.Values.Select(index => new OciDescriptor { Digest = index.Digest, Size = index.BlobLength })]
        };
        ResolvedManifest resolved = new(new ManifestInfo("application/vnd.oci.image.manifest.v1+json",
            LayerCacheTestContext.Digest(Encoding.UTF8.GetBytes(string.Join(",", indexes.Values.Select(i => i.Digest)))),
            manifest), manifest);
        Image config = new() { Os = "linux", Architecture = "amd64" };
        ImageFileSystem files = await ImageFileSystem.CreateAsync(client.Object, Image, new PlatformOptionsBase(),
            Token, store, resolvedManifest: resolved, imageConfig: config, requireLayerIndexes: true, layerIndexes: indexes);
        return (new ExplorerSession
        {
            Image = Image, Resolved = resolved, Config = config, Files = files,
            Analysis = files.Analyze(), Entries = files.List(null, true, false)
        }, client);
    }

    private static byte[] Layer(params (string Path, string Value, TarEntryType Type)[] entries)
    {
        using MemoryStream result = new();
        using (GZipStream gzip = new(result, CompressionMode.Compress, leaveOpen: true))
        using (TarWriter writer = new(gzip, leaveOpen: true))
        {
            foreach ((string path, string value, TarEntryType type) in entries)
            {
                using MemoryStream data = new(Encoding.UTF8.GetBytes(value));
                PaxTarEntry entry = new(type, path);
                if (type == TarEntryType.RegularFile) entry.DataStream = data;
                else entry.LinkName = value;
                writer.WriteEntry(entry);
            }
        }
        return result.ToArray();
    }

    public async ValueTask DisposeAsync()
    {
        if (store is not null) await store.DisposeAsync();
        if (Directory.Exists(cachePath)) Directory.Delete(cachePath, recursive: true);
    }

    private sealed class CountedPaths(IReadOnlyList<string> paths) : IReadOnlyList<string>
    {
        public int Counts { get; set; }
        public Action? OnEnumerate { get; set; }
        public int Count => paths.Count;
        public string this[int index] => paths[index];
        public IEnumerator<string> GetEnumerator()
        {
            Counts++;
            OnEnumerate?.Invoke();
            return paths.GetEnumerator();
        }
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
