namespace Valleysoft.Dredge.Tests;

using System.Formats.Tar;
using System.IO.Compression;
using System.Text;
using Valleysoft.DockerRegistryClient.Models.Manifests;
using Valleysoft.DockerRegistryClient.Models.Manifests.Docker;
using Valleysoft.Dredge.Commands;

public class ImageHelperTests : IAsyncDisposable
{
    private readonly LayerCacheTestContext cache = new();
    public ValueTask DisposeAsync() => cache.DisposeAsync();

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SaveLayers_PreservesDirectoryLinksAndCycles(bool noSquash)
    {
        byte[] layer = CreateArchive(
            new(TarEntryType.SymbolicLink, "bin") { LinkName = "usr/bin" },
            new(TarEntryType.SymbolicLink, "tool-link") { LinkName = "bin/tool" },
            new(TarEntryType.SymbolicLink, "cycle-a") { LinkName = "cycle-b" },
            new(TarEntryType.SymbolicLink, "cycle-b") { LinkName = "cycle-a" },
            FileEntry("usr/bin/tool", "tool"));
        string output = await SaveLayersAsync([layer], noSquash);
        string extracted = noSquash ? LayerOutput(output, 0, layer) : output;

        Assert.Equal("usr/bin", new DirectoryInfo(Path.Combine(extracted, "bin")).LinkTarget?.Replace('\\', '/'));
        Assert.Equal("tool", File.ReadAllText(Path.Combine(extracted, "bin", "tool")));
        Assert.Equal("tool", File.ReadAllText(Path.Combine(extracted, "tool-link")));
        Assert.Equal("cycle-b", new FileInfo(Path.Combine(extracted, "cycle-a")).LinkTarget);
        Assert.Equal("cycle-a", new FileInfo(Path.Combine(extracted, "cycle-b")).LinkTarget);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SaveLayers_PreservesParentSegmentsInSymbolicTargets(bool noSquash)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }
        byte[] layer = CreateArchive(
            FileEntry("a/target", "lexical"),
            FileEntry("b/target", "resolved"),
            new(TarEntryType.Directory, "b/c"),
            new(TarEntryType.SymbolicLink, "a/jump") { LinkName = "../b/c" },
            new(TarEntryType.SymbolicLink, "link") { LinkName = "a/jump/../target" });

        string output = await SaveLayersAsync([layer], noSquash);
        string extracted = noSquash ? LayerOutput(output, 0, layer) : output;

        Assert.Equal("a/jump/../target", new FileInfo(Path.Combine(extracted, "link")).LinkTarget);
        Assert.Equal("resolved", File.ReadAllText(Path.Combine(extracted, "link")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SaveLayers_RootClampingThroughLinksCannotEscapeTheExport(bool noSquash)
    {
        byte[] layer = CreateArchive(
            FileEntry("outside", "inside image"),
            new(TarEntryType.SymbolicLink, "root") { LinkName = "." },
            new(TarEntryType.SymbolicLink, "link") { LinkName = "root/../outside" });
        string parent = noSquash ? Path.Combine(cache.Root, "export") : cache.Root;
        Directory.CreateDirectory(parent);
        string sentinel = Path.Combine(parent, "outside");
        File.WriteAllText(sentinel, "outside image");

        string output = await SaveLayersAsync([layer], noSquash);
        string extracted = noSquash ? LayerOutput(output, 0, layer) : output;

        Assert.Equal("inside image", File.ReadAllText(Path.Combine(extracted, "link")));
        Assert.Equal("outside", new FileInfo(Path.Combine(extracted, "link")).LinkTarget);
        Assert.Equal("outside image", File.ReadAllText(sentinel));
    }

    [Fact]
    public async Task SaveLayers_RebasesLinksOnlyAfterTheirFinalTargetsAreMerged()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }
        byte[] lower = CreateArchive(
            FileEntry("outside", "lower"),
            new(TarEntryType.SymbolicLink, "root") { LinkName = "." },
            new(TarEntryType.SymbolicLink, "link") { LinkName = "root/../outside" });
        byte[] upper = CreateArchive(
            new(TarEntryType.Directory, "nested/a"),
            FileEntry("nested/outside", "final"),
            new(TarEntryType.SymbolicLink, "root") { LinkName = "nested/a" });

        string output = await SaveLayersAsync([lower, upper]);

        Assert.Equal("root/../outside", new FileInfo(Path.Combine(output, "link")).LinkTarget);
        Assert.Equal("final", File.ReadAllText(Path.Combine(output, "link")));
    }

    [Fact]
    public async Task SaveLayers_DoesNotFollowPreExistingAbsoluteHostLinks()
    {
        string output = Path.Combine(cache.Root, "export");
        Directory.CreateDirectory(output);
        string sentinel = Path.Combine(cache.Root, "outside");
        File.WriteAllText(sentinel, "outside image");
        File.CreateSymbolicLink(Path.Combine(output, "host"), sentinel);
        byte[] layer = CreateArchive(new PaxTarEntry(TarEntryType.SymbolicLink, "alias") { LinkName = "host" });

        await Assert.ThrowsAsync<InvalidDataException>(() => SaveLayersAsync([layer]));

        Assert.Equal("outside image", File.ReadAllText(sentinel));
        Assert.False(ImageFileSystemExtractor.PathExists(Path.Combine(output, "alias")));
        Assert.Equal(sentinel, new FileInfo(Path.Combine(output, "host")).LinkTarget);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SaveLayers_DoesNotPartiallyRebaseRootClampedLongLinkChains(bool noSquash)
    {
        PaxTarEntry[] links = [.. Enumerable.Range(0, ImagePathResolver.MaximumLinkHops)
            .Select(index => new PaxTarEntry(TarEntryType.SymbolicLink, $"hop{index}")
            {
                LinkName = index == ImagePathResolver.MaximumLinkHops - 1 ? "." : $"hop{index + 1}"
            })];
        byte[] layer = CreateArchive([
            new(TarEntryType.SymbolicLink, "root") { LinkName = "." },
            .. links,
            new(TarEntryType.SymbolicLink, "link") { LinkName = "root/../hop0/../outside" }]);
        string output = Path.Combine(cache.Root, "export");
        string extracted = noSquash ? LayerOutput(output, 0, layer) : output;
        string parent = noSquash ? output : cache.Root;
        Directory.CreateDirectory(parent);
        string sentinel = Path.Combine(parent, "outside");
        File.WriteAllText(sentinel, "outside image");

        await Assert.ThrowsAsync<InvalidDataException>(() => SaveLayersAsync([layer], noSquash));

        Assert.False(ImageFileSystemExtractor.PathExists(Path.Combine(extracted, "link")));
        Assert.Equal("outside image", File.ReadAllText(sentinel));
    }

    [Fact]
    public async Task SaveLayers_HardLinkCapturesLowerContentBeforeALaterWhiteout()
    {
        byte[] lower = CreateArchive(FileEntry("original", "lower"));
        byte[] middle = CreateArchive(new PaxTarEntry(TarEntryType.HardLink, "alias") { LinkName = "original" });
        byte[] upper = CreateArchive(FileEntry(".wh.original", ""));

        string output = await SaveLayersAsync([lower, middle, upper]);

        Assert.Equal("lower", File.ReadAllText(Path.Combine(output, "alias")));
        Assert.False(File.Exists(Path.Combine(output, "original")));
    }

    [Fact]
    public async Task SaveLayers_UpperHardLinkChainsDoNotCaptureAnOverriddenLowerTarget()
    {
        byte[] lower = CreateArchive(FileEntry("target", "old"), FileEntry("replacement", "new"));
        byte[] upper = CreateArchive(
            new(TarEntryType.HardLink, "target") { LinkName = "replacement" },
            new(TarEntryType.HardLink, "alias") { LinkName = "target" });

        string output = await SaveLayersAsync([lower, upper]);

        Assert.Equal("new", File.ReadAllText(Path.Combine(output, "alias")));
    }

    [Theory]
    [InlineData(".wh.original")]
    [InlineData(".wh..wh..opq")]
    public async Task SaveLayers_HardLinksCannotReadHiddenLowerTargets(string marker)
    {
        byte[] lower = CreateArchive(FileEntry("original", "lower"));
        byte[] upper = CreateArchive(
            FileEntry(marker, ""), new(TarEntryType.HardLink, "alias") { LinkName = "original" });

        await Assert.ThrowsAsync<InvalidDataException>(() => SaveLayersAsync([lower, upper]));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SaveLayers_RootOpaqueWhiteoutRetainsCurrentLayerSiblings(bool markerFirst)
    {
        byte[] lower = CreateArchive(FileEntry("old", "old"), FileEntry("nested/old", "old"));
        PaxTarEntry marker = FileEntry(".wh..wh..opq", "");
        PaxTarEntry sibling = FileEntry("new", "new");
        byte[] upper = CreateArchive(markerFirst ? [marker, sibling] : [sibling, marker]);

        string output = await SaveLayersAsync([lower, upper]);

        Assert.Equal(["new"], Directory.EnumerateFileSystemEntries(output).Select(Path.GetFileName));
        Assert.Equal("new", File.ReadAllText(Path.Combine(output, "new")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SaveLayers_ReplacesInterLayerFileAndDirectoryWithoutForce(bool directoryFirst)
    {
        byte[] directory = CreateArchive(
            new(TarEntryType.Directory, "thing/"), FileEntry("thing/child", "child"));
        byte[] file = CreateArchive(FileEntry("thing", "file"));

        string output = await SaveLayersAsync(directoryFirst ? [directory, file] : [file, directory]);

        if (directoryFirst)
        {
            Assert.Equal("file", File.ReadAllText(Path.Combine(output, "thing")));
            Assert.False(Directory.Exists(Path.Combine(output, "thing")));
        }
        else
        {
            Assert.Equal("child", File.ReadAllText(Path.Combine(output, "thing", "child")));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SaveLayers_PreservesEmptyAndOpaqueDirectories(bool noSquash)
    {
        byte[] lower = CreateArchive(FileEntry("opaque/old", "old"));
        byte[] upper = CreateArchive(
            new(TarEntryType.Directory, "state/"),
            new(TarEntryType.Directory, "opaque/"),
            FileEntry("opaque/.wh..wh..opq", ""));

        string output = await SaveLayersAsync([lower, upper], noSquash);
        string extracted = noSquash ? LayerOutput(output, 1, upper) : output;

        Assert.True(Directory.Exists(Path.Combine(extracted, "state")));
        Assert.Empty(Directory.EnumerateFileSystemEntries(Path.Combine(extracted, "state")));
        Assert.True(Directory.Exists(Path.Combine(extracted, "opaque")));
        Assert.False(File.Exists(Path.Combine(extracted, "opaque", "old")));
        Assert.Equal(noSquash, File.Exists(Path.Combine(extracted, "opaque", ".wh..wh..opq")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SaveLayers_PreservesUnixModes(bool noSquash)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }
        UnixFileMode executable = (UnixFileMode)Convert.ToInt32("755", 8);
        UnixFileMode directoryMode = (UnixFileMode)Convert.ToInt32("750", 8);
        PaxTarEntry tool = FileEntry("bin/tool", "tool");
        tool.Mode = executable;
        byte[] layer = CreateArchive(
            new(TarEntryType.Directory, "bin/") { Mode = directoryMode }, tool);

        string output = await SaveLayersAsync([layer], noSquash);
        string extracted = noSquash ? LayerOutput(output, 0, layer) : output;

        Assert.Equal(executable, File.GetUnixFileMode(Path.Combine(extracted, "bin", "tool")));
        Assert.Equal(directoryMode, File.GetUnixFileMode(Path.Combine(extracted, "bin")));
    }

    [Fact]
    public async Task SaveLayers_HardLinkRetainsLowerTargetUnixMode()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }
        UnixFileMode executable = (UnixFileMode)Convert.ToInt32("755", 8);
        PaxTarEntry tool = FileEntry("original", "tool");
        tool.Mode = executable;
        byte[] lower = CreateArchive(tool);
        byte[] upper = CreateArchive(new PaxTarEntry(TarEntryType.HardLink, "alias")
        {
            LinkName = "original", Mode = 0
        });

        string output = await SaveLayersAsync([lower, upper]);

        Assert.Equal(executable, File.GetUnixFileMode(Path.Combine(output, "alias")));
    }

    [Fact]
    public async Task SaveLayers_RestrictiveModesDoNotBlockLaterLayerChanges()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }
        UnixFileMode readOnlyFile = (UnixFileMode)Convert.ToInt32("444", 8);
        UnixFileMode readOnlyDirectory = (UnixFileMode)Convert.ToInt32("555", 8);
        PaxTarEntry original = FileEntry("locked/original", "old");
        original.Mode = readOnlyFile;
        byte[] lower = CreateArchive(
            new(TarEntryType.Directory, "locked/") { Mode = readOnlyDirectory }, original);
        byte[] middle = CreateArchive(
            new(TarEntryType.HardLink, "alias") { LinkName = "locked/original" },
            FileEntry("locked/new", "added"));
        byte[] upper = CreateArchive(FileEntry("locked/original", "new"));

        string output = await SaveLayersAsync([lower, middle, upper]);

        try
        {
            Assert.Equal("old", File.ReadAllText(Path.Combine(output, "alias")));
            Assert.Equal("new", File.ReadAllText(Path.Combine(output, "locked", "original")));
            Assert.Equal("added", File.ReadAllText(Path.Combine(output, "locked", "new")));
            Assert.Equal(readOnlyFile, File.GetUnixFileMode(Path.Combine(output, "alias")));
            Assert.Equal(readOnlyDirectory, File.GetUnixFileMode(Path.Combine(output, "locked")));
        }
        finally
        {
            File.SetUnixFileMode(Path.Combine(output, "locked"), (UnixFileMode)Convert.ToInt32("755", 8));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SaveLayers_WindowsSanitationCollisionsAreExplicit(bool separateLayers)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }
        byte[] first = CreateArchive(FileEntry("nested/a:b", "first"));
        byte[] second = CreateArchive(FileEntry("nested/a?b", "second"));
        byte[][] layers = separateLayers ? [first, second] :
            [CreateArchive(FileEntry("nested/a:b", "first"), FileEntry("nested/a?b", "second"))];

        InvalidDataException error = await Assert.ThrowsAsync<InvalidDataException>(
            () => SaveLayersAsync(layers));

        Assert.Contains("nested/a:b", error.Message);
        Assert.Contains("nested/a?b", error.Message);
        if (separateLayers)
        {
            Assert.Equal("first", File.ReadAllText(Path.Combine(cache.Root, "export", "nested", "a-b")));
        }
    }

    [Fact]
    public async Task SaveLayers_NoSquashLayerSelectionKeepsItsPrefixAndWhiteouts()
    {
        byte[] lower = CreateArchive(FileEntry("lower", "lower"));
        byte[] upper = CreateArchive(FileEntry("upper", "upper"), FileEntry(".wh.lower", ""));

        string output = await SaveLayersAsync([lower, upper], noSquash: true, layerIndex: 1);

        Assert.Equal([Path.GetFileName(LayerOutput(output, 1, upper))],
            Directory.EnumerateDirectories(output).Select(Path.GetFileName));
        Assert.Equal("upper", File.ReadAllText(Path.Combine(LayerOutput(output, 1, upper), "upper")));
        Assert.True(File.Exists(Path.Combine(LayerOutput(output, 1, upper), ".wh.lower")));
    }

    [Fact]
    public async Task SaveLayers_SquashLayerSelectionStopsAtSelectedPrefix()
    {
        byte[] first = CreateArchive(FileEntry("value", "first"));
        byte[] second = CreateArchive(FileEntry("value", "second"));
        byte[] third = CreateArchive(FileEntry("value", "third"));

        string output = await SaveLayersAsync([first, second, third], layerIndex: 1);

        Assert.Equal("second", File.ReadAllText(Path.Combine(output, "value")));
    }

    [Fact]
    public async Task SaveLayers_NoSquashWindowsNamesInSeparateLayersDoNotCollide()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }
        byte[] first = CreateArchive(FileEntry("nested/a:b", "first"));
        byte[] second = CreateArchive(FileEntry("nested/a?b", "second"));

        string output = await SaveLayersAsync([first, second], noSquash: true);

        Assert.Equal("first", File.ReadAllText(Path.Combine(LayerOutput(output, 0, first), "nested", "a-b")));
        Assert.Equal("second", File.ReadAllText(Path.Combine(LayerOutput(output, 1, second), "nested", "a-b")));
    }

    private async Task<string> SaveLayersAsync(byte[][] layers, bool noSquash = false, int? layerIndex = null)
    {
        Mock<IDockerRegistryClient> client = new() { DefaultValue = DefaultValue.Mock };
        client.Setup(item => item.Manifests.GetAsync("library/image", "latest", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ManifestInfo("application/test", "sha256:manifest", new DockerManifest
            {
                Layers = [.. layers.Select(layer => new ManifestLayer
                {
                    Digest = LayerCacheTestContext.Digest(layer), Size = layer.Length
                })]
            }));
        foreach (byte[] layer in layers)
        {
            client.Setup(item => item.Blobs.GetRangeAsync(
                "library/image", LayerCacheTestContext.Digest(layer), 0, null, It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => FullBlob(layer));
        }
        Mock<IDockerRegistryClientFactory> factory = new();
        factory.Setup(item => item.GetClientAsync(null, It.IsAny<CancellationToken>())).ReturnsAsync(client.Object);
        string output = Path.Combine(cache.Root, "export");
        await SaveAsync(factory.Object, "image", output, layerIndex, "--layer-index", noSquash,
            new PlatformOptionsBase(), TestContext.Current.CancellationToken);
        return output;
    }

    private static string LayerOutput(string root, int index, byte[] layer) =>
        Path.Combine(root, $"layer{index}-{LayerCacheTestContext.Digest(layer)["sha256:".Length..]}");

    private static PaxTarEntry FileEntry(string name, string content) =>
        new(TarEntryType.RegularFile, name)
        {
            DataStream = new MemoryStream(Encoding.UTF8.GetBytes(content))
        };

    private static byte[] CreateArchive(params PaxTarEntry[] entries)
    {
        using MemoryStream output = new();
        using (GZipStream gzip = new(output, CompressionMode.Compress, leaveOpen: true))
        using (TarWriter writer = new(gzip, leaveOpen: true))
        {
            foreach (PaxTarEntry entry in entries)
            {
                writer.WriteEntry(entry);
            }
        }
        return output.ToArray();
    }

    private Task SaveAsync(
        IDockerRegistryClientFactory factory, string image, string destPath, int? layerIndex,
        string layerIndexOptionName, bool noSquash, PlatformOptionsBase options,
        CancellationToken cancellationToken = default, IDredgePathProvider? pathProvider = null) =>
        ImageHelper.SaveImageLayersToDiskAsync(factory, image, destPath, layerIndex,
            layerIndexOptionName, noSquash, options, cancellationToken, pathProvider ?? cache.Paths);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SaveImageLayersToDiskAsync_SharedTempCannotSubstituteExtractionScratch(bool noSquash)
    {
        byte[] bytes = LayerCacheTestContext.ReadBytes(CreateLayer(("verified.txt", "verified")));
        string digest = LayerCacheTestContext.Digest(bytes);
        string sharedTemp = Path.Combine(cache.Root, "shared-temp");
        Directory.CreateDirectory(sharedTemp);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(sharedTemp,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute |
                UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute);
        }
        TestDredgePathProvider paths = new(sharedTemp, cache.Paths.CachePath);
        string dataPath = Path.Combine(cache.Paths.CachePath, "layer-store", "data");
        List<string> exposedScratch = [];
        string[] privateScratch = [];
        Mock<IDockerRegistryClient> client = CreateSingleLayerClient(digest, () => new MemoryStream(bytes));
        client.Setup(item => item.Blobs.GetRangeAsync("library/image", digest, 0, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                foreach (string directory in Directory.GetDirectories(sharedTemp, "layer-*"))
                {
                    exposedScratch.Add(directory);
                    Directory.Move(directory, $"{directory}.original");
                    Directory.CreateDirectory(directory);
                    File.WriteAllText(Path.Combine(directory, "injected.txt"), "untrusted");
                }
                privateScratch = Directory.GetDirectories(dataPath, "*.scratch.dir");
                return FullBlob(bytes);
            });
        Mock<IDockerRegistryClientFactory> factory = new();
        factory.Setup(item => item.GetClientAsync(null, It.IsAny<CancellationToken>())).ReturnsAsync(client.Object);
        string output = Path.Combine(cache.Root, "output");

        await SaveAsync(factory.Object, "image", output, null, "--layer-index", noSquash,
            new PlatformOptionsBase(), TestContext.Current.CancellationToken, paths);

        string extracted = noSquash ? Path.Combine(output, $"layer0-{digest["sha256:".Length..]}") : output;
        Assert.Equal("verified", await File.ReadAllTextAsync(
            Path.Combine(extracted, "verified.txt"), TestContext.Current.CancellationToken));
        Assert.False(File.Exists(Path.Combine(extracted, "injected.txt")));
        Assert.Empty(exposedScratch);
        Assert.Single(privateScratch);
        Assert.False(Directory.Exists(privateScratch[0]));
        Assert.Empty(Directory.GetFiles(dataPath, "*.scratch"));
        Assert.Empty(Directory.EnumerateFileSystemEntries(sharedTemp));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SaveImageLayersToDiskAsync_FailedDownloadRemovesPrivateScratch(bool canceled)
    {
        byte[] bytes = LayerCacheTestContext.ReadBytes(CreateLayer(("file", "content")));
        string digest = LayerCacheTestContext.Digest(bytes);
        Mock<IDockerRegistryClient> client = CreateSingleLayerClient(digest, () => new MemoryStream(bytes));
        Exception failure = canceled
            ? new OperationCanceledException()
            : new IOException("Download failed.");
        client.Setup(item => item.Blobs.GetRangeAsync("library/image", digest, 0, null, It.IsAny<CancellationToken>()))
            .ThrowsAsync(failure);
        Mock<IDockerRegistryClientFactory> factory = new();
        factory.Setup(item => item.GetClientAsync(null, It.IsAny<CancellationToken>())).ReturnsAsync(client.Object);

        Exception? actual = await Record.ExceptionAsync(() =>
            SaveAsync(factory.Object, "image", Path.Combine(cache.Root, "output"), null,
                "--layer-index", false, new PlatformOptionsBase(), TestContext.Current.CancellationToken));

        Assert.Same(failure, actual);
        Assert.Empty(Directory.EnumerateFileSystemEntries(
            Path.Combine(cache.Paths.CachePath, "layer-store", "data")));
    }

    [Fact]
    public async Task SaveImageLayersToDiskAsync_AppliesLayersAndWhiteouts()
    {
        string id = Guid.NewGuid().ToString("N");
        byte[] first = LayerCacheTestContext.ReadBytes(CreateLayer(
            ("keep.txt", "old"), ("delete.txt", "delete"), ("colon:name.txt", "delete"),
            ("nested/delete.txt", "nested delete"), ("removed/child.txt", "removed"), ("opaque/old.txt", "old")));
        byte[] second = LayerCacheTestContext.ReadBytes(CreateLayer(
            ("keep.txt", "new"), (".wh.delete.txt", string.Empty), (".wh.colon:name.txt", string.Empty),
            ("nested/.wh.delete.txt", string.Empty), (".wh.removed", string.Empty),
            ("opaque/!new.txt", "new"), ("opaque/.wh..wh..opq", string.Empty), ("nested/added.txt", "added")));
        string firstDigest = LayerCacheTestContext.Digest(first);
        string secondDigest = LayerCacheTestContext.Digest(second);
        string output = Path.Combine(Path.GetTempPath(), $"dredge-output-{id}");
        string layerCache = Path.Combine(cache.Paths.TempPath, "layers");
        string firstCache = Path.Combine(layerCache, $"{id}-first");
        string secondCache = Path.Combine(layerCache, $"{id}-second");
        Mock<IDockerRegistryClient> client = new() { DefaultValue = DefaultValue.Mock };
        client
            .Setup(o => o.Manifests.GetAsync("library/image", "latest", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ManifestInfo(
                "application/test",
                "sha256:manifest",
                new DockerManifest
                {
                    Layers =
                    [
                        new ManifestLayer { Digest = firstDigest, Size = first.Length },
                        new ManifestLayer { Digest = secondDigest, Size = second.Length }
                    ]
                }));
        client
            .Setup(o => o.Blobs.GetRangeAsync("library/image", firstDigest, 0, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => FullBlob(first));
        client
            .Setup(o => o.Blobs.GetRangeAsync("library/image", secondDigest, 0, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => FullBlob(second));
        Mock<IDockerRegistryClientFactory> factory = new();
        factory.Setup(o => o.GetClientAsync(null, It.IsAny<CancellationToken>())).ReturnsAsync(client.Object);

        try
        {
            await SaveAsync(
                factory.Object,
                "image",
                output,
                layerIndex: null,
                "--layer-index",
                noSquash: false,
                new PlatformOptionsBase());

            string colonFileName = OperatingSystem.IsWindows() ? "colon-name.txt" : "colon:name.txt";
            Assert.Equal("new", File.ReadAllText(Path.Combine(output, "keep.txt")));
            Assert.False(File.Exists(Path.Combine(output, "delete.txt")));
            Assert.False(File.Exists(Path.Combine(output, colonFileName)));
            Assert.False(File.Exists(Path.Combine(output, "nested", "delete.txt")));
            Assert.False(Directory.Exists(Path.Combine(output, "removed")));
            Assert.False(File.Exists(Path.Combine(output, "opaque", "old.txt")));
            Assert.Equal("new", File.ReadAllText(Path.Combine(output, "opaque", "!new.txt")));
            Assert.Equal("added", File.ReadAllText(Path.Combine(output, "nested", "added.txt")));
        }
        finally
        {
            if (Directory.Exists(output))
            {
                Directory.Delete(output, recursive: true);
            }
            if (Directory.Exists(firstCache))
            {
                Directory.Delete(firstCache, recursive: true);
            }
            if (Directory.Exists(secondCache))
            {
                Directory.Delete(secondCache, recursive: true);
            }
        }
    }

    [Fact]
    public async Task SaveImageLayersToDiskAsync_ConcurrentCallsPublishSameLayerAtomically()
    {
        string id = Guid.NewGuid().ToString("N");
        byte[] bytes = LayerCacheTestContext.ReadBytes(CreateLayer(("file.txt", "content")));
        string digest = LayerCacheTestContext.Digest(bytes);
        string tempRoot = Path.Combine(Path.GetTempPath(), $"dredge-concurrent-cache-{id}");
        string firstOutput = Path.Combine(tempRoot, "first-output");
        string secondOutput = Path.Combine(tempRoot, "second-output");
        int downloadCount = 0;
        Mock<IDockerRegistryClient> client = CreateSingleLayerClient(
            digest,
            () => new MemoryStream(bytes));
        client
            .Setup(o => o.Blobs.GetRangeAsync(
                "library/image",
                digest,
                0, null, It.IsAny<CancellationToken>()))
            .Returns(async () =>
            {
                Interlocked.Increment(ref downloadCount);
                await Task.Delay(100, TestContext.Current.CancellationToken);
                return FullBlob(bytes);
            });
        Mock<IDockerRegistryClientFactory> factory = new();
        factory.Setup(o => o.GetClientAsync(null, It.IsAny<CancellationToken>())).ReturnsAsync(client.Object);
        TestDredgePathProvider pathProvider = new(tempRoot);

        try
        {
            Task first = SaveAsync(
                factory.Object,
                "image",
                firstOutput,
                layerIndex: null,
                "--layer-index",
                noSquash: false,
                new PlatformOptionsBase(),
                TestContext.Current.CancellationToken,
                pathProvider);
            Task second = SaveAsync(
                factory.Object,
                "image",
                secondOutput,
                layerIndex: null,
                "--layer-index",
                noSquash: false,
                new PlatformOptionsBase(),
                TestContext.Current.CancellationToken,
                pathProvider);

            await Task.WhenAll(first, second);

            Assert.Equal(
                "content",
                await File.ReadAllTextAsync(
                    Path.Combine(firstOutput, "file.txt"),
                    TestContext.Current.CancellationToken));
            Assert.Equal(
                "content",
                await File.ReadAllTextAsync(
                    Path.Combine(secondOutput, "file.txt"),
                    TestContext.Current.CancellationToken));
            Assert.Equal(1, downloadCount);
            Assert.Single(Directory.GetFiles(
                Path.Combine(pathProvider.CachePath, "layer-store", "data"), "*.blob"));
        }
        finally
        {
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, recursive: true);
            }
        }
    }

    [Fact]
    public async Task SaveImageLayersToDiskAsync_WhenLayerIndexIsLastManifestLayer_Succeeds()
    {
        string id = Guid.NewGuid().ToString("N");
        string firstDigest = $"sha256:{id}-one";
        byte[] bytes = LayerCacheTestContext.ReadBytes(CreateLayer(("file.txt", "content")));
        string secondDigest = LayerCacheTestContext.Digest(bytes);
        string tempRoot = Path.Combine(Path.GetTempPath(), $"dredge-layer-boundary-{id}");
        Mock<IDockerRegistryClient> client = new() { DefaultValue = DefaultValue.Mock };
        client
            .Setup(o => o.Manifests.GetAsync("library/image", "latest", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ManifestInfo(
                "application/test",
                "sha256:manifest",
                new DockerManifest
                {
                    Layers =
                    [
                        new ManifestLayer { Digest = firstDigest },
                        new ManifestLayer { Digest = secondDigest, Size = bytes.Length }
                    ]
                }));
        client
            .Setup(o => o.Blobs.GetRangeAsync("library/image", secondDigest, 0, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => FullBlob(bytes));
        Mock<IDockerRegistryClientFactory> factory = new();
        factory.Setup(o => o.GetClientAsync(null, It.IsAny<CancellationToken>())).ReturnsAsync(client.Object);

        try
        {
            await SaveAsync(
                factory.Object,
                "image",
                Path.Combine(tempRoot, "output"),
                layerIndex: 1,
                "--layer-index",
                noSquash: true,
                new PlatformOptionsBase(),
                TestContext.Current.CancellationToken,
                new TestDredgePathProvider(tempRoot));

            client.Verify(
                o => o.Blobs.GetRangeAsync(
                    "library/image",
                    firstDigest,
                    0, null, It.IsAny<CancellationToken>()),
                Times.Never);
            client.Verify(
                o => o.Blobs.GetRangeAsync(
                    "library/image",
                    secondDigest,
                    0, null, It.IsAny<CancellationToken>()),
                Times.Once);
        }
        finally
        {
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, recursive: true);
            }
        }
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(2)]
    public async Task SaveImageLayersToDiskAsync_WhenLayerIndexIsOutOfRange_Throws(int layerIndex)
    {
        Mock<IDockerRegistryClient> client = new() { DefaultValue = DefaultValue.Mock };
        client
            .Setup(o => o.Manifests.GetAsync("library/image", "latest", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ManifestInfo(
                "application/test",
                "sha256:manifest",
                new DockerManifest
                {
                    Layers =
                    [
                        new ManifestLayer { Digest = "sha256:one" },
                        new ManifestLayer { Digest = "sha256:two" }
                    ]
                }));
        Mock<IDockerRegistryClientFactory> factory = new();
        factory.Setup(o => o.GetClientAsync(null, It.IsAny<CancellationToken>())).ReturnsAsync(client.Object);

        Exception exception = await Assert.ThrowsAsync<Exception>(
            () => SaveAsync(
                factory.Object,
                "image",
                "output",
                layerIndex,
                "--layer-index",
                noSquash: false,
                new PlatformOptionsBase(),
                TestContext.Current.CancellationToken));

        Assert.Equal(
            "Value for the '--layer-index' option must be in the range 0-1.",
            exception.Message);
        client.Verify(
            o => o.Blobs.GetAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task SaveImageLayersToDiskAsync_WhenManifestHasNoLayers_Throws()
    {
        Mock<IDockerRegistryClient> client = new() { DefaultValue = DefaultValue.Mock };
        client
            .Setup(o => o.Manifests.GetAsync("library/image", "latest", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ManifestInfo(
                "application/test",
                "sha256:manifest",
                new DockerManifest { Layers = [] }));
        Mock<IDockerRegistryClientFactory> factory = new();
        factory.Setup(o => o.GetClientAsync(null, It.IsAny<CancellationToken>())).ReturnsAsync(client.Object);

        Exception exception = await Assert.ThrowsAsync<Exception>(
            () => SaveAsync(
                factory.Object,
                "image",
                "output",
                layerIndex: 0,
                "--layer-index",
                noSquash: false,
                new PlatformOptionsBase(),
                TestContext.Current.CancellationToken));

        Assert.Equal(
            "The image has no layers, so the '--layer-index' option cannot be used.",
            exception.Message);
        client.Verify(
            o => o.Blobs.GetAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task SaveImageLayersToDiskAsync_WhenDigestEncodedPortionContainsColon_Throws()
    {
        const string Digest = "sha256:C:escape";
        Mock<IDockerRegistryClient> client = CreateSingleLayerClient(
            Digest,
            () => CreateLayer(("file.txt", "content")));
        Mock<IDockerRegistryClientFactory> factory = new();
        factory.Setup(o => o.GetClientAsync(null, It.IsAny<CancellationToken>())).ReturnsAsync(client.Object);

        InvalidDataException exception = await Assert.ThrowsAsync<InvalidDataException>(
            () => SaveAsync(
                factory.Object,
                "image",
                "output",
                layerIndex: null,
                "--layer-index",
                noSquash: false,
                new PlatformOptionsBase()));

        Assert.Equal("Invalid layer digest 'C:escape'.", exception.Message);
        client.Verify(
            o => o.Blobs.GetAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task SaveImageLayersToDiskAsync_WhenEntryEscapesLayerDirectory_Throws()
    {
        string id = Guid.NewGuid().ToString("N");
        byte[] bytes = LayerCacheTestContext.ReadBytes(CreateLayer(($"../escaped-{id}.txt", "escaped")));
        string digest = LayerCacheTestContext.Digest(bytes);
        string escapedPath = Path.Combine(cache.Paths.TempPath, "layers", $"escaped-{id}.txt");
        string layerCache = Path.Combine(cache.Paths.TempPath, "layers", id);
        Mock<IDockerRegistryClient> client = new() { DefaultValue = DefaultValue.Mock };
        client
            .Setup(o => o.Manifests.GetAsync("library/image", "latest", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ManifestInfo(
                "application/test",
                "sha256:manifest",
                new DockerManifest
                {
                    Layers = [new ManifestLayer { Digest = digest, Size = bytes.Length }]
                }));
        client
            .Setup(o => o.Blobs.GetRangeAsync("library/image", digest, 0, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => FullBlob(bytes));
        Mock<IDockerRegistryClientFactory> factory = new();
        factory.Setup(o => o.GetClientAsync(null, It.IsAny<CancellationToken>())).ReturnsAsync(client.Object);

        try
        {
            InvalidDataException exception = await Assert.ThrowsAsync<InvalidDataException>(
                () => SaveAsync(
                    factory.Object,
                    "image",
                    Path.Combine(Path.GetTempPath(), $"dredge-output-{id}"),
                    layerIndex: null,
                    "--layer-index",
                    noSquash: false,
                    new PlatformOptionsBase()));

            Assert.Contains($"escaped-{id}.txt", exception.Message);
            Assert.False(File.Exists(escapedPath));
        }
        finally
        {
            if (Directory.Exists(layerCache))
            {
                Directory.Delete(layerCache, recursive: true);
            }
            if (File.Exists(escapedPath))
            {
                File.Delete(escapedPath);
            }
        }
    }

    [Fact]
    public async Task SaveImageLayersToDiskAsync_WhenLinkTargetEscapesLayerDirectory_Throws()
    {
        string id = Guid.NewGuid().ToString("N");
        string digest = $"sha256:{id}";
        string layerCache = Path.Combine(cache.Paths.TempPath, "layers", id);
        Mock<IDockerRegistryClient> client = CreateSingleLayerClient(
            digest,
            () => CreateSymbolicLinkLayer("link", "../outside"));
        Mock<IDockerRegistryClientFactory> factory = new();
        factory.Setup(o => o.GetClientAsync(null, It.IsAny<CancellationToken>())).ReturnsAsync(client.Object);

        try
        {
            await Assert.ThrowsAsync<InvalidDataException>(
                () => SaveAsync(
                    factory.Object,
                    "image",
                    Path.Combine(Path.GetTempPath(), $"dredge-output-{id}"),
                    layerIndex: null,
                    "--layer-index",
                    noSquash: false,
                    new PlatformOptionsBase()));
        }
        finally
        {
            if (Directory.Exists(layerCache))
            {
                Directory.Delete(layerCache, recursive: true);
            }
        }
    }

    [Fact]
    public async Task SaveImageLayersToDiskAsync_WhenSymlinkChainEscapesLayerDirectory_Throws()
    {
        string id = Guid.NewGuid().ToString("N");
        string digest = $"sha256:{id}";
        string layerCache = Path.Combine(cache.Paths.TempPath, "layers", id);
        string escapedPath = Path.Combine(cache.Paths.TempPath, "layers", $"escaped-{id}.txt");
        Mock<IDockerRegistryClient> client = CreateSingleLayerClient(
            digest,
            () => CreateSymlinkChainLayer(id));
        Mock<IDockerRegistryClientFactory> factory = new();
        factory.Setup(o => o.GetClientAsync(null, It.IsAny<CancellationToken>())).ReturnsAsync(client.Object);

        try
        {
            await Assert.ThrowsAsync<InvalidDataException>(
                () => SaveAsync(
                    factory.Object,
                    "image",
                    Path.Combine(Path.GetTempPath(), $"dredge-output-{id}"),
                    layerIndex: null,
                    "--layer-index",
                    noSquash: false,
                    new PlatformOptionsBase()));

            Assert.False(File.Exists(escapedPath));
        }
        finally
        {
            if (Directory.Exists(layerCache))
            {
                Directory.Delete(layerCache, recursive: true);
            }
            if (File.Exists(escapedPath))
            {
                File.Delete(escapedPath);
            }
        }
    }

    [Theory]
    [InlineData(".wh..")]
    [InlineData(".wh...")]
    public async Task SaveImageLayersToDiskAsync_WhenWhiteoutTargetIsSpecialPath_Throws(string whiteoutName)
    {
        string id = Guid.NewGuid().ToString("N");
        string digest = $"sha256:{id}";
        string layerCache = Path.Combine(cache.Paths.TempPath, "layers", id);
        Mock<IDockerRegistryClient> client = CreateSingleLayerClient(
            digest,
            () => CreateLayer((whiteoutName, string.Empty)));
        Mock<IDockerRegistryClientFactory> factory = new();
        factory.Setup(o => o.GetClientAsync(null, It.IsAny<CancellationToken>())).ReturnsAsync(client.Object);

        try
        {
            InvalidDataException exception = await Assert.ThrowsAsync<InvalidDataException>(
                () => SaveAsync(
                    factory.Object,
                    "image",
                    Path.Combine(Path.GetTempPath(), $"dredge-output-{id}"),
                    layerIndex: null,
                    "--layer-index",
                    noSquash: false,
                    new PlatformOptionsBase()));

            Assert.Contains("Invalid whiteout target", exception.Message);
        }
        finally
        {
            if (Directory.Exists(layerCache))
            {
                Directory.Delete(layerCache, recursive: true);
            }
        }
    }

    private static Mock<IDockerRegistryClient> CreateSingleLayerClient(
        string digest,
        Func<Stream> createLayer)
    {
        byte[] bytes = LayerCacheTestContext.ReadBytes(createLayer());
        if (digest != "sha256:C:escape")
        {
            digest = LayerCacheTestContext.Digest(bytes);
        }
        Mock<IDockerRegistryClient> client = new() { DefaultValue = DefaultValue.Mock };
        client
            .Setup(o => o.Manifests.GetAsync("library/image", "latest", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ManifestInfo(
                "application/test",
                "sha256:manifest",
                new DockerManifest
                {
                    Layers = [new ManifestLayer { Digest = digest, Size = bytes.Length }]
                }));
        client
            .Setup(o => o.Blobs.GetRangeAsync("library/image", digest, 0, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => FullBlob(bytes));
        return client;
    }

    private static Valleysoft.DockerRegistryClient.BlobDownloadResult FullBlob(byte[] bytes) =>
        new(new MemoryStream(bytes), false, null, null, bytes.Length);

    private static Stream CreateLayer(params (string Name, string Content)[] files)
    {
        MemoryStream compressed = new();
        using (GZipStream gzip = new(compressed, CompressionMode.Compress, leaveOpen: true))
        using (TarWriter writer = new(gzip, leaveOpen: true))
        {
            foreach ((string name, string content) in files)
            {
                PaxTarEntry entry = new(TarEntryType.RegularFile, name)
                {
                    DataStream = new MemoryStream(Encoding.UTF8.GetBytes(content))
                };
                writer.WriteEntry(entry);
            }
        }
        compressed.Position = 0;
        return compressed;
    }

    private static Stream CreateSymbolicLinkLayer(string name, string linkTarget)
    {
        MemoryStream compressed = new();
        using (GZipStream gzip = new(compressed, CompressionMode.Compress, leaveOpen: true))
        using (TarWriter writer = new(gzip, leaveOpen: true))
        {
            PaxTarEntry entry = new(TarEntryType.SymbolicLink, name)
            {
                LinkName = linkTarget
            };
            writer.WriteEntry(entry);
        }
        compressed.Position = 0;
        return compressed;
    }

    private static Stream CreateSymlinkChainLayer(string id)
    {
        MemoryStream compressed = new();
        using (GZipStream gzip = new(compressed, CompressionMode.Compress, leaveOpen: true))
        using (TarWriter writer = new(gzip, leaveOpen: true))
        {
            writer.WriteEntry(new PaxTarEntry(TarEntryType.SymbolicLink, "a") { LinkName = "." });
            writer.WriteEntry(new PaxTarEntry(TarEntryType.SymbolicLink, "a/b") { LinkName = ".." });
            writer.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, $"a/b/escaped-{id}.txt")
            {
                DataStream = new MemoryStream(Encoding.UTF8.GetBytes("escaped"))
            });
        }
        compressed.Position = 0;
        return compressed;
    }
}
