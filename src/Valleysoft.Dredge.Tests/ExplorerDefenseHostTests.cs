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
    public async Task PreviewRetainsTruncationAlongsideFinalVersionProvenance()
    {
        TestImage image = await CreateAsync(Blob(("file", "old")),
            Blob(("file", new string('x', ExplorerApp.PreviewLimit + 1))));
        PreviewContent preview = await Host(image).PreviewAsync("file", 0, Token);

        Assert.NotNull(preview.Lines);
        Assert.Contains("Showing the first", preview.Message);
        Assert.Contains("Showing the final version from layer 1.", preview.Message);
        Assert.Equal(ExplorerApp.PreviewLimit, preview.Bytes);
    }

    [Fact]
    public async Task CachedTagDescriptionUsesTheComparisonSnapshotAfterTagMoves()
    {
        TestImage baseline = await CreateAsync(Blob(("baseline", "base")));
        byte[] targetBlob = Blob(("target", "old tag"));
        TestImage target = await CreateAsync(targetBlob);
        TestImage moved = await CreateAsync(Blob(("moved", "new tag")), Blob(("extra", "new layer")));
        baseline.Client.Setup(c => c.Manifests.GetAsync(Image.Repo, "previous", It.IsAny<CancellationToken>()))
            .ReturnsAsync(target.Source.Resolved.ManifestInfo);
        baseline.Client.Setup(c => c.Blobs.GetRangeAsync(Image.Repo, LayerCacheTestContext.Digest(targetBlob),
            0, It.IsAny<long?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new BlobDownloadResult(new MemoryStream(targetBlob), false, null, null, targetBlob.Length));
        ExplorerHost host = Host(baseline);
        ExplorerComparison comparison = await host.CompareAsync("registry.test/repo:previous", () => { }, Token);
        baseline.Client.Setup(c => c.Manifests.GetAsync(Image.Repo, "previous", It.IsAny<CancellationToken>()))
            .ReturnsAsync(moved.Source.Resolved.ManifestInfo);

        TagChoice choice = new("previous");
        await host.DescribeTagAsync(choice, Token);
        ExplorerComparison repeated = await host.CompareAsync("previous", () => { }, Token);

        Assert.Same(comparison.Target, repeated.Target);
        Assert.Equal(comparison.Target.Resolved.ManifestInfo.DockerContentDigest, choice.Digest);
        Assert.Equal(1, choice.LayerCount);
        Assert.Equal(targetBlob.Length, choice.AdditionalDownload);
        Assert.Contains("cached session snapshot", choice.Note);
        Assert.Contains("reopen explorer", choice.Note);
        baseline.Client.Verify(c => c.Manifests.GetAsync(Image.Repo, "previous", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task BaselineTagDescriptionIncludesItsResolvedSnapshotDigest()
    {
        TestImage baseline = await CreateAsync();
        TagChoice choice = new("current");
        await Host(baseline).DescribeTagAsync(choice, Token);
        Assert.Equal(baseline.Source.Resolved.ManifestInfo.DockerContentDigest, choice.Digest);
        Assert.Contains("session snapshot", choice.Note);
        baseline.Client.Verify(c => c.Manifests.GetAsync(Image.Repo, It.IsAny<string>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task OversizedOwnershipIsRejectedBeforeDownloadWhileReadableFilesRemain()
    {
        byte[] large = Blob(("other/example-1.dist-info/RECORD", "oversized,,"));
        TestImage baseline = await CreateAsync(
            [Blob(("site/example-1.dist-info/RECORD", "good,,"), ("site/good", "old")), large],
            indexes =>
            {
                StoredLayerIndex index = indexes[1];
                indexes[1] = index with
                {
                    Changes = index.Changes with
                    {
                        Entries = index.Changes.Entries.Select(entry =>
                            entry with { Size = ExplorerHost.MaxPackageOwnershipBytes + 1 }).ToArray()
                    }
                };
            });
        TestImage target = await CreateAsync();
        PackageFilesContent result = await Host(baseline).PackageFilesAsync(await CompareAsync(baseline, target),
            new(InstalledPackageEcosystem.Pip, "example", "1", null), Token);

        Assert.Equal(("site/good", Change.Removed), Assert.Single(result.Files!));
        Assert.Equal(1, result.Total);
        Assert.Contains("incomplete", result.Message);
        Assert.Contains("67108864", Assert.Single(result.Warnings!));
        baseline.Client.Verify(c => c.Blobs.GetRangeAsync(Image.Repo, LayerCacheTestContext.Digest(large),
            0, It.IsAny<long?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task MalformedAndInvalidUtf8OwnershipDoNotDiscardReadableRecords()
    {
        TestImage baseline = await CreateAsync(Archive(
            ("site/example-1.dist-info/RECORD", Encoding.UTF8.GetBytes("good,,")),
            ("bad/example-1.dist-info/RECORD", Encoding.UTF8.GetBytes("\"unterminated,,\nfabricated,,")),
            ("invalid/example-1.dist-info/RECORD", [0xff]),
            ("site/good", Encoding.UTF8.GetBytes("old"))));
        TestImage target = await CreateAsync();
        PackageFilesContent result = await Host(baseline).PackageFilesAsync(await CompareAsync(baseline, target),
            new(InstalledPackageEcosystem.Pip, "example", "1", null), Token);

        Assert.Equal(("site/good", Change.Removed), Assert.Single(result.Files!));
        Assert.Equal(2, result.Warnings!.Count);
        Assert.All(result.Warnings, warning => Assert.StartsWith("Baseline /", warning));
    }

    [Fact]
    public async Task UnsupportedNuGetOwnershipIsExplicitlyUnavailable()
    {
        TestImage baseline = await CreateAsync();
        PackageFilesContent result = await Host(baseline).PackageFilesAsync(await CompareAsync(baseline, baseline),
            new(InstalledPackageEcosystem.NuGet, "example", "1", "2"), Token);

        Assert.Null(result.Files);
        Assert.Contains("unavailable", result.Message);
        Assert.All(result.Warnings!, warning => Assert.Contains("does not establish deployed file ownership", warning));
    }

    [Fact]
    public async Task OwnershipCancellationIsNotReturnedAsPartialSuccess()
    {
        TestImage baseline = await CreateAsync(Blob(("var/lib/dpkg/info/example.list", "/file")));
        baseline.Client.Setup(c => c.Blobs.GetRangeAsync(Image.Repo, It.IsAny<string>(),
            0, It.IsAny<long?>(), It.IsAny<CancellationToken>())).ThrowsAsync(new OperationCanceledException());
        ExplorerComparison comparison = new(baseline.Session, baseline.Session, 0, [], []);

        await Assert.ThrowsAsync<OperationCanceledException>(() => Host(baseline).PackageFilesAsync(comparison,
            new(InstalledPackageEcosystem.Dpkg, "example", "1", null), Token));
    }

    [Theory]
    [InlineData("1", "2", 2)]
    [InlineData("1", null, 1)]
    [InlineData(null, "2", 1)]
    public async Task PackageOwnershipIncludesApplicableBaselineAndTargetFiles(
        string? baselineVersion, string? targetVersion, int expectedCount)
    {
        TestImage baseline = await CreateAsync(Blob(("var/lib/dpkg/info/example.list", "/old\n/shared\n"),
            ("old", "old"), ("shared", "same")));
        TestImage target = await CreateAsync(Blob(("var/lib/dpkg/info/example.list", "/new\n/shared\n"),
            ("new", "new"), ("shared", "same")));
        ExplorerComparison comparison = await CompareAsync(baseline, target);

        PackageFilesContent result = await Host(baseline).PackageFilesAsync(comparison,
            new(InstalledPackageEcosystem.Dpkg, "example", baselineVersion, targetVersion), Token);

        Assert.Equal(expectedCount, result.Files!.Count);
        Assert.Equal(expectedCount + 1, result.Total);
        if (baselineVersion is not null)
        {
            Assert.Contains(("old", Change.Removed), result.Files);
        }
        if (targetVersion is not null)
        {
            Assert.Contains(("new", Change.Added), result.Files);
        }
    }

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

    private Task<TestImage> CreateAsync(params byte[][] blobs) => CreateAsync(blobs, null);

    private async Task<TestImage> CreateAsync(byte[][] blobs, Action<Dictionary<int, StoredLayerIndex>>? customize)
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
        customize?.Invoke(indexes);
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

    private static async Task<ExplorerComparison> CompareAsync(TestImage baseline, TestImage target)
    {
        await baseline.Session.EnsurePackagesAsync(Token);
        await target.Session.EnsurePackagesAsync(Token);
        return ExplorerSession.Compare(baseline.Session, target.Session);
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
