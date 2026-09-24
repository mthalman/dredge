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
        client.Setup(item => item.Blobs.GetAsync("library/image", digest, It.IsAny<CancellationToken>()))
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
                return new MemoryStream(bytes);
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
        client.Setup(item => item.Blobs.GetAsync("library/image", digest, It.IsAny<CancellationToken>()))
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
            .Setup(o => o.Blobs.GetAsync("library/image", firstDigest, It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new MemoryStream(first));
        client
            .Setup(o => o.Blobs.GetAsync("library/image", secondDigest, It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new MemoryStream(second));
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
            .Setup(o => o.Blobs.GetAsync(
                "library/image",
                digest,
                It.IsAny<CancellationToken>()))
            .Returns(async () =>
            {
                Interlocked.Increment(ref downloadCount);
                await Task.Delay(100, TestContext.Current.CancellationToken);
                return new MemoryStream(bytes);
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
            .Setup(o => o.Blobs.GetAsync("library/image", secondDigest, It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new MemoryStream(bytes));
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
                o => o.Blobs.GetAsync(
                    "library/image",
                    firstDigest,
                    It.IsAny<CancellationToken>()),
                Times.Never);
            client.Verify(
                o => o.Blobs.GetAsync(
                    "library/image",
                    secondDigest,
                    It.IsAny<CancellationToken>()),
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
            .Setup(o => o.Blobs.GetAsync("library/image", digest, It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new MemoryStream(bytes));
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
            .Setup(o => o.Blobs.GetAsync("library/image", digest, It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new MemoryStream(bytes));
        return client;
    }

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
