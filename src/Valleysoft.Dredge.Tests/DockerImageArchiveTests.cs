using System.Formats.Tar;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Valleysoft.DockerRegistryClient;
using Valleysoft.DockerRegistryClient.Models.Manifests.Oci;

namespace Valleysoft.Dredge.Tests;

public class DockerImageArchiveTests : IDisposable
{
    private readonly string directory = Path.GetFullPath(Path.Combine(".test-docker-archives", Guid.NewGuid().ToString("N")));

    [Theory]
    [InlineData(false, "manifests", false)]
    [InlineData(false, "layers", false)]
    [InlineData(true, "manifests", false)]
    [InlineData(true, "layers", false)]
    [InlineData(true, "manifests", true)]
    [InlineData(true, "layers", true)]
    public async Task ConfigurationExtensionsAreNotClassifiedAsIndexesOrManifests(
        bool oci, string extension, bool filesystem)
    {
        DockerArchiveFixture fixture = DockerArchiveFixture.Create(oci: oci, gzip: filesystem);
        JsonObject config = JsonNode.Parse(fixture.Config)!.AsObject();
        config[extension] = new JsonArray();
        fixture = WithConfiguration(fixture, JsonSerializer.SerializeToUtf8Bytes(config), oci);

        await using DockerImageArchive archive = await DockerImageArchive.OpenAsync(
            new MemoryStream(fixture.Export), fixture.Id, TestContext.Current.CancellationToken,
            directory, prepareFileSystem: filesystem);

        Assert.Equal(fixture.Config, archive.ConfigurationJson);
        Assert.Single(archive.Resolved.Manifest.Layers);
        Assert.Equal(fixture.Id, archive.Resolved.Manifest.Config!.Digest);
    }

    [Theory]
    [InlineData(false, false, false, false)]
    [InlineData(false, false, true, false)]
    [InlineData(false, true, false, false)]
    [InlineData(false, true, true, false)]
    [InlineData(false, false, true, true)]
    [InlineData(false, true, true, true)]
    [InlineData(true, false, false, false)]
    [InlineData(true, false, true, false)]
    [InlineData(true, true, false, false)]
    [InlineData(true, true, true, false)]
    [InlineData(true, false, true, true)]
    [InlineData(true, true, true, true)]
    public async Task FileSystemAcquisitionReusesItsVerifiedScanAndSkipsScanningWarmIndexes(
        bool gzip, bool sha512, bool warm, bool damaged)
    {
        DockerArchiveFixture fixture = DockerArchiveFixture.Create(oci: true, gzip: gzip);
        if (sha512)
        {
            using Stream decoded = ImageTarReader.OpenContent(new MemoryStream(fixture.StoredLayer));
            using MemoryStream raw = new();
            await decoded.CopyToAsync(raw, TestContext.Current.CancellationToken);
            JsonObject config = JsonNode.Parse(fixture.Config)!.AsObject();
            config["rootfs"]!["diff_ids"]![0] = $"sha512:{Convert.ToHexStringLower(SHA512.HashData(raw.ToArray()))}";
            fixture = WithConfiguration(fixture, JsonSerializer.SerializeToUtf8Bytes(config), oci: true);
        }
        CacheFileSystem.CreateDirectory(directory);
        await using LayerStore store = new(Path.Combine(directory, "cache"));
        if (warm)
        {
            await using DockerImageArchive prior = await OpenAsync(fixture);
            string priorDigest = prior.Resolved.Manifest.Layers[0].Digest!;
            await store.GetIndexAsync(prior, new(0, priorDigest), fixture.StoredLayer.Length,
                TestContext.Current.CancellationToken);
            Assert.NotNull(await store.GetValidIndexAsync(priorDigest, fixture.StoredLayer.Length,
                TestContext.Current.CancellationToken));
            if (damaged)
            {
                string path = Assert.Single(Directory.GetFiles(Path.Combine(directory, "cache"), "*.index",
                    SearchOption.AllDirectories));
                await File.WriteAllTextAsync(path, "{invalid", TestContext.Current.CancellationToken);
            }
        }
        StoredLayerIndex? retained = null;
        int indexReads = 0;
        await using DockerImageArchive archive = await DockerImageArchive.OpenAsync(
            new MemoryStream(fixture.Export), fixture.Id, TestContext.Current.CancellationToken,
            directory, prepareFileSystem: true, hasLayerIndex: async (digest, size, token) =>
            {
                indexReads++;
                retained = await store.GetValidIndexAsync(digest, size, token);
                return retained;
            });
        string digest = archive.Resolved.Manifest.Layers[0].Digest!;
        StoredLayerIndex? prepared = archive.GetPreparedIndex(digest, fixture.StoredLayer.Length);
        Assert.Equal(!warm || damaged, prepared is not null);
        Assert.Equal(1, indexReads);
        Assert.Same(retained, archive.GetRetainedIndex(digest, fixture.StoredLayer.Length));
        Assert.True(archive.TryGetVerifiedContentDigest(digest, fixture.StoredLayer.Length,
            archive.Configuration.RootFilesystem!.DiffIds[0], out _));

        StoredLayerIndex index = await store.GetIndexAsync(archive, new(0, digest), fixture.StoredLayer.Length,
            TestContext.Current.CancellationToken);
        using MemoryStream blob = new(fixture.StoredLayer);
        LayerChanges expected = await ImageLayerScanner.ScanAsync(blob, new(0, digest),
            TestContext.Current.CancellationToken);
        Assert.Equal(expected.Entries, index.Changes.Entries);
        Assert.Equal(expected.Whiteouts, index.Changes.Whiteouts);
        Assert.Equal(expected.OpaqueDirectories, index.Changes.OpaqueDirectories);
        Assert.Same(prepared ?? retained, index);
        Assert.False(File.Exists(store.GetBlobPath(digest)));
        ScannedEntry entry = Assert.Single(index.Changes.Entries);
        using Stream content = await store.OpenIndexedBlobAsync(
            archive, index, [entry.EntryIndex], TestContext.Current.CancellationToken);
        using LayerContentReader reader = new(content, index.Encoding);
        using MemoryStream output = new();
        await reader.CopyToAsync(entry, output, TestContext.Current.CancellationToken);
        Assert.Equal("fixture"u8.ToArray(), output.ToArray());
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task FreshScansProveSingleAndBatchSelectionsWithoutTrustingChangedCoordinates(
        bool gzip, bool prepare)
    {
        byte[] payload = new byte[256_000];
        new Random(405).NextBytes(payload);
        byte[] raw = DockerArchiveFixture.BuildTar(("first", payload), (".wh.removed", []), ("second", payload));
        DockerArchiveFixture fixture = WithRawLayer(raw);
        if (gzip)
        {
            using MemoryStream compressed = new();
            using (GZipStream encoder = new(compressed, CompressionLevel.Fastest, leaveOpen: true))
            {
                encoder.Write(raw);
            }
            fixture = fixture with { StoredLayer = compressed.ToArray() };
            fixture = fixture with
            {
                Export = DockerArchiveFixture.BuildTar(
                    ("manifest.json", """[{"Config":"config.json","Layers":["layer.tar"]}]"""u8.ToArray()),
                    ("config.json", fixture.Config), ("layer.tar", fixture.StoredLayer))
            };
        }
        CacheFileSystem.CreateDirectory(directory);
        await using LayerStore store = new(Path.Combine(directory, "cache"));
        await using DockerImageArchive archive = await DockerImageArchive.OpenAsync(
            new MemoryStream(fixture.Export), fixture.Id, TestContext.Current.CancellationToken,
            directory, prepareFileSystem: prepare);
        string digest = archive.Resolved.Manifest.Layers[0].Digest!;
        StoredLayerIndex index = await store.GetIndexAsync(
            archive, new(0, digest), fixture.StoredLayer.Length, TestContext.Current.CancellationToken);
        Assert.Empty(Assert.IsAssignableFrom<System.Collections.IEnumerable>(typeof(DockerImageArchive)
            .GetField("verifiedEntries", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(archive)));
        ScannedEntry[] entries = [.. index.Changes.Entries];
        long prefix = entries.Max(entry => entry.CompressedHighWaterMark);

        foreach (ScannedEntry entry in entries)
        {
            Assert.Empty(archive.GetUnverifiedEntries(digest, index.Encoding, [entry], entry.CompressedHighWaterMark));
        }
        Assert.Empty(archive.GetUnverifiedEntries(digest, index.Encoding, entries, prefix));
        Assert.Single(archive.GetUnverifiedEntries(digest, index.Encoding,
            [entries[0] with { Size = entries[0].Size - 1 }], prefix));
        Assert.Single(archive.GetUnverifiedEntries(digest, index.Encoding,
            [entries[0] with { UncompressedOffset = entries[0].UncompressedOffset + 1 }], prefix));
        Assert.Single(archive.GetUnverifiedEntries(digest, index.Encoding,
            [entries[0] with { ContentHash = new string('f', 64) }], prefix));
        if (gzip)
        {
            Assert.Equal(entries.Length, archive.GetUnverifiedEntries(digest, index.Encoding, entries, prefix - 1).Length);
            Assert.Equal(entries.Length, archive.GetUnverifiedEntries(digest, index.Encoding, entries, prefix + 1).Length);
        }
        using Stream selection = await store.OpenIndexedBlobAsync(
            archive, index, entries.Select(entry => entry.EntryIndex), TestContext.Current.CancellationToken);
        using LayerContentReader reader = new(selection, index.Encoding);
        foreach (ScannedEntry entry in entries)
        {
            using MemoryStream output = new();
            await reader.CopyToAsync(entry, output, TestContext.Current.CancellationToken);
            Assert.Equal(payload, output.ToArray());
        }
        await using DockerImageArchive other = await OpenAsync(fixture);
        Assert.Equal(entries.Length, other.GetUnverifiedEntries(digest, index.Encoding, entries, prefix).Length);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LargeFreshScansRetainTheIndexWithoutPopulatingPerEntryProofs(bool prepare)
    {
        byte[] raw = DockerArchiveFixture.BuildTar([.. Enumerable.Range(0, 4096)
            .Select(i => ($"file{i}", new byte[] { 42 }))]);
        DockerArchiveFixture fixture = WithRawLayer(raw);
        CacheFileSystem.CreateDirectory(directory);
        await using LayerStore store = new(Path.Combine(directory, "cache"));
        await using DockerImageArchive archive = await DockerImageArchive.OpenAsync(
            new MemoryStream(fixture.Export), fixture.Id, TestContext.Current.CancellationToken,
            directory, prepareFileSystem: prepare);
        string digest = archive.Resolved.Manifest.Layers[0].Digest!;
        StoredLayerIndex index = await store.GetIndexAsync(
            archive, new(0, digest), fixture.StoredLayer.Length, TestContext.Current.CancellationToken);
        Assert.Empty(Assert.IsAssignableFrom<System.Collections.IEnumerable>(typeof(DockerImageArchive)
            .GetField("verifiedEntries", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(archive)));
        ScannedEntry[] selected = [index.Changes.Entries[0], index.Changes.Entries[^1]];
        Assert.Empty(archive.GetUnverifiedEntries(digest, index.Encoding, selected));

        long start = GC.GetAllocatedBytesForCurrentThread();
        ScannedEntry[] unverified = archive.GetUnverifiedEntries(digest, index.Encoding, selected);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - start;

        Assert.Empty(unverified);
        Assert.True(allocated < 4096, $"A two-file proof lookup allocated {allocated:N0} bytes.");
        await archive.DisposeAsync();
        archive.RetainScannedIndex(index);
        Assert.Throws<ObjectDisposedException>(() => archive.GetUnverifiedEntries(digest, index.Encoding, selected));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ContentOnlyAcquisitionDoesNotEagerlyPrepareFilesystemIndexes(bool metadata)
    {
        DockerArchiveFixture fixture = DockerArchiveFixture.Create(oci: true, gzip: true);
        await using DockerImageArchive archive = await DockerImageArchive.OpenAsync(
            new MemoryStream(fixture.Export), fixture.Id, TestContext.Current.CancellationToken,
            directory, verifyLayerContent: !metadata);
        Assert.Null(archive.GetPreparedIndex(archive.Resolved.Manifest.Layers[0].Digest!, fixture.StoredLayer.Length));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ClassicRawAcquisitionUsesKnownSha256ForWarmLookupButScansUnknownSha512(bool sha512)
    {
        DockerArchiveFixture fixture = WithRawLayer(DockerArchiveFixture.BuildTar(("file", [1, 2, 3])), sha512);
        int lookups = 0;
        await using DockerImageArchive archive = await DockerImageArchive.OpenAsync(
            new MemoryStream(fixture.Export), fixture.Id, TestContext.Current.CancellationToken,
            directory, prepareFileSystem: true, hasLayerIndex: (digest, size, token) =>
            {
                token.ThrowIfCancellationRequested();
                Assert.Equal(DockerImageArchive.Hash(fixture.StoredLayer), digest);
                Assert.Equal(fixture.StoredLayer.Length, size);
                lookups++;
                return Task.FromResult<StoredLayerIndex?>(null);
            });

        string digest = archive.Resolved.Manifest.Layers[0].Digest!;
        Assert.Equal(sha512 ? 0 : 1, lookups);
        StoredLayerIndex index = Assert.IsType<StoredLayerIndex>(archive.GetPreparedIndex(digest, fixture.StoredLayer.Length));
        Assert.Equal("tar", index.Encoding);
        Assert.True(archive.TryGetVerifiedContentDigest(digest, fixture.StoredLayer.Length,
            archive.Configuration.RootFilesystem!.DiffIds[0], out _));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FusedScanPreservesEntryCoordinatesWhiteoutsAndEncodedAndDecodedTails(bool gzip)
    {
        byte[] payload = new byte[256_000];
        new Random(405).NextBytes(payload);
        byte[] raw = [.. DockerArchiveFixture.BuildTar(
            ("directory/data", payload), ("directory/.wh.deleted", []), ("opaque/.wh..wh..opq", [])),
            .. "decoded trailing bytes"u8];
        DockerArchiveFixture fixture = WithRawLayer(raw);
        using MemoryStream compressed = new();
        using (GZipStream encoder = new(compressed, CompressionLevel.Fastest, leaveOpen: true))
        {
            encoder.Write(raw);
        }
        byte[] stored = gzip ? [.. compressed.ToArray(), .. new byte[128]] : raw;
        fixture = fixture with
        {
            StoredLayer = stored,
            Export = DockerArchiveFixture.BuildTar(
                ("manifest.json", """[{"Config":"config.json","Layers":["layer.tar"]}]"""u8.ToArray()),
                ("config.json", fixture.Config), ("layer.tar", stored))
        };
        await using DockerImageArchive archive = await DockerImageArchive.OpenAsync(
            new MemoryStream(fixture.Export), fixture.Id, TestContext.Current.CancellationToken,
            directory, prepareFileSystem: true);
        string digest = DockerImageArchive.Hash(stored);
        StoredLayerIndex index = Assert.IsType<StoredLayerIndex>(archive.GetPreparedIndex(digest, stored.Length));
        using MemoryStream blob = new(stored);
        LayerChanges expected = await ImageLayerScanner.ScanAsync(blob, new(0, digest), TestContext.Current.CancellationToken);
        Assert.Equal(expected.Entries, index.Changes.Entries);
        Assert.Equal(expected.Whiteouts, index.Changes.Whiteouts);
        Assert.Equal(expected.OpaqueDirectories, index.Changes.OpaqueDirectories);
        Assert.Equal(["directory/deleted"], index.Changes.Whiteouts);
        Assert.Equal(["opaque"], index.Changes.OpaqueDirectories);
        Assert.True(archive.TryGetVerifiedContentDigest(digest, stored.Length, DockerImageArchive.Hash(raw), out _));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FusedAcquisitionRejectsWrongDiffIdsBeforeExposingPreparedIndexes(bool gzip)
    {
        DockerArchiveFixture fixture = DockerArchiveFixture.Create(oci: true, gzip: gzip);
        JsonObject config = JsonNode.Parse(fixture.Config)!.AsObject();
        config["rootfs"]!["diff_ids"]![0] = "sha512:" + new string('f', 128);
        fixture = WithConfiguration(fixture, JsonSerializer.SerializeToUtf8Bytes(config), oci: true);
        InvalidDataException error = await Assert.ThrowsAsync<InvalidDataException>(() => DockerImageArchive.OpenAsync(
            new MemoryStream(fixture.Export), fixture.Id, TestContext.Current.CancellationToken,
            directory, prepareFileSystem: true));
        Assert.Contains("diff ID", error.Message);
        Assert.Empty(Directory.GetDirectories(directory));
    }

    [Fact]
    public async Task RawEntryValidationProofIsBoundToArchiveAndExactIndexCoordinates()
    {
        DockerArchiveFixture fixture = WithRawLayer(DockerArchiveFixture.BuildTar(
            ("first", "first"u8.ToArray()), ("second", "second"u8.ToArray())));
        await using DockerImageArchive seed = await OpenAsync(fixture);
        await using LayerStore store = new(Path.Combine(directory, "cache"));
        string digest = seed.Resolved.Manifest.Layers[0].Digest!;
        StoredLayerIndex index = await store.GetIndexAsync(seed, new(0, digest), fixture.StoredLayer.Length,
            TestContext.Current.CancellationToken);
        await using DockerImageArchive archive = await OpenAsync(fixture);
        ScannedEntry first = index.Changes.Entries[0];
        ScannedEntry second = index.Changes.Entries[1];
        Assert.Equal(2, archive.GetUnverifiedEntries(digest, "tar", index.Changes.Entries).Length);
        using (await store.OpenIndexedBlobAsync(archive, index, [first.EntryIndex], TestContext.Current.CancellationToken)) { }
        Assert.Equal([second], archive.GetUnverifiedEntries(digest, "tar", index.Changes.Entries));
        using Stream repeated = await store.OpenIndexedBlobAsync(archive, index, [first.EntryIndex],
            TestContext.Current.CancellationToken);
        Assert.Empty(archive.GetUnverifiedEntries(digest, "tar", [first]));
        Assert.Single(archive.GetUnverifiedEntries(digest, "gzip", [first]));
        Assert.Single(archive.GetUnverifiedEntries(digest, "tar", [first with { Size = first.Size - 1 }]));
        Assert.Single(archive.GetUnverifiedEntries(digest, "tar", [first with { UncompressedOffset = second.UncompressedOffset }]));
        ScannedEntry corrupt = first with { ContentHash = new string('a', 64) };
        Assert.Single(archive.GetUnverifiedEntries(digest, "tar", [corrupt]));
        StoredLayerIndex invalid = new(digest, index.BlobLength, new([corrupt, second], [], []), "tar");
        using (await store.OpenIndexedBlobAsync(archive, invalid, [corrupt.EntryIndex], TestContext.Current.CancellationToken)) { }
        Assert.Single(archive.GetUnverifiedEntries(digest, "tar", [corrupt]));
        await using DockerImageArchive other = await OpenAsync(fixture);
        Assert.Single(other.GetUnverifiedEntries(digest, "tar", [first]));
        await archive.DisposeAsync();
        using LayerContentReader reader = new(repeated, "tar");
        using MemoryStream output = new();
        await reader.CopyToAsync(first, output, TestContext.Current.CancellationToken);
        Assert.Equal("first"u8.ToArray(), output.ToArray());
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task ValidExport_PreservesExactConfigAndVerifiedLayers(bool oci, bool gzip)
    {
        DockerArchiveFixture fixture = DockerArchiveFixture.Create(oci: oci, gzip: gzip);
        await using DockerImageArchive archive = await OpenAsync(fixture);
        Assert.Equal(fixture.Config, archive.ConfigurationJson);
        Assert.Equal("linux", archive.Configuration.Os);
        Assert.Equal("amd64", archive.Configuration.Architecture);
        Assert.Equal(["X=preserved"], archive.Configuration.Config!.EnvironmentVariables);
        Assert.NotEqual(fixture.Id, archive.Resolved.ManifestInfo.DockerContentDigest);
        Assert.Equal(DockerImageArchive.Hash(archive.Resolved.ManifestInfo.Content.Span),
            archive.Resolved.ManifestInfo.DockerContentDigest);
        Assert.Single(archive.Resolved.Manifest.Layers);
        string digest = archive.Resolved.Manifest.Layers[0].Digest!;
        Assert.Equal(DockerImageArchive.Hash(fixture.StoredLayer), digest);
        Assert.Equal(gzip ? "application/vnd.oci.image.layer.v1.tar+gzip" : DockerImageArchive.RawLayerMediaType,
            archive.Resolved.Manifest.Layers[0].MediaType);
        BlobDownloadResult result = await archive.GetRangeAsync(digest, 3, 10, TestContext.Current.CancellationToken);
        await using Stream content = result.Content;
        using MemoryStream buffer = new();
        await content.CopyToAsync(buffer, TestContext.Current.CancellationToken);
        Assert.Equal(fixture.StoredLayer[3..13], buffer.ToArray());
        Assert.True(result.IsRangeHonored);
        Assert.Equal(3, result.RangeStart);
        Assert.Equal(12, result.RangeEnd);
        Assert.Equal(fixture.StoredLayer.Length, result.TotalLength);
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, false, true)]
    [InlineData(false, true, true)]
    [InlineData(true, false, false)]
    [InlineData(true, false, true)]
    [InlineData(true, true, true)]
    public async Task Sha512DiffIdsPreserveConfigurationAndVerifyContent(bool oci, bool gzip, bool verifyContent)
    {
        DockerArchiveFixture fixture = DockerArchiveFixture.Create(oci: oci, gzip: gzip);
        using Stream decoded = ImageTarReader.OpenContent(new MemoryStream(fixture.StoredLayer));
        string diffId = "sha512:" + Convert.ToHexStringLower(
            await SHA512.HashDataAsync(decoded, TestContext.Current.CancellationToken));
        JsonObject config = JsonNode.Parse(fixture.Config)!.AsObject();
        config["rootfs"]!["diff_ids"]![0] = diffId;
        fixture = WithConfiguration(fixture, JsonSerializer.SerializeToUtf8Bytes(config), oci);

        await using DockerImageArchive archive = await DockerImageArchive.OpenAsync(
            new MemoryStream(fixture.Export), fixture.Id, TestContext.Current.CancellationToken, directory,
            verifyLayerContent: verifyContent);

        Assert.Equal(fixture.Config, archive.ConfigurationJson);
        Assert.Equal(diffId, Assert.Single(archive.Configuration.RootFilesystem!.DiffIds));
        Assert.Equal(DockerImageArchive.Hash(fixture.StoredLayer), archive.Resolved.Manifest.Layers[0].Digest);
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, false, true)]
    [InlineData(false, true, false)]
    [InlineData(false, true, true)]
    [InlineData(true, false, false)]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    [InlineData(true, true, true)]
    public async Task VerifiedArchiveContentIdentitiesAvoidAnotherLayerCopy(
        bool gzip, bool sha512, bool verifyContent)
    {
        DockerArchiveFixture fixture = DockerArchiveFixture.Create(oci: true, gzip: gzip);
        using Stream decoded = ImageTarReader.OpenContent(new MemoryStream(fixture.StoredLayer));
        using MemoryStream raw = new();
        await decoded.CopyToAsync(raw, TestContext.Current.CancellationToken);
        if (sha512)
        {
            JsonObject config = JsonNode.Parse(fixture.Config)!.AsObject();
            config["rootfs"]!["diff_ids"]![0] = "sha512:" +
                Convert.ToHexStringLower(SHA512.HashData(raw.ToArray()));
            fixture = WithConfiguration(fixture, JsonSerializer.SerializeToUtf8Bytes(config), true);
        }
        await using DockerImageArchive archive = await DockerImageArchive.OpenAsync(
            new MemoryStream(fixture.Export), fixture.Id, TestContext.Current.CancellationToken,
            directory, verifyLayerContent: verifyContent);
        await using LayerStore store = new(Path.Combine(directory, "cache"));

        string[] identities = await ExplorerContentIdentity.VerifyAsync(
            archive, archive.Configuration, archive.Resolved, store, TestContext.Current.CancellationToken);

        Assert.Equal(DockerImageArchive.Hash(raw.ToArray()), Assert.Single(identities));
        Assert.False(File.Exists(store.GetBlobPath(archive.Resolved.Manifest.Layers[0].Digest!)));
        Assert.Equal(fixture.Config, archive.ConfigurationJson);
        Assert.Equal(identities, await ExplorerContentIdentity.VerifyAsync(
            archive, archive.Configuration, archive.Resolved, store, TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task OciSharedGraphRequiresUniqueEffectivePlatform(bool nested, bool reverse)
    {
        DockerArchiveFixture fixture = DockerArchiveFixture.Create(oci: true, architecture: "amd64");
        Dictionary<string, byte[]> entries = ReadEntries(fixture.Export);
        JsonObject index = JsonNode.Parse(entries["index.json"])!.AsObject();
        JsonObject first = index["manifests"]![0]!.AsObject();
        if (nested)
        {
            first = AddIndex(entries, JsonSerializer.SerializeToUtf8Bytes(index.DeepClone()));
            index["manifests"]![0] = first;
        }
        first["platform"] = new JsonObject
        {
            ["os"] = "linux",
            ["architecture"] = "amd64",
            ["variant"] = reverse ? "v2" : "v1"
        };
        JsonObject second = first.DeepClone().AsObject();
        second["platform"]!["variant"] = reverse ? "v1" : "v2";
        index["manifests"]!.AsArray().Add(second);
        entries["index.json"] = JsonSerializer.SerializeToUtf8Bytes(index);
        string id = DockerImageArchive.Hash(entries["index.json"]);
        entries["blobs/sha256/" + id[7..]] = entries["index.json"];

        InvalidDataException error = await Assert.ThrowsAsync<InvalidDataException>(() =>
            DockerImageArchive.OpenAsync(new MemoryStream(BuildEntries(entries)), id,
                TestContext.Current.CancellationToken, directory, "linux", "amd64"));

        Assert.Contains("ambiguous", error.Message);
        Assert.Empty(Directory.EnumerateFileSystemEntries(directory));
    }

    [Theory]
    [InlineData(false, "same")]
    [InlineData(true, "same")]
    [InlineData(false, "case")]
    [InlineData(true, "case")]
    [InlineData(false, "inspection")]
    [InlineData(true, "inspection")]
    [InlineData(false, "configuration")]
    [InlineData(true, "configuration")]
    [InlineData(false, "missing")]
    [InlineData(true, "missing")]
    public async Task OciSharedGraphAcceptsEquivalentOrUniquelySelectedPlatforms(bool nested, string context)
    {
        DockerArchiveFixture fixture = DockerArchiveFixture.Create(oci: true);
        if (context == "configuration")
        {
            JsonObject config = JsonNode.Parse(fixture.Config)!.AsObject();
            config["variant"] = "v1";
            fixture = WithConfiguration(fixture, JsonSerializer.SerializeToUtf8Bytes(config), true);
        }
        Dictionary<string, byte[]> entries = ReadEntries(fixture.Export);
        JsonObject index = JsonNode.Parse(entries["index.json"])!.AsObject();
        JsonObject first = index["manifests"]![0]!.AsObject();
        if (nested)
        {
            first = AddIndex(entries, JsonSerializer.SerializeToUtf8Bytes(index.DeepClone()));
            index["manifests"]![0] = first;
        }
        first["platform"] = new JsonObject
        {
            ["os"] = "linux",
            ["architecture"] = "amd64",
            ["variant"] = context == "missing" ? null : "v1"
        };
        JsonObject second = first.DeepClone().AsObject();
        second["platform"]!["variant"] = context is "inspection" or "configuration" ? "v2" : "v1";
        if (context == "case")
        {
            second["platform"]!["os"] = "LINUX";
            second["platform"]!["architecture"] = "AMD64";
            second["platform"]!["variant"] = "V1";
            second["platform"]!["os.version"] = "";
        }
        index["manifests"]!.AsArray().Add(second);
        entries["index.json"] = JsonSerializer.SerializeToUtf8Bytes(index);
        string id = DockerImageArchive.Hash(entries["index.json"]);
        entries["blobs/sha256/" + id[7..]] = entries["index.json"];

        await using DockerImageArchive archive = await DockerImageArchive.OpenAsync(
            new MemoryStream(BuildEntries(entries)), id, TestContext.Current.CancellationToken,
            directory, "linux", "amd64", variant: context is "inspection" or "missing" ? "v1" : null);

        Assert.Equal("v1", archive.Platform.Variant);
        Assert.Equal(fixture.Config, archive.ConfigurationJson);
    }

    [Theory]
    [InlineData(false, false, false, "v8")]
    [InlineData(false, false, true, "v8")]
    [InlineData(false, true, false, "v8")]
    [InlineData(false, true, true, "v8")]
    [InlineData(true, false, false, "v8")]
    [InlineData(true, false, true, "v8")]
    [InlineData(true, true, false, "v8")]
    [InlineData(true, true, true, "v8")]
    [InlineData(false, false, false, "v9")]
    [InlineData(false, true, true, "v9")]
    [InlineData(true, false, true, "v9")]
    [InlineData(true, true, false, "v9")]
    public async Task OciSharedArm64GraphAcceptsOmittedAndDefaultVariant(
        bool nested, bool pinnedIndex, bool reverse, string variant)
    {
        DockerArchiveFixture fixture = DockerArchiveFixture.Create(oci: true, architecture: "arm64");
        Dictionary<string, byte[]> entries = ReadEntries(fixture.Export);
        JsonObject index = JsonNode.Parse(entries["index.json"])!.AsObject();
        JsonObject first = index["manifests"]![0]!.AsObject();
        if (nested)
        {
            first = AddIndex(entries, JsonSerializer.SerializeToUtf8Bytes(index.DeepClone()));
            index["manifests"]![0] = first;
        }
        first["platform"] = new JsonObject
        {
            ["os"] = "linux",
            ["architecture"] = "arm64",
            ["variant"] = reverse ? variant : null
        };
        JsonObject second = first.DeepClone().AsObject();
        second["platform"]!["variant"] = reverse ? null : variant;
        index["manifests"]!.AsArray().Add(second);
        entries["index.json"] = JsonSerializer.SerializeToUtf8Bytes(index);
        string id = pinnedIndex ? DockerImageArchive.Hash(entries["index.json"]) : fixture.Id;
        if (pinnedIndex)
        {
            entries["blobs/sha256/" + id[7..]] = entries["index.json"];
        }

        Task<DockerImageArchive> acquisition = DockerImageArchive.OpenAsync(
            new MemoryStream(BuildEntries(entries)), id, TestContext.Current.CancellationToken,
            directory, "linux", "arm64");
        if (variant != "v8")
        {
            InvalidDataException error = await Assert.ThrowsAsync<InvalidDataException>(() => acquisition);
            Assert.Contains("ambiguous", error.Message);
            Assert.Empty(Directory.EnumerateFileSystemEntries(directory));
            return;
        }
        await using DockerImageArchive archive = await acquisition;

        Assert.True(archive.Platform.IsEquivalentTo(new("linux", "arm64", "v8", null)));
        Assert.Equal(fixture.Config, archive.ConfigurationJson);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task DaemonIndexScansArchiveWithoutPublishingBlobAndSupportsIndexedReads(
        bool gzip, bool verifyContent)
    {
        DockerArchiveFixture fixture = DockerArchiveFixture.Create(oci: true, gzip: gzip);
        await using DockerImageArchive archive = await DockerImageArchive.OpenAsync(
            new MemoryStream(fixture.Export), fixture.Id, TestContext.Current.CancellationToken,
            directory, verifyLayerContent: verifyContent);
        string digest = archive.Resolved.Manifest.Layers[0].Digest!;
        string cacheRoot = Path.Combine(directory, "cache");
        StoredLayerIndex index;
        await using (LayerStore store = new(cacheRoot))
        {
            index = await store.GetIndexAsync(archive, new(0, digest), fixture.StoredLayer.Length,
                TestContext.Current.CancellationToken, expectedDiffId: archive.Configuration.RootFilesystem!.DiffIds[0]);
            Assert.False(File.Exists(store.GetBlobPath(digest)));
            Assert.Single(index.Changes.Entries);
            await Assert.ThrowsAsync<InvalidDataException>(() => store.GetIndexAsync(
                archive, new(0, digest), fixture.StoredLayer.Length + 1, TestContext.Current.CancellationToken));
        }
        await using LayerStore restored = new(cacheRoot);
        StoredLayerIndex persisted = await restored.GetIndexAsync(
            archive, new(0, digest), fixture.StoredLayer.Length, TestContext.Current.CancellationToken);
        Assert.Equal(index.Changes.Entries, persisted.Changes.Entries);
        using Stream blob = await restored.OpenIndexedBlobAsync(
            archive, persisted, [persisted.Changes.Entries[0].EntryIndex], TestContext.Current.CancellationToken);
        using LayerContentReader reader = new(blob, persisted.Encoding);
        using MemoryStream output = new();
        await reader.CopyToAsync(persisted.Changes.Entries[0], output, TestContext.Current.CancellationToken);
        Assert.Equal("fixture"u8.ToArray(), output.ToArray());
        Assert.False(File.Exists(restored.GetBlobPath(digest)));
        Assert.Empty(Directory.GetFiles(Path.Combine(cacheRoot, "layer-store", "data"), "*.tmp"));
    }

    [Fact]
    public async Task SnapshotUsesTheVerifiedFileHandleAfterPathReplacement()
    {
        DockerArchiveFixture fixture = DockerArchiveFixture.Create();
        await using DockerImageArchive archive = await OpenAsync(fixture);
        string spool = Assert.Single(Directory.GetFiles(directory, "export.tar", SearchOption.AllDirectories));
        if (OperatingSystem.IsWindows())
        {
            Assert.Throws<IOException>(() => File.Open(spool, FileMode.Open, FileAccess.Write).Dispose());
        }
        File.Move(spool, spool + ".original");
        await File.WriteAllBytesAsync(spool, new byte[fixture.Export.Length], TestContext.Current.CancellationToken);
        string digest = archive.Resolved.Manifest.Layers[0].Digest!;
        await using LayerStore store = new(Path.Combine(directory, "cache"));
        StoredLayerIndex index = await store.GetIndexAsync(archive, new(0, digest), fixture.StoredLayer.Length,
            TestContext.Current.CancellationToken, expectedDiffId: archive.Configuration.RootFilesystem!.DiffIds[0]);
        ScannedEntry entry = Assert.Single(index.Changes.Entries);
        using Stream content = await store.OpenIndexedBlobAsync(archive, index, [entry.EntryIndex],
            TestContext.Current.CancellationToken);
        await archive.DisposeAsync();
        using LayerContentReader reader = new(content, index.Encoding);
        using MemoryStream output = new();
        await reader.CopyToAsync(entry, output, TestContext.Current.CancellationToken);
        Assert.Equal("fixture"u8.ToArray(), output.ToArray());
    }

    [Fact]
    public async Task SharedSnapshotHandleKeepsConcurrentRangePositionsIndependent()
    {
        DockerArchiveFixture fixture = DockerArchiveFixture.Create();
        await using DockerImageArchive archive = await OpenAsync(fixture);
        string digest = archive.Resolved.Manifest.Layers[0].Digest!;
        BlobDownloadResult first = await archive.GetRangeAsync(digest, 0, 512, TestContext.Current.CancellationToken);
        BlobDownloadResult second = await archive.GetRangeAsync(digest, 512, 512, TestContext.Current.CancellationToken);
        using Stream firstContent = first.Content;
        using Stream secondContent = second.Content;
        await archive.DisposeAsync();
        byte[] header = new byte[512];
        byte[] data = new byte[512];
        await Task.WhenAll(
            firstContent.ReadExactlyAsync(header, TestContext.Current.CancellationToken).AsTask(),
            secondContent.ReadExactlyAsync(data, TestContext.Current.CancellationToken).AsTask());
        Assert.Equal(fixture.StoredLayer[..512], header);
        Assert.Equal(fixture.StoredLayer[512..1024], data);
        firstContent.Dispose();
        secondContent.Position = 0;
        await secondContent.ReadExactlyAsync(data, TestContext.Current.CancellationToken);
        Assert.Equal(fixture.StoredLayer[512..1024], data);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DaemonSelectionBypassesAnUnpinnedCompleteCache(bool gzip)
    {
        DockerArchiveFixture fixture = DockerArchiveFixture.Create(oci: true, gzip: gzip);
        await using DockerImageArchive archive = await OpenAsync(fixture);
        await using LayerStore store = new(Path.Combine(directory, "cache"));
        string digest = archive.Resolved.Manifest.Layers[0].Digest!;
        StoredLayerIndex index = await store.GetIndexAsync(archive, new(0, digest), fixture.StoredLayer.Length,
            TestContext.Current.CancellationToken);
        byte[] unrelated = new byte[fixture.StoredLayer.Length];
        await File.WriteAllBytesAsync(store.GetBlobPath(digest), unrelated, TestContext.Current.CancellationToken);
        ScannedEntry entry = Assert.Single(index.Changes.Entries);
        using Stream content = await store.OpenIndexedBlobAsync(archive, index, [entry.EntryIndex],
            TestContext.Current.CancellationToken);
        using LayerContentReader reader = new(content, index.Encoding);
        using MemoryStream output = new();
        await reader.CopyToAsync(entry, output, TestContext.Current.CancellationToken);
        Assert.Equal("fixture"u8.ToArray(), output.ToArray());
        Assert.Equal(unrelated, await File.ReadAllBytesAsync(store.GetBlobPath(digest), TestContext.Current.CancellationToken));
        if (!gzip)
        {
            Assert.Throws<InvalidDataException>(() => content.Seek(0, SeekOrigin.Begin));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HealthyDaemonSelectionsDoNotWaitForTheCachePublicationLock(bool gzip)
    {
        DockerArchiveFixture fixture = DockerArchiveFixture.Create(oci: true, gzip: gzip);
        await using DockerImageArchive archive = await OpenAsync(fixture);
        string root = Path.Combine(directory, "cache");
        await using LayerStore store = new(root);
        string digest = archive.Resolved.Manifest.Layers[0].Digest!;
        StoredLayerIndex index = await store.GetIndexAsync(archive, new(0, digest), fixture.StoredLayer.Length,
            TestContext.Current.CancellationToken);
        string stripe = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(LayerStore.GetKey(digest))))[..2];
        using FileStream cacheLock = new(Path.Combine(root, "layer-store", "locks", $"{stripe}.lock"),
            FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        using CancellationTokenSource cancellation = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken);
        cancellation.CancelAfter(TimeSpan.FromSeconds(5));
        int ordinal = Assert.Single(index.Changes.Entries).EntryIndex;

        Task<Stream> first = store.OpenIndexedBlobAsync(archive, index, [ordinal], cancellation.Token);
        Task<Stream> second = store.OpenIndexedBlobAsync(archive, index, [ordinal], cancellation.Token);
        using Stream firstContent = await first;
        using Stream secondContent = await second;
        using LayerContentReader reader = new(secondContent, index.Encoding);
        using MemoryStream output = new();
        await reader.CopyToAsync(index.Changes.Entries[0], output, cancellation.Token);
        Assert.Equal("fixture"u8.ToArray(), output.ToArray());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InvalidDaemonSelectionStillWaitsForTheCachePublicationLock(bool gzip)
    {
        DockerArchiveFixture fixture = DockerArchiveFixture.Create(oci: true, gzip: gzip);
        await using DockerImageArchive archive = await OpenAsync(fixture);
        string root = Path.Combine(directory, "cache");
        await using LayerStore store = new(root);
        string digest = archive.Resolved.Manifest.Layers[0].Digest!;
        StoredLayerIndex index = await store.GetIndexAsync(archive, new(0, digest), fixture.StoredLayer.Length,
            TestContext.Current.CancellationToken);
        ScannedEntry original = Assert.Single(index.Changes.Entries);
        StoredLayerIndex invalid = new(index.Digest, index.BlobLength,
            new([original with { ContentHash = new string('a', 64) }], [], []), index.Encoding);
        string stripe = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(LayerStore.GetKey(digest))))[..2];
        using FileStream cacheLock = new(Path.Combine(root, "layer-store", "locks", $"{stripe}.lock"),
            FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        using CancellationTokenSource cancellation = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken);
        cancellation.CancelAfter(TimeSpan.FromMilliseconds(250));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.OpenIndexedBlobAsync(
            archive, invalid, [original.EntryIndex], cancellation.Token));

        Assert.False(File.Exists(store.GetBlobPath(digest)));
        cacheLock.Dispose();
        using Stream fallback = await store.OpenIndexedBlobAsync(
            archive, invalid, [original.EntryIndex], TestContext.Current.CancellationToken);
        Assert.True(File.Exists(store.GetBlobPath(digest)));
        Assert.Equal(fixture.StoredLayer.Length, fallback.Length);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task MetadataArchiveDirectVerificationRejectsInvalidDescriptors(bool gzip, bool sha512)
    {
        DockerArchiveFixture fixture = DockerArchiveFixture.Create(oci: true, gzip: gzip);
        using Stream decoded = ImageTarReader.OpenContent(new MemoryStream(fixture.StoredLayer));
        using MemoryStream raw = new();
        await decoded.CopyToAsync(raw, TestContext.Current.CancellationToken);
        string identity = DockerImageArchive.Hash(raw.ToArray());
        string diffId = sha512 ? $"sha512:{Convert.ToHexStringLower(SHA512.HashData(raw.ToArray()))}" : identity;
        await using DockerImageArchive archive = await DockerImageArchive.OpenAsync(
            new MemoryStream(fixture.Export), fixture.Id, TestContext.Current.CancellationToken,
            directory, verifyLayerContent: false);
        string digest = archive.Resolved.Manifest.Layers[0].Digest!;

        Assert.Equal(identity, await archive.VerifyContentAsync(digest, fixture.StoredLayer.Length, diffId,
            TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<InvalidDataException>(() => archive.VerifyContentAsync(
            digest, fixture.StoredLayer.Length + 1, diffId, TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<InvalidDataException>(() => archive.VerifyContentAsync(
            "sha256:" + new string('a', 64), fixture.StoredLayer.Length, diffId, TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<InvalidDataException>(() => archive.VerifyContentAsync(
            digest, fixture.StoredLayer.Length, "sha256:" + new string('a', 64), TestContext.Current.CancellationToken));
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => archive.VerifyContentAsync(
            digest, fixture.StoredLayer.Length, diffId, cancellation.Token));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task SavingDaemonLayersDoesNotPublishCompleteBlobs(bool gzip, bool noSquash)
    {
        DockerArchiveFixture fixture = DockerArchiveFixture.Create(oci: true, gzip: gzip);
        await using DockerImageArchive archive = await OpenAsync(fixture);
        string reference = "docker://fixture:local";
        await using ResolvedImage source = new(ImageSourceReference.Parse(reference), ImageName.Parse("fixture:local"),
            archive.Resolved, null, archive.Configuration, archive.ConfigurationJson, archive,
            fixture.Id, "linux/amd64");
        await using LayerCacheTestContext cache = new();
        string output = Path.Combine(cache.Root, "saved");
        Moq.Mock<IDockerRegistryClientFactory> registry = new(Moq.MockBehavior.Strict);

        await ImageHelper.SaveImageLayersToDiskAsync(registry.Object, reference, output, null,
            "--layer-index", noSquash, new Valleysoft.Dredge.Commands.PlatformOptionsBase(),
            TestContext.Current.CancellationToken, cache.Paths, resolvedImage: source);

        string digest = archive.Resolved.Manifest.Layers[0].Digest!;
        string extracted = noSquash ? Path.Combine(output, $"layer0-{digest[7..]}") : output;
        Assert.Equal("fixture", await File.ReadAllTextAsync(Path.Combine(extracted, "etc", "example"),
            TestContext.Current.CancellationToken));
        Assert.False(File.Exists(cache.Store.GetBlobPath(digest)));
        Assert.Empty(Directory.GetFiles(Path.Combine(cache.Paths.CachePath, "layer-store", "data"), "*.tmp"));
    }

    [Fact]
    public async Task GzipDaemonSelectedReaderOutlivesTheArchiveWithoutScratch()
    {
        DockerArchiveFixture fixture = DockerArchiveFixture.Create(oci: true, gzip: true);
        await using DockerImageArchive archive = await OpenAsync(fixture);
        string root = Path.Combine(directory, "cache");
        await using LayerStore store = new(root);
        string digest = archive.Resolved.Manifest.Layers[0].Digest!;
        StoredLayerIndex index = await store.GetIndexAsync(archive, new(0, digest), fixture.StoredLayer.Length,
            TestContext.Current.CancellationToken);
        ScannedEntry entry = Assert.Single(index.Changes.Entries);
        using Stream content = await store.OpenIndexedBlobAsync(archive, index, [entry.EntryIndex],
            TestContext.Current.CancellationToken);
        Assert.Empty(Directory.GetFiles(Path.Combine(root, "layer-store", "data"), "*.tmp"));
        Assert.False(File.Exists(store.GetBlobPath(digest)));
        await archive.DisposeAsync();
        await store.DisposeAsync();
        using LayerContentReader reader = new(content, index.Encoding);
        using MemoryStream output = new();
        await reader.CopyToAsync(entry, output, TestContext.Current.CancellationToken);
        Assert.Equal("fixture"u8.ToArray(), output.ToArray());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RawDaemonIndexedReadsDoNotStageAndOutliveTheirOwners(bool verifyContent)
    {
        byte[] selected = new byte[2 * 1024 * 1024 + 17];
        selected.AsSpan().Fill(42);
        DockerArchiveFixture fixture = WithRawLayer(DockerArchiveFixture.BuildTar(
            ("first", "first"u8.ToArray()), ("unselected", new byte[4096]),
            ("last", selected), ("empty", [])));
        await using DockerImageArchive archive = await DockerImageArchive.OpenAsync(
            new MemoryStream(fixture.Export), fixture.Id, TestContext.Current.CancellationToken,
            Path.Combine(directory, "archive"), verifyLayerContent: verifyContent);
        string cacheRoot = Path.Combine(directory, "cache");
        await using LayerStore store = new(cacheRoot);
        string digest = archive.Resolved.Manifest.Layers[0].Digest!;
        StoredLayerIndex index = await store.GetIndexAsync(archive, new(0, digest), fixture.StoredLayer.Length,
            TestContext.Current.CancellationToken, expectedDiffId: archive.Configuration.RootFilesystem!.DiffIds[0]);
        ScannedEntry[] entries = [.. index.Changes.Entries.Where(entry => entry.Path != "unselected")];
        using Stream blob = await store.OpenIndexedBlobAsync(
            archive, index, entries.Select(entry => entry.EntryIndex), TestContext.Current.CancellationToken);

        Assert.Empty(Directory.GetFiles(Path.Combine(cacheRoot, "layer-store", "data"), "*.tmp"));
        Assert.False(File.Exists(store.GetBlobPath(digest)));
        long gap = index.Changes.Entries.Single(entry => entry.Path == "unselected").UncompressedOffset;
        Assert.Throws<InvalidDataException>(() => blob.Seek(gap, SeekOrigin.Begin));
        ScannedEntry first = entries.Single(entry => entry.Path == "first");
        blob.Position = first.UncompressedOffset;
        byte[] beyondExtent = new byte[first.Size + 100];
        Assert.Equal(first.Size, await blob.ReadAsync(beyondExtent, TestContext.Current.CancellationToken));
        Assert.Equal(0, await blob.ReadAsync(beyondExtent, TestContext.Current.CancellationToken));

        await store.DisposeAsync();
        await archive.DisposeAsync();
        using LayerContentReader reader = new(blob, index.Encoding);
        foreach (ScannedEntry entry in entries.Reverse())
        {
            using MemoryStream output = new();
            await reader.CopyToAsync(entry, output, TestContext.Current.CancellationToken);
            Assert.Equal(entry.Path switch
            {
                "first" => "first"u8.ToArray(),
                "last" => selected,
                _ => []
            }, output.ToArray());
        }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task DaemonInvalidSelectedHashFallsBackToValidatedCompleteBlob(bool empty, bool gzip)
    {
        DockerArchiveFixture fixture = WithRawLayer(DockerArchiveFixture.BuildTar(("selected", empty ? [] : [1, 2, 3])));
        if (gzip)
        {
            Dictionary<string, byte[]> entries = ReadEntries(fixture.Export);
            using MemoryStream compressed = new();
            using (GZipStream zip = new(compressed, CompressionLevel.Fastest, leaveOpen: true))
            {
                zip.Write(fixture.StoredLayer);
            }
            byte[] stored = compressed.ToArray();
            entries["layer.tar"] = stored;
            fixture = fixture with { Export = BuildEntries(entries), StoredLayer = stored };
        }
        await using DockerImageArchive archive = await OpenAsync(fixture);
        using StringWriter diagnostics = new();
        await using LayerStore store = new(Path.Combine(directory, "cache"), diagnostics: diagnostics);
        string digest = archive.Resolved.Manifest.Layers[0].Digest!;
        StoredLayerIndex index = await store.GetIndexAsync(
            archive, new(0, digest), fixture.StoredLayer.Length, TestContext.Current.CancellationToken);
        ScannedEntry original = Assert.Single(index.Changes.Entries);
        StoredLayerIndex invalid = new(index.Digest, index.BlobLength,
            new([original with { ContentHash = new string('a', 64) }], [], []), index.Encoding);

        using Stream blob = await store.OpenIndexedBlobAsync(
            archive, invalid, [original.EntryIndex], TestContext.Current.CancellationToken);

        Assert.Contains("Invalid cached-index range", diagnostics.ToString());
        Assert.True(File.Exists(store.GetBlobPath(digest)));
        Assert.Equal(fixture.StoredLayer.Length, blob.Length);
        using LayerContentReader reader = new(blob, index.Encoding);
        using MemoryStream output = new();
        await reader.CopyToAsync(original, output, TestContext.Current.CancellationToken);
        Assert.Equal(empty ? [] : new byte[] { 1, 2, 3 }, output.ToArray());
    }

    [Fact]
    public async Task RawDaemonIndexedReadsRejectInvalidLengthsAndCancellationWithoutScratch()
    {
        DockerArchiveFixture fixture = DockerArchiveFixture.Create();
        await using DockerImageArchive archive = await OpenAsync(fixture);
        string root = Path.Combine(directory, "cache");
        await using LayerStore store = new(root);
        string digest = archive.Resolved.Manifest.Layers[0].Digest!;
        StoredLayerIndex index = await store.GetIndexAsync(
            archive, new(0, digest), fixture.StoredLayer.Length, TestContext.Current.CancellationToken);
        int ordinal = Assert.Single(index.Changes.Entries).EntryIndex;
        StoredLayerIndex invalid = new(index.Digest, index.BlobLength + 1, index.Changes, index.Encoding);
        await Assert.ThrowsAsync<InvalidDataException>(() => store.OpenIndexedBlobAsync(
            archive, invalid, [ordinal], TestContext.Current.CancellationToken));
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.OpenIndexedBlobAsync(
            archive, index, [ordinal], cancellation.Token));
        Assert.Empty(Directory.GetFiles(Path.Combine(root, "layer-store", "data"), "*.tmp"));
        Assert.False(File.Exists(store.GetBlobPath(digest)));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task DaemonIndexRetainsPerFileHashesWithAndWithoutArchiveProof(bool sha512, bool verifyContent)
    {
        byte[] payload = "selected"u8.ToArray();
        byte[] raw = [.. DockerArchiveFixture.BuildTar(("selected", payload)), .. new byte[131_073]];
        DockerArchiveFixture fixture = WithRawLayer(raw, sha512);
        await using DockerImageArchive archive = await DockerImageArchive.OpenAsync(
            new MemoryStream(fixture.Export), fixture.Id, TestContext.Current.CancellationToken,
            directory, verifyLayerContent: verifyContent);
        await using LayerStore store = new(Path.Combine(directory, "cache"));
        string digest = archive.Resolved.Manifest.Layers[0].Digest!;
        string diffId = archive.Configuration.RootFilesystem!.DiffIds[0];
        Assert.Equal(verifyContent, archive.TryGetVerifiedContentDigest(digest, raw.Length, diffId, out _));

        StoredLayerIndex index = await store.GetIndexAsync(
            archive, new(0, digest), raw.Length, TestContext.Current.CancellationToken, expectedDiffId: diffId);

        Assert.Equal(DockerImageArchive.Hash(payload)[7..], Assert.Single(index.Changes.Entries).ContentHash);
        Assert.Equal(raw.Length, index.BlobLength);
        Assert.False(File.Exists(store.GetBlobPath(digest)));
    }

    [Theory]
    [InlineData(false, "identity")]
    [InlineData(true, "identity")]
    [InlineData(false, "size")]
    [InlineData(true, "size")]
    [InlineData(false, "repeated-size")]
    [InlineData(true, "repeated-size")]
    [InlineData(false, "digest")]
    [InlineData(true, "digest")]
    public async Task ArchiveContentProofDoesNotBypassInvalidRequestedDescriptors(bool verifyContent, string invalid)
    {
        DockerArchiveFixture fixture = DockerArchiveFixture.Create(oci: true, gzip: true);
        await using DockerImageArchive archive = await DockerImageArchive.OpenAsync(
            new MemoryStream(fixture.Export), fixture.Id, TestContext.Current.CancellationToken,
            directory, verifyLayerContent: verifyContent);
        var original = archive.Resolved.Manifest.Layers[0];
        OciDescriptor descriptor = new()
        {
            Digest = invalid == "digest" ? "sha256:" + new string('a', 64) : original.Digest,
            Size = original.Size + (invalid == "size" ? 1 : 0),
            MediaType = original.MediaType
        };
        OciImageManifest manifest = new()
        {
            Layers = invalid == "repeated-size" ?
                [descriptor, new OciDescriptor
                {
                    Digest = original.Digest, Size = original.Size + 1, MediaType = original.MediaType
                }] : [descriptor]
        };
        string diffId = invalid == "identity" ?
            "sha512:" + new string('a', 128) : archive.Configuration.RootFilesystem!.DiffIds[0];
        await using LayerStore store = new(Path.Combine(directory, "cache"));

        await Assert.ThrowsAsync<InvalidDataException>(() => ExplorerContentIdentity.VerifyAsync(
            archive, archive.Configuration, new(archive.Resolved.ManifestInfo, manifest), store,
            TestContext.Current.CancellationToken,
            expectedDiffIds: Enumerable.Repeat(diffId, manifest.Layers.Length).ToArray()));
    }

    [Fact]
    public async Task VerifiedArchiveContentProofStillHonorsCancellation()
    {
        await using DockerImageArchive archive = await OpenAsync(DockerArchiveFixture.Create(oci: true));
        await using LayerStore store = new(Path.Combine(directory, "cache"));
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ExplorerContentIdentity.VerifyAsync(
            archive, archive.Configuration, archive.Resolved, store, cancellation.Token));

        Assert.False(File.Exists(store.GetBlobPath(archive.Resolved.Manifest.Layers[0].Digest!)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DecodedContentHashesUseOneReadForBothAlgorithms(bool sha512)
    {
        byte[] raw = [.. DockerArchiveFixture.Create().StoredLayer,
            .. Enumerable.Repeat((byte)0x5a, 131072)];
        using CountingArchiveInput input = new(raw);

        (string diffId, string? sha512DiffId) = await DockerImageArchive.HashContentAsync(
            input, sha512, TestContext.Current.CancellationToken);

        Assert.Equal(DockerImageArchive.Hash(raw), diffId);
        Assert.Equal(sha512 ? "sha512:" + Convert.ToHexStringLower(SHA512.HashData(raw)) : null, sha512DiffId);
        Assert.Equal(raw.LongLength, input.BytesRead);
        Assert.Equal(0, input.SeekCount);
        Assert.True(input.CanRead);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DecodedContentHashesHonorCancellationWithoutClosingInput(bool sha512)
    {
        using CountingArchiveInput input = new(DockerArchiveFixture.Create().StoredLayer);
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            DockerImageArchive.HashContentAsync(input, sha512, cancellation.Token));

        Assert.Equal(0, input.BytesRead);
        Assert.True(input.CanRead);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DecodedContentHashingUsesBoundedSequentialReads(bool sha512)
    {
        byte[] raw = new byte[8 * 1024 * 1024 + 17];
        new Random(405).NextBytes(raw);
        using CountingArchiveInput input = new(raw, int.MaxValue);

        (string diffId, string? sha512DiffId) = await DockerImageArchive.HashContentAsync(
            input, sha512, TestContext.Current.CancellationToken);

        Assert.Equal(DockerImageArchive.Hash(raw), diffId);
        Assert.Equal(sha512 ? "sha512:" + Convert.ToHexStringLower(SHA512.HashData(raw)) : null, sha512DiffId);
        Assert.Equal(raw.LongLength, input.BytesRead);
        Assert.InRange(input.ReadCount, 2, 105);
        Assert.InRange(input.MaximumRequestedRead, 81920, 131072);
        Assert.Equal(0, input.SeekCount);
        Assert.True(input.CanRead);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DecodedContentHashingCancelsBetweenReadsWithoutClosingInput(bool sha512)
    {
        using CancellationTokenSource cancellation = new();
        using CountingArchiveInput input = new(new byte[262144], 81920, cancellation);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            DockerImageArchive.HashContentAsync(input, sha512, cancellation.Token));

        Assert.Equal(1, input.ReadCount);
        Assert.Equal(81920, input.BytesRead);
        Assert.True(input.CanRead);
    }

    [Fact]
    public async Task RequiredMemberGuardsPreserveProofLookupAndDisposalContracts()
    {
        DockerArchiveFixture fixture = DockerArchiveFixture.Create(oci: true);
        await using DockerImageArchive archive = await OpenAsync(fixture);
        string digest = archive.Resolved.Manifest.Layers[0].Digest!;
        string diffId = archive.Configuration.RootFilesystem!.DiffIds[0];
        string missing = "sha256:" + new string('f', 64);
        long wrongSize = fixture.StoredLayer.Length + 1;
        foreach (Func<string, long?, StoredLayerIndex?> getIndex in
            new Func<string, long?, StoredLayerIndex?>[] { archive.GetPreparedIndex, archive.GetRetainedIndex })
        {
            Assert.Contains("not part of this Docker snapshot",
                Assert.Throws<InvalidDataException>(() => getIndex(missing, wrongSize)).Message);
            Assert.Contains("advertised length",
                Assert.Throws<InvalidDataException>(() => getIndex(digest, wrongSize)).Message);
        }
        Assert.False(archive.TryGetVerifiedContentDigest(missing, wrongSize, diffId, out _));
        Assert.False(archive.TryGetVerifiedContentDigest(digest, wrongSize, missing, out _));
        Assert.Contains("not part of this Docker snapshot",
            (await Assert.ThrowsAsync<InvalidDataException>(() =>
                archive.VerifyContentAsync(missing, wrongSize, diffId, TestContext.Current.CancellationToken))).Message);
        Assert.Contains("advertised length",
            (await Assert.ThrowsAsync<InvalidDataException>(() =>
                archive.VerifyContentAsync(digest, wrongSize, missing, TestContext.Current.CancellationToken))).Message);
        Assert.Equal(diffId, await archive.VerifyContentAsync(digest, fixture.StoredLayer.Length,
            diffId, TestContext.Current.CancellationToken));
        await archive.DisposeAsync();
        Assert.Throws<ObjectDisposedException>(() => archive.GetPreparedIndex(digest, null));
        Assert.Throws<ObjectDisposedException>(() => archive.GetRetainedIndex(digest, null));
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            archive.VerifyContentAsync(digest, null, diffId, cancellation.Token));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task OciPinnedGraphRetainsOmittedConfigurationPlatformFields(bool pinnedIndex, bool version)
    {
        DockerArchiveFixture fixture = DockerArchiveFixture.Create(oci: true, architecture: "arm64");
        Dictionary<string, byte[]> entries = ReadEntries(fixture.Export);
        JsonObject index = JsonNode.Parse(entries["index.json"])!.AsObject();
        index["manifests"]![0]!["platform"] = new JsonObject
        {
            ["os"] = "linux",
            ["architecture"] = "arm64",
            ["variant"] = "v8",
            ["os.version"] = version ? "1" : null
        };
        entries["index.json"] = JsonSerializer.SerializeToUtf8Bytes(index);
        string id = pinnedIndex ? DockerImageArchive.Hash(entries["index.json"]) :
            index["manifests"]![0]!["digest"]!.GetValue<string>();
        if (pinnedIndex)
        {
            entries["blobs/sha256/" + id[7..]] = entries["index.json"];
        }

        await using DockerImageArchive archive = await DockerImageArchive.OpenAsync(
            new MemoryStream(BuildEntries(entries)), id, TestContext.Current.CancellationToken, directory,
            "linux", "arm64", version ? "1" : null, "v8");

        Assert.Equal(fixture.Config, archive.ConfigurationJson);
        Assert.Null(archive.Configuration.Variant);
        Assert.Equal("v8", archive.Platform.Variant);
        Assert.Equal(version ? "1" : null, archive.Platform.OsVersion);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OciPinnedGraphRejectsExplicitConflictingConfigurationPlatform(bool version)
    {
        DockerArchiveFixture fixture = DockerArchiveFixture.Create(oci: true, architecture: "arm");
        JsonObject config = JsonNode.Parse(fixture.Config)!.AsObject();
        config[version ? "os.version" : "variant"] = version ? "2" : "v6";
        fixture = WithConfiguration(fixture, JsonSerializer.SerializeToUtf8Bytes(config), true);
        Dictionary<string, byte[]> entries = ReadEntries(fixture.Export);
        JsonObject index = JsonNode.Parse(entries["index.json"])!.AsObject();
        index["manifests"]![0]!["platform"] = new JsonObject
        {
            ["os"] = "linux",
            ["architecture"] = "arm",
            [version ? "os.version" : "variant"] = version ? "1" : "v7"
        };
        entries["index.json"] = JsonSerializer.SerializeToUtf8Bytes(index);
        string id = DockerImageArchive.Hash(entries["index.json"]);
        entries["blobs/sha256/" + id[7..]] = entries["index.json"];

        await Assert.ThrowsAsync<InvalidDataException>(() => DockerImageArchive.OpenAsync(
            new MemoryStream(BuildEntries(entries)), id, TestContext.Current.CancellationToken, directory,
            "linux", "arm", version ? "1" : null, version ? null : "v7"));

        Assert.Empty(Directory.EnumerateFileSystemEntries(directory));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Sha512DiffIdsRejectIncorrectDecodedContent(bool gzip)
    {
        DockerArchiveFixture fixture = DockerArchiveFixture.Create(oci: true, gzip: gzip);
        JsonObject config = JsonNode.Parse(fixture.Config)!.AsObject();
        config["rootfs"]!["diff_ids"]![0] = "sha512:" + new string('a', 128);
        fixture = WithConfiguration(fixture, JsonSerializer.SerializeToUtf8Bytes(config), true);

        InvalidDataException error = await Assert.ThrowsAsync<InvalidDataException>(() => OpenAsync(fixture));

        Assert.Contains("does not match its rootfs diff ID", error.Message);
        Assert.Empty(Directory.EnumerateFileSystemEntries(directory));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReusedLayerSupportsBothDiffIdAlgorithms(bool gzip)
    {
        DockerArchiveFixture fixture = DockerArchiveFixture.Create(oci: true, gzip: gzip);
        Dictionary<string, byte[]> entries = ReadEntries(fixture.Export);
        JsonObject index = JsonNode.Parse(entries["index.json"])!.AsObject();
        JsonObject manifest = JsonNode.Parse(
            entries["blobs/sha256/" + index["manifests"]![0]!["digest"]!.GetValue<string>()[7..]])!.AsObject();
        manifest["layers"]!.AsArray().Add(manifest["layers"]![0]!.DeepClone());
        ReplaceManifest(entries, index, manifest);
        using Stream decoded = ImageTarReader.OpenContent(new MemoryStream(fixture.StoredLayer));
        string sha512 = "sha512:" + Convert.ToHexStringLower(
            await SHA512.HashDataAsync(decoded, TestContext.Current.CancellationToken));
        JsonObject config = JsonNode.Parse(fixture.Config)!.AsObject();
        config["rootfs"]!["diff_ids"]!.AsArray().Add(JsonValue.Create(sha512));
        config["history"]!.AsArray().Add(config["history"]![0]!.DeepClone());
        fixture = WithConfiguration(fixture with { Export = BuildEntries(entries) },
            JsonSerializer.SerializeToUtf8Bytes(config), true);

        await using DockerImageArchive archive = await OpenAsync(fixture);

        Assert.Equal(2, archive.Resolved.Manifest.Layers.Length);
        Assert.Equal(archive.Resolved.Manifest.Layers[0].Digest, archive.Resolved.Manifest.Layers[1].Digest);
        Assert.Equal(sha512, archive.Configuration.RootFilesystem!.DiffIds[1]);
    }

    [Fact]
    public async Task OciFactoryPreservesEffectivePlatformWithoutRewritingConfiguration()
    {
        DockerArchiveFixture fixture = DockerArchiveFixture.Create(oci: true, architecture: "arm64");
        Dictionary<string, byte[]> entries = ReadEntries(fixture.Export);
        JsonObject index = JsonNode.Parse(entries["index.json"])!.AsObject();
        index["manifests"]![0]!["platform"] = new JsonObject
        {
            ["os"] = "linux",
            ["architecture"] = "arm64",
            ["variant"] = "v8",
            ["os.version"] = "1"
        };
        entries["index.json"] = JsonSerializer.SerializeToUtf8Bytes(index);
        string id = DockerImageArchive.Hash(entries["index.json"]);
        entries["blobs/sha256/" + id[7..]] = entries["index.json"];
        fixture = fixture with { Id = id, Export = BuildEntries(entries) };
        ImageSourceFactoryTests.FakeEngine engine = new(fixture)
        {
            Architecture = "arm64",
            Variant = "v8",
            OsVersion = "1"
        };
        IDockerRegistryClientFactory registries = Mock.Of<IDockerRegistryClientFactory>();
        ImageSourceFactory factory = new(registries, new TestAppSettingsStore(new(), ""), _ => engine, directory);
        await using LayerCacheTestContext cache = new();

        await using ExplorerSource source = await ExplorerSource.OpenAsync(
            factory, registries, "docker://" + id,
            new() { DockerHost = "http://localhost:2375", UsePlatformDefaults = false },
            null, TestContext.Current.CancellationToken, store: cache.Store);

        Assert.Equal(fixture.Config, source.Source!.ConfigurationJson);
        Assert.Null(source.Config.Variant);
        Assert.Null(source.Config.OsVersion);
        Assert.Equal(new("linux", "arm64", "v8", "1"), source.Platform);
        Assert.Equal("linux/arm64/v8 (1)", source.Source.Platform);
        Assert.Equal([id], engine.Inspections);
    }

    [Fact]
    public async Task DockerRawTarDescriptor_PreservesVerifiedRawContent()
    {
        DockerArchiveFixture fixture = DockerArchiveFixture.Create(
            oci: true, layerMediaType: "application/vnd.docker.image.rootfs.diff.tar");
        await using DockerImageArchive archive = await OpenAsync(fixture);
        Assert.Equal(fixture.Config, archive.ConfigurationJson);
        Assert.Equal(DockerImageArchive.RawLayerMediaType, archive.Resolved.Manifest.Layers[0].MediaType);
        Assert.Equal(DockerImageArchive.Hash(fixture.StoredLayer), archive.Resolved.Manifest.Layers[0].Digest);
        Assert.Equal(fixture.StoredLayer.Length, archive.Resolved.Manifest.Layers[0].Size);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task DockerRawTarDescriptor_RejectsGzipOrCorruptContent(bool gzip, bool corrupt)
    {
        DockerArchiveFixture fixture = DockerArchiveFixture.Create(
            oci: true, gzip: gzip, corruptLayer: corrupt,
            layerMediaType: "application/vnd.docker.image.rootfs.diff.tar");
        await Assert.ThrowsAsync<InvalidDataException>(() => OpenAsync(fixture));
        Assert.Empty(Directory.EnumerateFileSystemEntries(directory));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ScratchImageAndWindowsMetadata_AreSupported(bool oci)
    {
        DockerArchiveFixture fixture = DockerArchiveFixture.Create(oci: oci, scratch: true, windows: true);
        await using DockerImageArchive archive = await OpenAsync(fixture);
        Assert.Empty(archive.Resolved.Manifest.Layers);
        Assert.Equal("windows", archive.Configuration.Os);
        Assert.Equal("10.0.20348.1", archive.Configuration.OsVersion);
        Assert.Equal(fixture.Config, archive.ConfigurationJson);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task AbsentDiffIds_AreAcceptedOnlyForZeroLayers(bool oci, bool scratch)
    {
        DockerArchiveFixture fixture = DockerArchiveFixture.Create(oci: oci, scratch: scratch);
        JsonObject config = JsonNode.Parse(fixture.Config)!.AsObject();
        config["rootfs"]!.AsObject().Remove("diff_ids");
        fixture = WithConfiguration(fixture, JsonSerializer.SerializeToUtf8Bytes(config), oci);
        if (!scratch)
        {
            await Assert.ThrowsAsync<InvalidDataException>(() => OpenAsync(fixture));
            Assert.Empty(Directory.EnumerateFileSystemEntries(directory));
            return;
        }
        await using DockerImageArchive archive = await OpenAsync(fixture);
        Assert.Empty(archive.Resolved.Manifest.Layers);
        Assert.Equal(fixture.Config, archive.ConfigurationJson);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("\"invalid\"")]
    [InlineData("{}")]
    [InlineData("[null]")]
    [InlineData("[123]")]
    [InlineData("[\"sha256:invalid\"]")]
    public async Task ZeroLayers_StillRejectsInvalidDiffIds(string diffIds)
    {
        DockerArchiveFixture fixture = DockerArchiveFixture.Create(scratch: true);
        JsonObject config = JsonNode.Parse(fixture.Config)!.AsObject();
        config["rootfs"]!["diff_ids"] = JsonNode.Parse(diffIds);
        fixture = WithConfiguration(fixture, JsonSerializer.SerializeToUtf8Bytes(config), oci: false);
        await Assert.ThrowsAsync<InvalidDataException>(() => OpenAsync(fixture));
        Assert.Empty(Directory.EnumerateFileSystemEntries(directory));
    }

    [Fact]
    public async Task SnapshotPermissions_ArePrivateEvenUnderPublicParent()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }
        CacheFileSystem.CreateDirectory(directory);
        string publicParent = Path.Combine(directory, "public");
        Directory.CreateDirectory(publicParent);
        File.SetUnixFileMode(publicParent, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
            UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute |
            UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute);
        string root = Path.Combine(publicParent, "snapshots");
        DockerArchiveFixture fixture = DockerArchiveFixture.Create();
        await using DockerImageArchive archive = await DockerImageArchive.OpenAsync(
            new MemoryStream(fixture.Export), fixture.Id, TestContext.Current.CancellationToken, root);
        UnixFileMode privateDirectory = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
        Assert.Equal(privateDirectory, File.GetUnixFileMode(root));
        string snapshot = Assert.Single(Directory.EnumerateDirectories(root));
        Assert.Equal(privateDirectory, File.GetUnixFileMode(snapshot));
        string spool = Assert.Single(Directory.EnumerateFiles(snapshot));
        Assert.Equal(UnixFileMode.UserRead, File.GetUnixFileMode(spool));
    }

    [Fact]
    public async Task HybridContainerdExport_UsesOriginalOciConfigNotConvertedDockerCompatibilityConfig()
    {
        DockerArchiveFixture fixture = DockerArchiveFixture.Create(oci: true);
        List<(string, byte[])> entries = [];
        using (TarReader reader = new(new MemoryStream(fixture.Export)))
        {
            TarEntry? entry;
            while ((entry = reader.GetNextEntry()) is not null)
            {
                using MemoryStream data = new();
                entry.DataStream!.CopyTo(data);
                entries.Add((entry.Name, data.ToArray()));
            }
        }
        entries.Add(("compat-config.json", "{}"u8.ToArray()));
        entries.Add(("manifest.json", """[{"Config":"compat-config.json","Layers":[]}]"""u8.ToArray()));
        await using DockerImageArchive archive = await DockerImageArchive.OpenAsync(
            new MemoryStream(DockerArchiveFixture.BuildTar([.. entries])),
            fixture.Id, TestContext.Current.CancellationToken, directory);
        Assert.Equal(fixture.Config, archive.ConfigurationJson);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ModernDaemonImageId_CanAddressPinnedIndexOrManifest(bool indexIdentity)
    {
        DockerArchiveFixture fixture = DockerArchiveFixture.Create(oci: true);
        List<(string, byte[])> entries = [];
        byte[]? index = null;
        using (TarReader reader = new(new MemoryStream(fixture.Export)))
        {
            TarEntry? entry;
            while ((entry = reader.GetNextEntry()) is not null)
            {
                using MemoryStream data = new();
                entry.DataStream!.CopyTo(data);
                byte[] bytes = data.ToArray();
                entries.Add((entry.Name, bytes));
                if (entry.Name == "index.json")
                {
                    index = bytes;
                }
            }
        }
        Assert.NotNull(index);
        string pinnedId;
        if (indexIdentity)
        {
            pinnedId = DockerImageArchive.Hash(index);
            entries.Add(("blobs/sha256/" + pinnedId[7..], index));
        }
        else
        {
            using JsonDocument document = JsonDocument.Parse(index);
            pinnedId = document.RootElement.GetProperty("manifests")[0].GetProperty("digest").GetString()!;
        }
        await using DockerImageArchive archive = await DockerImageArchive.OpenAsync(
            new MemoryStream(DockerArchiveFixture.BuildTar([.. entries])),
            pinnedId, TestContext.Current.CancellationToken, directory, "linux", "amd64");
        Assert.Equal(fixture.Config, archive.ConfigurationJson);
        Assert.Equal(fixture.Id, archive.Resolved.Manifest.Config!.Digest);
        Assert.NotEqual(pinnedId, archive.Resolved.Manifest.Config.Digest);
    }

    [Fact]
    public async Task SnapshotDisposal_RemovesScratch()
    {
        DockerImageArchive archive = await OpenAsync(DockerArchiveFixture.Create());
        Assert.NotEmpty(Directory.EnumerateDirectories(directory));
        await archive.DisposeAsync();
        Assert.Empty(Directory.EnumerateFileSystemEntries(directory));
        await archive.DisposeAsync();
    }

    [Theory]
    [InlineData("../escape")]
    [InlineData("/absolute")]
    [InlineData("C:/absolute")]
    [InlineData("folder\\escape")]
    [InlineData("a/../escape")]
    public async Task UnsafeOuterArchivePaths_AreRejectedAndCleaned(string name)
    {
        byte[] export = DockerArchiveFixture.BuildTar((name, [1, 2, 3]));
        await Assert.ThrowsAsync<InvalidDataException>(() => DockerImageArchive.OpenAsync(
            new MemoryStream(export), "sha256:" + new string('a', 64), TestContext.Current.CancellationToken, directory));
        Assert.Empty(Directory.EnumerateFileSystemEntries(directory));
    }

    [Fact]
    public async Task OuterSymlink_IsRejected()
    {
        using MemoryStream stream = new();
        using (TarWriter writer = new(stream, leaveOpen: true))
        {
            writer.WriteEntry(new PaxTarEntry(TarEntryType.SymbolicLink, "config.json") { LinkName = "outside" });
        }
        await Assert.ThrowsAsync<InvalidDataException>(() => DockerImageArchive.OpenAsync(
            new MemoryStream(stream.ToArray()), "sha256:" + new string('a', 64), TestContext.Current.CancellationToken, directory));
        Assert.Empty(Directory.EnumerateFileSystemEntries(directory));
    }

    [Fact]
    public async Task DuplicateEntries_AreRejected()
    {
        byte[] export = DockerArchiveFixture.BuildTar(("manifest.json", "[]"u8.ToArray()), ("./manifest.json", "[]"u8.ToArray()));
        await Assert.ThrowsAsync<InvalidDataException>(() => DockerImageArchive.OpenAsync(
            new MemoryStream(export), "sha256:" + new string('a', 64), TestContext.Current.CancellationToken, directory));
    }

    [Fact]
    public async Task WrongPinnedConfigId_IsRejected()
    {
        DockerArchiveFixture fixture = DockerArchiveFixture.Create();
        await Assert.ThrowsAsync<InvalidDataException>(() => DockerImageArchive.OpenAsync(
            new MemoryStream(fixture.Export), "sha256:" + new string('a', 64), TestContext.Current.CancellationToken, directory));
        Assert.Empty(Directory.EnumerateFileSystemEntries(directory));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CorruptLayer_IsRejected(bool oci)
    {
        DockerArchiveFixture fixture = DockerArchiveFixture.Create(oci: oci, corruptLayer: true);
        await Assert.ThrowsAsync<InvalidDataException>(() => OpenAsync(fixture));
        Assert.Empty(Directory.EnumerateFileSystemEntries(directory));
    }

    [Fact]
    public async Task LayerCountMismatch_IsRejected()
    {
        DockerArchiveFixture fixture = DockerArchiveFixture.Create(scratch: true);
        byte[] manifest = Encoding.UTF8.GetBytes("""[{"Config":"config.json","Layers":["layer.tar"]}]""");
        byte[] export = DockerArchiveFixture.BuildTar(("manifest.json", manifest), ("config.json", fixture.Config), ("layer.tar", fixture.StoredLayer));
        await Assert.ThrowsAsync<InvalidDataException>(() => DockerImageArchive.OpenAsync(
            new MemoryStream(export), fixture.Id, TestContext.Current.CancellationToken, directory));
    }

    [Fact]
    public async Task ClassicScratchImage_AcceptsNullLayerArray()
    {
        DockerArchiveFixture fixture = DockerArchiveFixture.Create(scratch: true);
        byte[] manifest = Encoding.UTF8.GetBytes("""[{"Config":"config.json","Layers":null}]""");
        byte[] export = DockerArchiveFixture.BuildTar(("manifest.json", manifest), ("config.json", fixture.Config));
        await using DockerImageArchive archive = await DockerImageArchive.OpenAsync(
            new MemoryStream(export), fixture.Id, TestContext.Current.CancellationToken, directory);
        Assert.Empty(archive.Resolved.Manifest.Layers);
    }

    [Fact]
    public async Task RepeatedLayersAndEmptyHistory_PreserveLayerOrdering()
    {
        DockerArchiveFixture fixture = DockerArchiveFixture.Create();
        string diffId = DockerImageArchive.Hash(fixture.StoredLayer);
        byte[] config = JsonSerializer.SerializeToUtf8Bytes(new
        {
            os = "linux",
            architecture = "amd64",
            rootfs = new { type = "layers", diff_ids = new[] { diffId, diffId } },
            history = new[]
            {
                new { created_by = "first", empty_layer = false },
                new { created_by = "metadata", empty_layer = true },
                new { created_by = "second", empty_layer = false }
            }
        });
        byte[] manifest = Encoding.UTF8.GetBytes("""[{"Config":"config.json","Layers":["layer.tar","layer.tar"]}]""");
        byte[] export = DockerArchiveFixture.BuildTar(("manifest.json", manifest), ("config.json", config), ("layer.tar", fixture.StoredLayer));
        await using DockerImageArchive archive = await DockerImageArchive.OpenAsync(
            new MemoryStream(export), DockerImageArchive.Hash(config), TestContext.Current.CancellationToken, directory);
        Assert.Equal(2, archive.Resolved.Manifest.Layers.Length);
        Assert.Equal(archive.Resolved.Manifest.Layers[0].Digest, archive.Resolved.Manifest.Layers[1].Digest);
        Assert.Equal(3, archive.Configuration.History!.Length);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task OptionalHistory_CanBeAbsentOrExplicitlyEmpty(bool oci, bool omit)
    {
        DockerArchiveFixture fixture = DockerArchiveFixture.Create(oci: oci);
        JsonObject config = JsonNode.Parse(fixture.Config)!.AsObject();
        if (omit)
        {
            config.Remove("history");
        }
        else
        {
            config["history"] = new JsonArray();
        }
        fixture = WithConfiguration(fixture, JsonSerializer.SerializeToUtf8Bytes(config), oci);
        await using DockerImageArchive archive = await OpenAsync(fixture);
        Assert.Single(archive.Resolved.Manifest.Layers);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConfigurationBom_IsDecodedWithoutChangingIdentityOrBytes(bool oci)
    {
        DockerArchiveFixture fixture = DockerArchiveFixture.Create(oci: oci);
        fixture = WithConfiguration(fixture, [0xef, 0xbb, 0xbf, .. fixture.Config], oci);
        await using DockerImageArchive archive = await OpenAsync(fixture);
        Assert.Equal(fixture.Config, archive.ConfigurationJson);
        Assert.Equal(fixture.Id, archive.Resolved.Manifest.Config!.Digest);
        Assert.Equal("linux", archive.Configuration.Os);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PopulatedHistory_MustMatchFilesystemLayerCount(bool oci)
    {
        DockerArchiveFixture fixture = DockerArchiveFixture.Create(oci: oci);
        JsonObject config = JsonNode.Parse(fixture.Config)!.AsObject();
        config["history"]![0]!["empty_layer"] = true;
        fixture = WithConfiguration(fixture, JsonSerializer.SerializeToUtf8Bytes(config), oci);
        await Assert.ThrowsAsync<InvalidDataException>(() => OpenAsync(fixture));
        Assert.Empty(Directory.EnumerateFileSystemEntries(directory));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SharedOciManifestDag_IsDeduplicated(bool pinnedIndex)
    {
        DockerArchiveFixture fixture = DockerArchiveFixture.Create(oci: true);
        Dictionary<string, byte[]> entries = ReadEntries(fixture.Export);
        JsonObject manifestDescriptor = JsonNode.Parse(entries["index.json"])!["manifests"]![0]!.AsObject();
        JsonObject first = new()
        {
            ["schemaVersion"] = 2,
            ["manifests"] = new JsonArray(manifestDescriptor.DeepClone()),
            ["annotations"] = new JsonObject { ["branch"] = "first" }
        };
        JsonObject second = first.DeepClone().AsObject();
        second["annotations"]!["branch"] = "second";
        JsonObject firstDescriptor = AddIndex(entries, JsonSerializer.SerializeToUtf8Bytes(first));
        JsonObject secondDescriptor = AddIndex(entries, JsonSerializer.SerializeToUtf8Bytes(second));
        entries["index.json"] = JsonSerializer.SerializeToUtf8Bytes(new JsonObject
        {
            ["schemaVersion"] = 2,
            ["manifests"] = new JsonArray(firstDescriptor, secondDescriptor)
        });
        string id = fixture.Id;
        if (pinnedIndex)
        {
            id = DockerImageArchive.Hash(entries["index.json"]);
            entries["blobs/sha256/" + id[7..]] = entries["index.json"];
        }
        await using DockerImageArchive archive = await DockerImageArchive.OpenAsync(
            new MemoryStream(BuildEntries(entries)), id, TestContext.Current.CancellationToken, directory, "linux", "amd64");
        Assert.Equal(fixture.Config, archive.ConfigurationJson);
        Assert.Single(archive.Resolved.Manifest.Layers);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task MissingOtherPlatformManifest_DoesNotBlockSelectedImage(bool pinnedIndex, bool nested)
    {
        DockerArchiveFixture fixture = DockerArchiveFixture.Create(oci: true);
        Dictionary<string, byte[]> entries = ReadEntries(fixture.Export);
        JsonObject index = JsonNode.Parse(entries["index.json"])!.AsObject();
        index["manifests"]![0]!["platform"] = new JsonObject { ["os"] = "linux", ["architecture"] = "amd64" };
        index["manifests"]!.AsArray().Add(new JsonObject
        {
            ["mediaType"] = nested ? "application/vnd.oci.image.index.v1+json" : "application/vnd.oci.image.manifest.v1+json",
            ["digest"] = "sha256:" + new string('a', 64),
            ["size"] = 123,
            ["platform"] = new JsonObject { ["os"] = "linux", ["architecture"] = "arm64" }
        });
        entries["index.json"] = JsonSerializer.SerializeToUtf8Bytes(index);
        string id = fixture.Id;
        if (pinnedIndex)
        {
            id = DockerImageArchive.Hash(entries["index.json"]);
            entries["blobs/sha256/" + id[7..]] = entries["index.json"];
        }
        await using DockerImageArchive archive = await DockerImageArchive.OpenAsync(
            new MemoryStream(BuildEntries(entries)), id, TestContext.Current.CancellationToken, directory,
            pinnedIndex ? "linux" : null, pinnedIndex ? "amd64" : null);
        Assert.Equal(fixture.Config, archive.ConfigurationJson);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task MissingOrCorruptRequiredManifest_IsRejected(bool unknownPlatform, bool corrupt)
    {
        DockerArchiveFixture fixture = DockerArchiveFixture.Create(oci: true);
        Dictionary<string, byte[]> entries = ReadEntries(fixture.Export);
        JsonObject index = JsonNode.Parse(entries["index.json"])!.AsObject();
        JsonNode descriptor = index["manifests"]![0]!;
        if (!unknownPlatform)
        {
            descriptor["platform"] = new JsonObject { ["os"] = "linux", ["architecture"] = "amd64" };
        }
        string path = "blobs/sha256/" + descriptor["digest"]!.GetValue<string>()[7..];
        if (corrupt)
        {
            entries[path][0] ^= 1;
        }
        else
        {
            entries.Remove(path);
        }
        entries["index.json"] = JsonSerializer.SerializeToUtf8Bytes(index);
        await Assert.ThrowsAsync<InvalidDataException>(() => DockerImageArchive.OpenAsync(
            new MemoryStream(BuildEntries(entries)), fixture.Id, TestContext.Current.CancellationToken, directory,
            verifyLayerContent: false));
        Assert.Empty(Directory.EnumerateFileSystemEntries(directory));
    }

    [Fact]
    public async Task ExcessivelyNestedOciIndex_IsRejected()
    {
        DockerArchiveFixture fixture = DockerArchiveFixture.Create(oci: true);
        Dictionary<string, byte[]> entries = ReadEntries(fixture.Export);
        for (int i = 0; i < 10; i++)
        {
            JsonObject descriptor = AddIndex(entries, entries["index.json"]);
            entries["index.json"] = JsonSerializer.SerializeToUtf8Bytes(new JsonObject
            {
                ["schemaVersion"] = 2,
                ["manifests"] = new JsonArray(descriptor)
            });
        }
        InvalidDataException error = await Assert.ThrowsAsync<InvalidDataException>(() => DockerImageArchive.OpenAsync(
            new MemoryStream(BuildEntries(entries)), fixture.Id, TestContext.Current.CancellationToken, directory));
        Assert.Contains("too deep", error.Message);
        Assert.Empty(Directory.EnumerateFileSystemEntries(directory));
    }

    [Fact]
    public async Task SharedCompletedIndex_DoesNotBypassDepthLimit()
    {
        DockerArchiveFixture fixture = DockerArchiveFixture.Create(oci: true);
        Dictionary<string, byte[]> entries = ReadEntries(fixture.Export);
        JsonObject shared = AddIndex(entries, entries["index.json"]);
        JsonObject descriptor = shared.DeepClone().AsObject();
        for (int i = 0; i < 8; i++)
        {
            descriptor = AddIndex(entries, JsonSerializer.SerializeToUtf8Bytes(new JsonObject
            {
                ["schemaVersion"] = 2,
                ["manifests"] = new JsonArray(descriptor)
            }));
        }
        entries["index.json"] = JsonSerializer.SerializeToUtf8Bytes(new JsonObject
        {
            ["schemaVersion"] = 2,
            ["manifests"] = new JsonArray(shared, descriptor)
        });
        InvalidDataException error = await Assert.ThrowsAsync<InvalidDataException>(() => DockerImageArchive.OpenAsync(
            new MemoryStream(BuildEntries(entries)), fixture.Id, TestContext.Current.CancellationToken, directory));
        Assert.Contains("too deep", error.Message);
        Assert.Empty(Directory.EnumerateFileSystemEntries(directory));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ZstdLayer_MetadataOnlyPreservesVerifiedStoredBytes(bool verifyContent)
    {
        DockerArchiveFixture fixture = DockerArchiveFixture.Create(oci: true);
        byte[] zstd = [0x28, 0xb5, 0x2f, 0xfd, 0x20, 0x05, 0x29, 0x00, 0x00, .. "hello"u8.ToArray()];
        JsonObject config = JsonNode.Parse(fixture.Config)!.AsObject();
        config["rootfs"]!["diff_ids"]![0] = DockerImageArchive.Hash("hello"u8);
        fixture = WithConfiguration(fixture, JsonSerializer.SerializeToUtf8Bytes(config), oci: true);
        Dictionary<string, byte[]> entries = ReadEntries(fixture.Export);
        JsonObject index = JsonNode.Parse(entries["index.json"])!.AsObject();
        string manifestPath = "blobs/sha256/" + index["manifests"]![0]!["digest"]!.GetValue<string>()[7..];
        JsonObject manifest = JsonNode.Parse(entries[manifestPath])!.AsObject();
        JsonNode layer = manifest["layers"]![0]!;
        layer["mediaType"] = "application/vnd.oci.image.layer.v1.tar+zstd";
        layer["digest"] = DockerImageArchive.Hash(zstd);
        layer["size"] = zstd.Length;
        entries["blobs/sha256/" + DockerImageArchive.Hash(zstd)[7..]] = zstd;
        ReplaceManifest(entries, index, manifest);
        if (verifyContent)
        {
            InvalidDataException error = await Assert.ThrowsAsync<InvalidDataException>(() => DockerImageArchive.OpenAsync(
                new MemoryStream(BuildEntries(entries)), fixture.Id, TestContext.Current.CancellationToken, directory));
            Assert.Contains("Unsupported", error.Message);
            Assert.Empty(Directory.EnumerateFileSystemEntries(directory));
            return;
        }
        await using DockerImageArchive archive = await DockerImageArchive.OpenAsync(
            new MemoryStream(BuildEntries(entries)), fixture.Id, TestContext.Current.CancellationToken, directory,
            verifyLayerContent: false);
        Assert.Equal("application/vnd.oci.image.layer.v1.tar+zstd", archive.Resolved.Manifest.Layers[0].MediaType);
        BlobDownloadResult result = await archive.GetRangeAsync(DockerImageArchive.Hash(zstd), 0, null,
            TestContext.Current.CancellationToken);
        await using Stream content = result.Content;
        using MemoryStream output = new();
        await content.CopyToAsync(output, TestContext.Current.CancellationToken);
        Assert.Equal(zstd, output.ToArray());
    }

    [Fact]
    public async Task MetadataOnly_StillRejectsCorruptStoredLayerDescriptors()
    {
        DockerArchiveFixture fixture = DockerArchiveFixture.Create(oci: true, corruptLayer: true);
        await Assert.ThrowsAsync<InvalidDataException>(() => DockerImageArchive.OpenAsync(
            new MemoryStream(fixture.Export), fixture.Id, TestContext.Current.CancellationToken, directory,
            verifyLayerContent: false));
        Assert.Empty(Directory.EnumerateFileSystemEntries(directory));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Acquisition_RetainsOnlyOneExportSpool(bool gzip)
    {
        DockerArchiveFixture fixture = DockerArchiveFixture.Create(oci: true, gzip: gzip);
        await using DockerImageArchive archive = await OpenAsync(fixture);
        string file = Assert.Single(Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories));
        Assert.Equal("export.tar", Path.GetFileName(file));
        Assert.Equal(fixture.Export.LongLength, new FileInfo(file).Length);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(131072)]
    public async Task GzipHashes_ReadEncodedBytesOnceIncludingTrailingBytes_AndMatchSharedDecoder(int trailingLength)
    {
        DockerArchiveFixture fixture = DockerArchiveFixture.Create(oci: true, gzip: true);
        byte[] stored = [.. fixture.StoredLayer, .. Enumerable.Repeat((byte)0x5a, trailingLength)];
        using MemoryStream expectedInput = new(stored);
        Assert.Equal("gzip", ImageTarReader.DetectEncoding(expectedInput));
        Assert.Equal(0, expectedInput.Position);
        using Stream decoded = ImageTarReader.OpenContent(expectedInput);
        using MemoryStream expectedContent = new();
        await decoded.CopyToAsync(expectedContent, TestContext.Current.CancellationToken);
        using CountingArchiveInput input = new(stored);
        (string digest, string diffId) = await DockerImageArchive.HashGzipLayerAsync(
            input, TestContext.Current.CancellationToken);
        Assert.Equal(DockerImageArchive.Hash(stored), digest);
        Assert.Equal(DockerImageArchive.Hash(expectedContent.ToArray()), diffId);
        Assert.Equal(stored.LongLength, input.BytesRead);
        Assert.Equal(stored.LongLength, input.Position);
        Assert.True(input.CanRead);
        Assert.Equal(0, input.SeekCount);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task GzipTrailingBytes_AreIncludedInStoredDescriptorValidation(bool corruptTrailingByte, bool verifyContent)
    {
        DockerArchiveFixture fixture = DockerArchiveFixture.Create(oci: true, gzip: true);
        Dictionary<string, byte[]> entries = ReadEntries(fixture.Export);
        JsonObject index = JsonNode.Parse(entries["index.json"])!.AsObject();
        string path = "blobs/sha256/" + index["manifests"]![0]!["digest"]!.GetValue<string>()[7..];
        JsonObject manifest = JsonNode.Parse(entries[path])!.AsObject();
        byte[] stored = [.. fixture.StoredLayer, .. Enumerable.Repeat((byte)0x5a, 131072)];
        string digest = DockerImageArchive.Hash(stored);
        manifest["layers"]![0]!["digest"] = digest;
        manifest["layers"]![0]!["size"] = stored.Length;
        if (corruptTrailingByte)
        {
            stored[^1] ^= 1;
        }
        entries["blobs/sha256/" + digest[7..]] = stored;
        ReplaceManifest(entries, index, manifest);
        fixture = fixture with { Export = BuildEntries(entries) };
        if (corruptTrailingByte)
        {
            InvalidDataException error = await Assert.ThrowsAsync<InvalidDataException>(() =>
                DockerImageArchive.OpenAsync(new MemoryStream(fixture.Export), fixture.Id,
                    TestContext.Current.CancellationToken, directory, verifyLayerContent: verifyContent,
                    prepareFileSystem: verifyContent));
            Assert.Contains("descriptor", error.Message);
            Assert.Empty(Directory.EnumerateFileSystemEntries(directory));
            return;
        }
        await using DockerImageArchive archive = await DockerImageArchive.OpenAsync(
            new MemoryStream(fixture.Export), fixture.Id, TestContext.Current.CancellationToken, directory,
            verifyLayerContent: verifyContent, prepareFileSystem: verifyContent);
        Assert.Equal(digest, archive.Resolved.Manifest.Layers[0].Digest);
        Assert.Equal(stored.Length, archive.Resolved.Manifest.Layers[0].Size);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task ReusedLayerHashes_StillValidateEveryDiffId(bool gzip, bool wrongSecondDiffId)
    {
        DockerArchiveFixture fixture = DockerArchiveFixture.Create(oci: true, gzip: gzip);
        Dictionary<string, byte[]> entries = ReadEntries(fixture.Export);
        JsonObject index = JsonNode.Parse(entries["index.json"])!.AsObject();
        string path = "blobs/sha256/" + index["manifests"]![0]!["digest"]!.GetValue<string>()[7..];
        JsonObject manifest = JsonNode.Parse(entries[path])!.AsObject();
        manifest["layers"]!.AsArray().Add(manifest["layers"]![0]!.DeepClone());
        ReplaceManifest(entries, index, manifest);
        JsonObject config = JsonNode.Parse(fixture.Config)!.AsObject();
        config["rootfs"]!["diff_ids"]!.AsArray().Add(wrongSecondDiffId ?
            JsonValue.Create("sha256:" + new string('a', 64)) : config["rootfs"]!["diff_ids"]![0]!.DeepClone());
        config["history"]!.AsArray().Add(config["history"]![0]!.DeepClone());
        fixture = WithConfiguration(fixture with { Export = BuildEntries(entries) }, JsonSerializer.SerializeToUtf8Bytes(config), oci: true);
        if (wrongSecondDiffId)
        {
            InvalidDataException error = await Assert.ThrowsAsync<InvalidDataException>(() => OpenAsync(fixture));
            Assert.Contains("layer 2", error.Message);
            Assert.Empty(Directory.EnumerateFileSystemEntries(directory));
            return;
        }
        await using DockerImageArchive archive = await OpenAsync(fixture);
        Assert.Equal(2, archive.Resolved.Manifest.Layers.Length);
        Assert.Equal(archive.Resolved.Manifest.Layers[0].Digest, archive.Resolved.Manifest.Layers[1].Digest);
    }

    [Fact]
    public async Task SharedDescriptor_StillValidatesEachDeclaredSize()
    {
        DockerArchiveFixture fixture = DockerArchiveFixture.Create(oci: true);
        Dictionary<string, byte[]> entries = ReadEntries(fixture.Export);
        JsonObject index = JsonNode.Parse(entries["index.json"])!.AsObject();
        JsonObject duplicate = index["manifests"]![0]!.DeepClone().AsObject();
        duplicate["size"] = duplicate["size"]!.GetValue<long>() + 1;
        index["manifests"]!.AsArray().Add(duplicate);
        entries["index.json"] = JsonSerializer.SerializeToUtf8Bytes(index);
        await Assert.ThrowsAsync<InvalidDataException>(() => DockerImageArchive.OpenAsync(
            new MemoryStream(BuildEntries(entries)), fixture.Id, TestContext.Current.CancellationToken, directory));
        Assert.Empty(Directory.EnumerateFileSystemEntries(directory));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReusedLayer_StillValidatesEachDescriptorSize(bool gzip)
    {
        DockerArchiveFixture fixture = DockerArchiveFixture.Create(oci: true, gzip: gzip);
        Dictionary<string, byte[]> entries = ReadEntries(fixture.Export);
        JsonObject index = JsonNode.Parse(entries["index.json"])!.AsObject();
        string path = "blobs/sha256/" + index["manifests"]![0]!["digest"]!.GetValue<string>()[7..];
        JsonObject manifest = JsonNode.Parse(entries[path])!.AsObject();
        JsonObject duplicate = manifest["layers"]![0]!.DeepClone().AsObject();
        duplicate["size"] = fixture.StoredLayer.Length + 1;
        manifest["layers"]!.AsArray().Add(duplicate);
        ReplaceManifest(entries, index, manifest);
        JsonObject config = JsonNode.Parse(fixture.Config)!.AsObject();
        config["rootfs"]!["diff_ids"]!.AsArray().Add(config["rootfs"]!["diff_ids"]![0]!.DeepClone());
        config["history"]!.AsArray().Add(config["history"]![0]!.DeepClone());
        fixture = WithConfiguration(fixture with { Export = BuildEntries(entries) },
            JsonSerializer.SerializeToUtf8Bytes(config), oci: true);
        InvalidDataException error = await Assert.ThrowsAsync<InvalidDataException>(() => OpenAsync(fixture));
        Assert.Contains("descriptor", error.Message);
        Assert.Empty(Directory.EnumerateFileSystemEntries(directory));
    }

    [Fact]
    public async Task MemberRanges_AreIndependentSeekableAndBounded()
    {
        DockerArchiveFixture fixture = DockerArchiveFixture.Create(oci: true);
        await using DockerImageArchive archive = await OpenAsync(fixture);
        string digest = archive.Resolved.Manifest.Layers[0].Digest!;
        BlobDownloadResult first = await archive.GetRangeAsync(digest, 3, 10, TestContext.Current.CancellationToken);
        BlobDownloadResult second = await archive.GetRangeAsync(digest, 0, null, TestContext.Current.CancellationToken);
        await using Stream firstStream = first.Content;
        await using Stream secondStream = second.Content;
        Assert.True(firstStream.CanSeek);
        Assert.Equal(10, firstStream.Length);
        Assert.Equal(0, firstStream.Position);
        Assert.Equal(fixture.StoredLayer[3], firstStream.ReadByte());
        Assert.Equal(fixture.StoredLayer[0], secondStream.ReadByte());
        Assert.Equal(9, firstStream.Seek(-1, SeekOrigin.End));
        Assert.Equal(fixture.StoredLayer[12], firstStream.ReadByte());
        Assert.Equal(-1, firstStream.ReadByte());
        Assert.Throws<IOException>(() => firstStream.Seek(11, SeekOrigin.Begin));
        Assert.Throws<IOException>(() => firstStream.Seek(-1, SeekOrigin.Begin));
        BlobDownloadResult empty = await archive.GetRangeAsync(digest, fixture.StoredLayer.Length, null, TestContext.Current.CancellationToken);
        await using Stream emptyStream = empty.Content;
        Assert.Equal(0, emptyStream.Length);
        Assert.Null(empty.RangeEnd);
        Assert.Equal(fixture.StoredLayer.Length, empty.TotalLength);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => archive.GetRangeAsync(digest, fixture.StoredLayer.Length + 1, null,
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task AmbiguousClassicImageRecords_AreRejected()
    {
        DockerArchiveFixture fixture = DockerArchiveFixture.Create(scratch: true);
        byte[] manifest = Encoding.UTF8.GetBytes("""[{"Config":"config.json","Layers":[]},{"Config":"config.json","Layers":[]}]""");
        byte[] export = DockerArchiveFixture.BuildTar(("manifest.json", manifest), ("config.json", fixture.Config));
        await Assert.ThrowsAsync<InvalidDataException>(() => DockerImageArchive.OpenAsync(
            new MemoryStream(export), fixture.Id, TestContext.Current.CancellationToken, directory));
    }

    [Fact]
    public async Task MissingLayer_IsRejected()
    {
        DockerArchiveFixture fixture = DockerArchiveFixture.Create();
        byte[] manifest = Encoding.UTF8.GetBytes("""[{"Config":"config.json","Layers":["missing.tar"]}]""");
        byte[] export = DockerArchiveFixture.BuildTar(("manifest.json", manifest), ("config.json", fixture.Config));
        await Assert.ThrowsAsync<InvalidDataException>(() => DockerImageArchive.OpenAsync(
            new MemoryStream(export), fixture.Id, TestContext.Current.CancellationToken, directory));
    }

    [Fact]
    public async Task TruncatedMember_IsRejectedAndCleaned()
    {
        DockerArchiveFixture fixture = DockerArchiveFixture.Create();
        await Assert.ThrowsAnyAsync<Exception>(() => DockerImageArchive.OpenAsync(
            new MemoryStream(fixture.Export[..700]), fixture.Id, TestContext.Current.CancellationToken, directory));
        Assert.Empty(Directory.EnumerateFileSystemEntries(directory));
    }

    [Fact]
    public async Task CancelledExport_IsClosedAndCleaned()
    {
        using CancellationTokenSource cancellation = new();
        CancellingStream export = new(cancellation);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => DockerImageArchive.OpenAsync(
            export, "sha256:" + new string('a', 64), cancellation.Token, directory));
        Assert.True(export.Disposed);
        Assert.Empty(Directory.EnumerateFileSystemEntries(directory));
    }

    private Task<DockerImageArchive> OpenAsync(DockerArchiveFixture fixture) =>
        DockerImageArchive.OpenAsync(new MemoryStream(fixture.Export), fixture.Id, TestContext.Current.CancellationToken, directory);

    private static DockerArchiveFixture WithRawLayer(byte[] layer, bool sha512 = false)
    {
        DockerArchiveFixture fixture = DockerArchiveFixture.Create();
        Dictionary<string, byte[]> entries = ReadEntries(fixture.Export);
        entries["layer.tar"] = layer;
        fixture = fixture with { Export = BuildEntries(entries), StoredLayer = layer };
        JsonObject config = JsonNode.Parse(fixture.Config)!.AsObject();
        config["rootfs"]!["diff_ids"]![0] = sha512
            ? $"sha512:{Convert.ToHexStringLower(SHA512.HashData(layer))}"
            : DockerImageArchive.Hash(layer);
        return WithConfiguration(fixture, JsonSerializer.SerializeToUtf8Bytes(config), oci: false);
    }

    private static Dictionary<string, byte[]> ReadEntries(byte[] export)
    {
        Dictionary<string, byte[]> entries = new(StringComparer.Ordinal);
        using TarReader reader = new(new MemoryStream(export));
        TarEntry? entry;
        while ((entry = reader.GetNextEntry()) is not null)
        {
            using MemoryStream data = new();
            entry.DataStream!.CopyTo(data);
            entries.Add(entry.Name, data.ToArray());
        }
        return entries;
    }

    private static byte[] BuildEntries(Dictionary<string, byte[]> entries) =>
        DockerArchiveFixture.BuildTar([.. entries.Select(entry => (entry.Key, entry.Value))]);

    private static JsonObject AddIndex(Dictionary<string, byte[]> entries, byte[] index)
    {
        string digest = DockerImageArchive.Hash(index);
        entries["blobs/sha256/" + digest[7..]] = index;
        return new JsonObject
        {
            ["mediaType"] = "application/vnd.oci.image.index.v1+json",
            ["digest"] = digest,
            ["size"] = index.Length
        };
    }

    private static void ReplaceManifest(Dictionary<string, byte[]> entries, JsonObject index, JsonObject manifest)
    {
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(manifest);
        string digest = DockerImageArchive.Hash(bytes);
        entries["blobs/sha256/" + digest[7..]] = bytes;
        index["manifests"]![0]!["digest"] = digest;
        index["manifests"]![0]!["size"] = bytes.Length;
        entries["index.json"] = JsonSerializer.SerializeToUtf8Bytes(index);
    }

    private static DockerArchiveFixture WithConfiguration(DockerArchiveFixture fixture, byte[] config, bool oci)
    {
        Dictionary<string, byte[]> entries = ReadEntries(fixture.Export);
        string id = DockerImageArchive.Hash(config);
        if (oci)
        {
            JsonObject index = JsonNode.Parse(entries["index.json"])!.AsObject();
            string path = "blobs/sha256/" + index["manifests"]![0]!["digest"]!.GetValue<string>()[7..];
            JsonObject manifest = JsonNode.Parse(entries[path])!.AsObject();
            manifest["config"]!["digest"] = id;
            manifest["config"]!["size"] = config.Length;
            entries["blobs/sha256/" + id[7..]] = config;
            ReplaceManifest(entries, index, manifest);
        }
        else
        {
            entries["config.json"] = config;
        }
        return new DockerArchiveFixture(BuildEntries(entries), config, id, fixture.StoredLayer);
    }

    public void Dispose()
    {
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private sealed class CountingArchiveInput : Stream
    {
        private readonly MemoryStream inner;
        private readonly int maximumRead;
        private readonly CancellationTokenSource? cancelAfterRead;

        public CountingArchiveInput(byte[] bytes, int maximumRead = 257,
            CancellationTokenSource? cancelAfterRead = null)
        {
            inner = new MemoryStream(bytes);
            this.maximumRead = maximumRead;
            this.cancelAfterRead = cancelAfterRead;
        }

        public long BytesRead { get; private set; }
        public int ReadCount { get; private set; }
        public int MaximumRequestedRead { get; private set; }
        public int SeekCount { get; private set; }
        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => inner.CanSeek;
        public override bool CanWrite => false;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => inner.Position = value; }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            ReadCount++;
            MaximumRequestedRead = Math.Max(MaximumRequestedRead, buffer.Length);
            int count = inner.Read(buffer[..Math.Min(buffer.Length, maximumRead)]);
            BytesRead += count;
            cancelAfterRead?.Cancel();
            return count;
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(Read(buffer.Span));
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override long Seek(long offset, SeekOrigin loc)
        {
            SeekCount++;
            return inner.Seek(offset, loc);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                inner.Dispose();
            }
            base.Dispose(disposing);
        }

        public override void Flush() { }
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class CancellingStream : MemoryStream
    {
        private readonly CancellationTokenSource cancellation;

        public CancellingStream(CancellationTokenSource cancellation)
        {
            this.cancellation = cancellation;
        }

        public bool Disposed { get; private set; }

        public override Task CopyToAsync(Stream destination, int bufferSize, CancellationToken cancellationToken)
        {
            cancellation.Cancel();
            cancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }

        protected override void Dispose(bool disposing)
        {
            Disposed = true;
            base.Dispose(disposing);
        }
    }
}

internal sealed record DockerArchiveFixture(byte[] Export, byte[] Config, string Id, byte[] StoredLayer)
{
    internal static DockerArchiveFixture Create(bool oci = false, bool gzip = false, bool scratch = false,
        bool windows = false, bool corruptLayer = false, string? layerMediaType = null,
        string architecture = "amd64")
    {
        byte[] layer = BuildTar(("etc/example", "fixture"u8.ToArray()));
        byte[] storedLayer = layer;
        if (gzip)
        {
            using MemoryStream compressed = new();
            using (GZipStream gzipStream = new(compressed, CompressionLevel.Fastest, leaveOpen: true))
            {
                gzipStream.Write(layer);
            }
            storedLayer = compressed.ToArray();
        }
        byte[] config = Encoding.UTF8.GetBytes(
            "{\n \"architecture\":\"" + architecture + "\", \"os\":\"" + (windows ? "windows" : "linux") + "\"," +
            (windows ? "\"os.version\":\"10.0.20348.1\"," : "") +
            "\"config\":{\"Env\":[\"X=preserved\"],\"UnknownPreservedField\":42}," +
            "\"rootfs\":{\"type\":\"layers\",\"diff_ids\":[" + (scratch ? "" : "\"" + DockerImageArchive.Hash(layer) + "\"") + "]}," +
            "\"history\":[" + (scratch ? "" : "{\"created_by\":\"fixture\"}") + "],\"future_field\":{\"exact\":true}}\n");
        string id = DockerImageArchive.Hash(config);
        byte[] outputLayer = storedLayer.ToArray();
        if (corruptLayer)
        {
            outputLayer[100] ^= 0xff;
        }
        byte[] export;
        if (!oci)
        {
            byte[] manifest = Encoding.UTF8.GetBytes("[{\"Config\":\"config.json\",\"Layers\":[" + (scratch ? "" : "\"layer.tar\"") + "]}]");
            export = BuildTar(("manifest.json", manifest), ("config.json", config), ("layer.tar", outputLayer));
        }
        else
        {
            byte[] manifest = JsonSerializer.SerializeToUtf8Bytes(new
            {
                schemaVersion = 2,
                mediaType = "application/vnd.oci.image.manifest.v1+json",
                config = new { mediaType = "application/vnd.oci.image.config.v1+json", digest = id, size = config.Length },
                layers = scratch ? [] : new[] { new
                {
                    mediaType = layerMediaType ?? DockerImageArchive.RawLayerMediaType + (gzip ? "+gzip" : ""),
                    digest = DockerImageArchive.Hash(storedLayer),
                    size = storedLayer.Length
                } }
            });
            byte[] index = JsonSerializer.SerializeToUtf8Bytes(new
            {
                schemaVersion = 2,
                manifests = new[] { new { mediaType = "application/vnd.oci.image.manifest.v1+json", digest = DockerImageArchive.Hash(manifest), size = manifest.Length } }
            });
            export = BuildTar(("oci-layout", """{"imageLayoutVersion":"1.0.0"}"""u8.ToArray()), ("index.json", index),
                ("blobs/sha256/" + DockerImageArchive.Hash(manifest)[7..], manifest),
                ("blobs/sha256/" + id[7..], config), ("blobs/sha256/" + DockerImageArchive.Hash(storedLayer)[7..], outputLayer));
        }
        return new DockerArchiveFixture(export, config, id, storedLayer);
    }

    internal static byte[] BuildTar(params (string Path, byte[] Content)[] entries)
    {
        using MemoryStream stream = new();
        using (TarWriter writer = new(stream, leaveOpen: true))
        {
            foreach ((string path, byte[] content) in entries)
            {
                writer.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, path) { DataStream = new MemoryStream(content) });
            }
        }
        return stream.ToArray();
    }
}
