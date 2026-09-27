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
    public async Task HardLinkSelfComparisonUsesCapturedContentAfterOriginalChanges(bool remove)
    {
        ExplorerSession session = await SessionAsync(
            Archive(("original", "old", TarEntryType.RegularFile),
                ("saved", "original", TarEntryType.HardLink),
                ("chain", "saved", TarEntryType.HardLink)),
            remove ? Blob((".wh.original", "")) : Blob(("original", "new")));

        Assert.Empty(ExplorerSession.Compare(session, session).Files);
        Assert.Equal(session.Analysis.LiveContents["saved"].ContentHash,
            session.Analysis.LiveContents["chain"].ContentHash);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HardLinksWithMatchingHeadersCompareTheirCapturedBytes(bool remove)
    {
        ExplorerSession baseline = await SessionAsync(
            Archive(("original", "old", TarEntryType.RegularFile),
                ("saved", "original", TarEntryType.HardLink)),
            remove ? Blob((".wh.original", "")) : Blob(("original", "new")));
        ExplorerSession target = await SessionAsync(
            Archive(("original", "new", TarEntryType.RegularFile),
                ("saved", "original", TarEntryType.HardLink)),
            remove ? Blob((".wh.original", "")) : Blob(("original", "new")));

        ExplorerFileDifference difference = Assert.Single(ExplorerSession.Compare(baseline, target).Files);
        Assert.Equal("saved", difference.Path);
        Assert.Equal(LayerChangeKind.Modified, difference.Kind);
    }

    [Fact]
    public async Task ReplacingOriginalDoesNotModifySurvivingHardLink()
    {
        byte[] shared = Archive(("original", "old", TarEntryType.RegularFile),
            ("saved", "original", TarEntryType.HardLink));
        ExplorerSession baseline = await SessionAsync(shared, Blob(("original", "new")));
        ExplorerSession target = await SessionAsync(shared, Blob(("original", "NEW")));

        Assert.Equal("original", Assert.Single(ExplorerSession.Compare(baseline, target).Files).Path);
    }

    [Fact]
    public async Task HardLinksToSymbolicLinksCompareCapturedLinkTargets()
    {
        ExplorerSession baseline = await SessionAsync(
            Archive(("original", "before", TarEntryType.SymbolicLink),
                ("saved", "original", TarEntryType.HardLink)),
            Blob((".wh.original", "")));
        ExplorerSession target = await SessionAsync(
            Archive(("original", "after", TarEntryType.SymbolicLink),
                ("saved", "original", TarEntryType.HardLink)),
            Blob((".wh.original", "")));

        Assert.Empty(ExplorerSession.Compare(baseline, baseline).Files);
        Assert.Equal("saved", Assert.Single(ExplorerSession.Compare(baseline, target).Files).Path);
    }

    [Theory]
    [InlineData("old", "new")]
    [InlineData("same", "same")]
    public void LastHardLinkReplacementDoesNotClaimIdenticalPayloadShipment(
        string originalHash, string replacementHash)
    {
        ImageAnalysisResult analysis = ImageAnalysis.Analyze(
        [
            Layer(File("original", 7, originalHash), HardLink("saved", "original")),
            Layer(File("original", 7, replacementHash)),
            Layer(HardLink("saved", "original"))
        ]);

        HiddenFile hidden = Assert.Single(analysis.HiddenFiles);
        Assert.Equal("original", hidden.Path);
        Assert.Equal("saved", hidden.ReplacedByHardLink);
        Assert.Equal(0, hidden.Layer);
        Assert.Equal(2, hidden.HiddenBy);
        Assert.Equal(7, hidden.Size);
        Assert.Equal(LayerChangeKind.Modified, hidden.Reason);
        Assert.Equal(originalHash == replacementHash ? LayerChangeKind.Identical : LayerChangeKind.Modified,
            Assert.Single(analysis.Layers[2].Changes).Kind);
        ExplorerFinding finding = Assert.Single(ExplorerInsights.Build(analysis, ["RUN create", "RUN replace", "RUN ln"], null).Findings);
        Assert.Equal(ExplorerFindingKind.Replaced, finding.Kind);
        Assert.Contains("not new file payload", finding.Why);
        Assert.DoesNotContain("same bytes", finding.Why);
        Assert.Contains("/saved", string.Join("\n", finding.Explain));
        Assert.Contains("original content", finding.Fix);
    }

    [Fact]
    public void RefreshingHardLinkToSameContentDoesNotHideItsPayload()
    {
        ImageAnalysisResult analysis = ImageAnalysis.Analyze(
        [
            Layer(File("original", 7, "hash"), HardLink("saved", "original")),
            Layer(HardLink("saved", "original"))
        ]);
        Assert.Empty(analysis.HiddenFiles);
        Assert.Equal(0, analysis.HiddenBytes);
        Assert.Equal(LayerChangeKind.Identical, Assert.Single(analysis.Layers[1].Changes).Kind);
    }

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

    private async Task<ExplorerSession> SessionAsync(params byte[][] blobs)
    {
        (ImageFileSystem files, _, ResolvedManifest resolved) = await CreateAsync(blobs);
        return new()
        {
            Image = Image,
            Resolved = resolved,
            Config = new Image { Os = "linux", Architecture = "amd64" },
            Files = files,
            Analysis = files.Analyze(),
            Entries = files.List(null, true, false),
            Packages = new InstalledPackageMetadata(Enum.GetValues<InstalledPackageEcosystem>()
                .ToDictionary(ecosystem => ecosystem, _ => new InstalledPackageEcosystemMetadata(
                    InstalledPackageMetadataAvailability.Unavailable,
                    new Dictionary<string, IReadOnlyList<string>>())))
        };
    }

    private static LayerChanges Layer(params ScannedEntry[] entries) => new(entries, [], []);

    private static ScannedEntry File(string path, long size, string hash) =>
        new(path, ImageFileType.File, 0x1A4, 0, 0, size, DateTime.UnixEpoch, null, 0, 0, 0, hash);

    private static ScannedEntry HardLink(string path, string target) =>
        File(path, 0, "") with { Type = ImageFileType.HardLink, LinkTarget = target };

    private static byte[] Blob(params (string Path, string Content)[] files) =>
        Archive(files.Select(file => (file.Path, file.Content, TarEntryType.RegularFile)).ToArray());

    private static byte[] Archive(params (string Path, string Value, TarEntryType Type)[] entries)
    {
        using MemoryStream result = new();
        using (GZipStream gzip = new(result, CompressionMode.Compress, leaveOpen: true))
        using (TarWriter writer = new(gzip, leaveOpen: true))
        {
            foreach ((string path, string value, TarEntryType type) in entries)
            {
                using MemoryStream data = new(Encoding.UTF8.GetBytes(value));
                PaxTarEntry entry = new(type, path);
                if (type == TarEntryType.RegularFile)
                {
                    entry.DataStream = data;
                }
                else
                {
                    entry.LinkName = value;
                }
                writer.WriteEntry(entry);
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
