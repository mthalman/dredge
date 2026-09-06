namespace Valleysoft.Dredge.Tests;

using System.Formats.Tar;
using System.IO.Compression;
using System.Text;
using Valleysoft.DockerRegistryClient.Models.Manifests;
using Valleysoft.DockerRegistryClient.Models.Manifests.Docker;
using Valleysoft.Dredge.Commands;

public class ImageHelperTests
{
    [Fact]
    public async Task SaveImageLayersToDiskAsync_AppliesLayersAndWhiteouts()
    {
        string id = Guid.NewGuid().ToString("N");
        string firstDigest = $"sha256:{id}-first";
        string secondDigest = $"sha256:{id}-second";
        string output = Path.Combine(Path.GetTempPath(), $"dredge-output-{id}");
        string layerCache = Path.Combine(DredgeState.DredgeTempPath, "layers");
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
                        new ManifestLayer { Digest = firstDigest },
                        new ManifestLayer { Digest = secondDigest }
                    ]
                }));
        client
            .Setup(o => o.Blobs.GetAsync("library/image", firstDigest, It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => CreateLayer(
                ("keep.txt", "old"),
                ("delete.txt", "delete"),
                ("colon:name.txt", "delete"),
                ("nested/delete.txt", "nested delete"),
                ("removed/child.txt", "removed"),
                ("opaque/old.txt", "old")));
        client
            .Setup(o => o.Blobs.GetAsync("library/image", secondDigest, It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => CreateLayer(
                ("keep.txt", "new"),
                (".wh.delete.txt", string.Empty),
                (".wh.colon:name.txt", string.Empty),
                ("nested/.wh.delete.txt", string.Empty),
                (".wh.removed", string.Empty),
                ("opaque/!new.txt", "new"),
                ("opaque/.wh..wh..opq", string.Empty),
                ("nested/added.txt", "added")));
        Mock<IDockerRegistryClientFactory> factory = new();
        factory.Setup(o => o.GetClientAsync(null)).ReturnsAsync(client.Object);

        try
        {
            await ImageHelper.SaveImageLayersToDiskAsync(
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
        string digest = $"sha256:{id}";
        string tempRoot = Path.Combine(Path.GetTempPath(), $"dredge-concurrent-cache-{id}");
        string firstOutput = Path.Combine(tempRoot, "first-output");
        string secondOutput = Path.Combine(tempRoot, "second-output");
        string layersPath = Path.Combine(tempRoot, "layers");
        TaskCompletionSource bothDownloadsStarted = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        int downloadCount = 0;
        Mock<IDockerRegistryClient> client = CreateSingleLayerClient(
            digest,
            () => CreateLayer(("file.txt", "content")));
        client
            .Setup(o => o.Blobs.GetAsync(
                "library/image",
                digest,
                It.IsAny<CancellationToken>()))
            .Returns(async () =>
            {
                if (Interlocked.Increment(ref downloadCount) == 2)
                {
                    bothDownloadsStarted.SetResult();
                }
                await bothDownloadsStarted.Task.WaitAsync(
                    TestContext.Current.CancellationToken);
                return CreateLayer(("file.txt", "content"));
            });
        Mock<IDockerRegistryClientFactory> factory = new();
        factory.Setup(o => o.GetClientAsync(null)).ReturnsAsync(client.Object);
        TestDredgePathProvider pathProvider = new(tempRoot);

        try
        {
            Task first = ImageHelper.SaveImageLayersToDiskAsync(
                factory.Object,
                "image",
                firstOutput,
                layerIndex: null,
                "--layer-index",
                noSquash: false,
                new PlatformOptionsBase(),
                TestContext.Current.CancellationToken,
                pathProvider);
            Task second = ImageHelper.SaveImageLayersToDiskAsync(
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

            Assert.Equal(2, downloadCount);
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
            Assert.True(Directory.Exists(Path.Combine(layersPath, id)));
            Assert.Empty(Directory.EnumerateDirectories(layersPath, "*.tmp"));
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
        string secondDigest = $"sha256:{id}-two";
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
                        new ManifestLayer { Digest = secondDigest }
                    ]
                }));
        client
            .Setup(o => o.Blobs.GetAsync("library/image", secondDigest, It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => CreateLayer(("file.txt", "content")));
        Mock<IDockerRegistryClientFactory> factory = new();
        factory.Setup(o => o.GetClientAsync(null)).ReturnsAsync(client.Object);

        try
        {
            await ImageHelper.SaveImageLayersToDiskAsync(
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
        factory.Setup(o => o.GetClientAsync(null)).ReturnsAsync(client.Object);

        Exception exception = await Assert.ThrowsAsync<Exception>(
            () => ImageHelper.SaveImageLayersToDiskAsync(
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
        factory.Setup(o => o.GetClientAsync(null)).ReturnsAsync(client.Object);

        Exception exception = await Assert.ThrowsAsync<Exception>(
            () => ImageHelper.SaveImageLayersToDiskAsync(
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
        factory.Setup(o => o.GetClientAsync(null)).ReturnsAsync(client.Object);

        InvalidDataException exception = await Assert.ThrowsAsync<InvalidDataException>(
            () => ImageHelper.SaveImageLayersToDiskAsync(
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
        string digest = $"sha256:{id}";
        string escapedPath = Path.Combine(DredgeState.DredgeTempPath, "layers", $"escaped-{id}.txt");
        string layerCache = Path.Combine(DredgeState.DredgeTempPath, "layers", id);
        Mock<IDockerRegistryClient> client = new() { DefaultValue = DefaultValue.Mock };
        client
            .Setup(o => o.Manifests.GetAsync("library/image", "latest", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ManifestInfo(
                "application/test",
                "sha256:manifest",
                new DockerManifest
                {
                    Layers = [new ManifestLayer { Digest = digest }]
                }));
        client
            .Setup(o => o.Blobs.GetAsync("library/image", digest, It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => CreateLayer(($"../escaped-{id}.txt", "escaped")));
        Mock<IDockerRegistryClientFactory> factory = new();
        factory.Setup(o => o.GetClientAsync(null)).ReturnsAsync(client.Object);

        try
        {
            InvalidDataException exception = await Assert.ThrowsAsync<InvalidDataException>(
                () => ImageHelper.SaveImageLayersToDiskAsync(
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
        string layerCache = Path.Combine(DredgeState.DredgeTempPath, "layers", id);
        Mock<IDockerRegistryClient> client = CreateSingleLayerClient(
            digest,
            () => CreateSymbolicLinkLayer("link", "../outside"));
        Mock<IDockerRegistryClientFactory> factory = new();
        factory.Setup(o => o.GetClientAsync(null)).ReturnsAsync(client.Object);

        try
        {
            await Assert.ThrowsAsync<InvalidDataException>(
                () => ImageHelper.SaveImageLayersToDiskAsync(
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
        string layerCache = Path.Combine(DredgeState.DredgeTempPath, "layers", id);
        string escapedPath = Path.Combine(DredgeState.DredgeTempPath, "layers", $"escaped-{id}.txt");
        Mock<IDockerRegistryClient> client = CreateSingleLayerClient(
            digest,
            () => CreateSymlinkChainLayer(id));
        Mock<IDockerRegistryClientFactory> factory = new();
        factory.Setup(o => o.GetClientAsync(null)).ReturnsAsync(client.Object);

        try
        {
            await Assert.ThrowsAsync<InvalidDataException>(
                () => ImageHelper.SaveImageLayersToDiskAsync(
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
        string layerCache = Path.Combine(DredgeState.DredgeTempPath, "layers", id);
        Mock<IDockerRegistryClient> client = CreateSingleLayerClient(
            digest,
            () => CreateLayer((whiteoutName, string.Empty)));
        Mock<IDockerRegistryClientFactory> factory = new();
        factory.Setup(o => o.GetClientAsync(null)).ReturnsAsync(client.Object);

        try
        {
            InvalidDataException exception = await Assert.ThrowsAsync<InvalidDataException>(
                () => ImageHelper.SaveImageLayersToDiskAsync(
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
        Mock<IDockerRegistryClient> client = new() { DefaultValue = DefaultValue.Mock };
        client
            .Setup(o => o.Manifests.GetAsync("library/image", "latest", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ManifestInfo(
                "application/test",
                "sha256:manifest",
                new DockerManifest
                {
                    Layers = [new ManifestLayer { Digest = digest }]
                }));
        client
            .Setup(o => o.Blobs.GetAsync("library/image", digest, It.IsAny<CancellationToken>()))
            .ReturnsAsync(createLayer);
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
