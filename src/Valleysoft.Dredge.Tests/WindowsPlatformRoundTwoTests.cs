using System.Collections;
using Valleysoft.DockerRegistryClient.Models.Images;
using Valleysoft.DockerRegistryClient.Models.Manifests;
using Valleysoft.DockerRegistryClient.Models.Manifests.Oci;
using static Valleysoft.Dredge.Tests.WindowsImageTestData;

namespace Valleysoft.Dredge.Tests;

public sealed class WindowsPlatformRoundTwoTests : IAsyncDisposable
{
    private readonly LayerCacheTestContext cache = new();
    private CancellationToken Token => TestContext.Current.CancellationToken;
    public ValueTask DisposeAsync() => cache.DisposeAsync();

    [Fact]
    public async Task WindowsPackageInventoryDoesNotReplayAnyLayerPrefix()
    {
        ReplayGuard whiteouts = new();
        ExplorerSession session = await SessionAsync(whiteouts);
        await using ImageFileSystem files = session.Files;
        whiteouts.BlockReplay = true;

        InstalledPackageMetadata first = await session.GetPackagesAsync(0, Token);
        InstalledPackageMetadata second = await session.GetPackagesAsync(1, Token);
        await session.EnsurePackagesAsync(Token);

        Assert.Same(first, second);
        Assert.Same(first, session.Packages);
        Assert.Equal(Enum.GetValues<InstalledPackageEcosystem>().Length, first.Ecosystems.Count);
        Assert.All(first.Ecosystems.Values, static ecosystem =>
        {
            Assert.Equal(InstalledPackageMetadataAvailability.Unavailable, ecosystem.Availability);
            Assert.Empty(ecosystem.Packages);
        });
        Assert.Contains("Windows", first.UnavailableReason);
        Assert.Equal(first.UnavailableReason, Assert.Single(first.Diagnostics).Message);
        Assert.Empty(first.NpmPackageRoots);
    }

    [Theory]
    [InlineData(-2)]
    [InlineData(-1)]
    [InlineData(3)]
    public async Task WindowsPackageInventoryStillRejectsInvalidLayerNumbers(int layer)
    {
        ExplorerSession session = await SessionAsync(new());
        await using ImageFileSystem files = session.Files;
        await session.EnsurePackagesAsync(Token);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => session.GetPackagesAsync(layer, Token));
    }

    [Fact]
    public async Task WindowsPackageInventoryStillRequiresAnIndexedPrefix()
    {
        ExplorerSession session = await SessionAsync(new(), indexedLayers: 1);
        await using ImageFileSystem files = session.Files;
        await session.GetPackagesAsync(0, Token);
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.GetPackagesAsync(1, Token));
    }

    [Fact]
    public async Task WindowsPackageInventoryHonorsCancellationEvenWhenCached()
    {
        ExplorerSession session = await SessionAsync(new());
        await using ImageFileSystem files = session.Files;
        await session.GetPackagesAsync(0, Token);
        using CancellationTokenSource canceled = new();
        canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => session.GetPackagesAsync(0, canceled.Token));
    }

    [Fact]
    public async Task EmptyWindowsImageHasUnavailableInventoryOnlyAtItsFinalLayer()
    {
        ExplorerSession session = await SessionAsync(new(), indexedLayers: 0, layerCount: 0);
        await using ImageFileSystem files = session.Files;
        await session.EnsurePackagesAsync(Token);
        Assert.Contains("Windows", session.Packages.UnavailableReason);
        Assert.Same(session.Packages, await session.GetPackagesAsync(-1, Token));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => session.GetPackagesAsync(0, Token));
    }

    private async Task<ExplorerSession> SessionAsync(
        ReplayGuard whiteouts, int indexedLayers = 3, int layerCount = 3)
    {
        Mock<IDockerRegistryClient> client = new() { DefaultValue = DefaultValue.Mock };
        Dictionary<int, StoredLayerIndex> indexes = [];
        for (int layer = 0; layer < indexedLayers; layer++)
        {
            indexes.Add(layer, new($"sha256:layer{layer}", 0, new([], whiteouts, [])));
        }
        OciImageManifest manifest = new()
        {
            Config = new OciDescriptor { Digest = "sha256:config" },
            Layers = [.. Enumerable.Range(0, layerCount).Select(layer => new OciDescriptor
            {
                Digest = $"sha256:layer{layer}", MediaType = "application/vnd.oci.image.layer.v1.tar+gzip"
            })]
        };
        ResolvedManifest resolved = new(new ManifestInfo("application/manifest",
            LayerCacheTestContext.Digest(BitConverter.GetBytes(indexedLayers)), manifest), manifest);
        Image config = new() { Os = "windows", Architecture = "amd64" };
        ImageFileSystem files = await ImageFileSystem.CreateAsync(client.Object, Name, new(), Token,
            cache.Store, resolvedManifest: resolved, imageConfig: config, requireLayerIndexes: true,
            layerIndexes: indexes, maxLayerIndex: indexedLayers > 0 ? indexedLayers - 1 : null);
        return new()
        {
            Image = Name,
            Resolved = resolved,
            Config = config,
            Files = files,
            Analysis = ImageAnalysis.Analyze([.. indexes.Values.Select(index => index.Changes)],
                ImageFileSystemSemantics.Windows),
            Entries = files.List(null, true, false)
        };
    }

    private sealed class ReplayGuard : IReadOnlyList<string>
    {
        public bool BlockReplay { get; set; }
        public int Count => 0;
        public string this[int index] => throw new ArgumentOutOfRangeException(nameof(index));
        public IEnumerator<string> GetEnumerator()
        {
            if (BlockReplay)
            {
                throw new InvalidOperationException("The Windows package inventory must not replay filesystem layers.");
            }
            return Enumerable.Empty<string>().GetEnumerator();
        }
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
