using System.CommandLine;
using System.CommandLine.Parsing;
using System.Formats.Tar;
using System.IO.Compression;
using System.Text;
using Spectre.Console;
using Valleysoft.DockerRegistryClient.Models.Manifests.Docker;
using Valleysoft.Dredge.Commands.Image;
using Valleysoft.Dredge.Explorer;
using Valleysoft.Dredge.Explorer.Tui;
using static Valleysoft.Dredge.Tests.WindowsImageTestData;

namespace Valleysoft.Dredge.Tests;

public sealed class WindowsReviewRegressionTests : IAsyncDisposable
{
    private readonly LayerCacheTestContext cache = new();
    private CancellationToken Token => TestContext.Current.CancellationToken;
    public ValueTask DisposeAsync() => cache.DisposeAsync();

    private Task<ImageFileSystem> Open(Mock<IDockerRegistryClient> client, int? layer = null,
        string? contentPath = null, string? extractionPath = null) =>
        ImageFileSystem.CreateAsync(client.Object, Name, new(), Token, cache.Store,
            contentPath: contentPath, extractionPath: extractionPath,
            requireLayerIndexes: true, maxLayerIndex: layer);

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void Utf8DecodingRemovesOnlyTheEncodingSignature(int contentCharacters)
    {
        string content = new('\uFEFF', contentCharacters);
        byte[] bytes = Encoding.UTF8.GetBytes("\uFEFF" + content + "value");
        var decoded = ExplorerHost.Decode(bytes, false);
        Assert.Equal([content + "value"], decoded.Lines);
        Assert.Null(decoded.Message);
        Assert.Equal(bytes.Length, decoded.Bytes);
    }

    [Theory]
    [InlineData("/")]
    [InlineData(@"\")]
    [InlineData(@"C:\")]
    public async Task RootLinkTargetsResolveWithoutBecomingUncPaths(string target)
    {
        await using ImageFileSystem files = await Open(Client(Layer(
            File("Files/value", "root"), Link("Files/App/root", target))));
        using MemoryStream output = new();
        await files.CopyFileToAsync("App/root/value", output, Token);
        Assert.Equal("root", Encoding.UTF8.GetString(output.ToArray()));
    }

    [Fact]
    public async Task BareDriveRelativeLinkTargetRemainsUnsupported()
    {
        await using ImageFileSystem files = await Open(Client(Layer(
            File("Files/value", "root"), Link("Files/App/root", "C:"))));
        Assert.Equal(ImageFileType.Other, Assert.Single(files.List("App/root", false, false)).Type);
        await Assert.ThrowsAsync<NotSupportedException>(() =>
            files.CopyFileToAsync("App/root", new MemoryStream(), Token));
    }

    [Theory]
    [InlineData("MSWINDOWS.xattr.CaseSensitive", "AQ==")]
    [InlineData("MSWINDOWS.mountpoint", "1")]
    [InlineData("MSWINDOWS.unknown", "1")]
    public async Task RootDirectoryMetadataIsValidated(string key, string value)
    {
        PaxTarEntry root = new(TarEntryType.Directory, "Files/",
            new Dictionary<string, string> { [key] = value });
        await Assert.ThrowsAsync<NotSupportedException>(() =>
            Open(Client(Layer(root, File("Files/value", "value")))));
    }

    [Theory]
    [InlineData("a:secret")]
    [InlineData("C:secret")]
    public async Task RootAlternateStreamsRemainUnsupportedWithoutHidingOtherFiles(string path)
    {
        await using ImageFileSystem files = await Open(Client(Layer(
            File("Files/value", "value"), File("Files/" + path, "stream"))));
        ImageFileSystemEntry stream = Assert.Single(files.List(null, true, false), entry => entry.Path == path);
        Assert.Equal(ImageFileType.Other, stream.Type);
        Assert.Contains("Alternate data streams", stream.Windows!.UnsupportedReason);
        using MemoryStream output = new();
        await files.CopyFileToAsync("value", output, Token);
        Assert.Equal("value", Encoding.UTF8.GetString(output.ToArray()));
    }

    [Fact]
    public async Task GlobalPaxHeadersAreNotFilesystemEntriesAndPreserveOffsets()
    {
        byte[] layer = GlobalLayer(new() { ["comment"] = "metadata" }, File("Files/value", "content"));
        Mock<IDockerRegistryClient> client = Client(layer);
        await using ImageFileSystem files = await Open(client);
        Assert.Equal(["value"], files.List(null, true, false).Select(static entry => entry.Path));
        await cache.EvictBlobsAsync();
        await using ImageFileSystem restored = await Open(client);
        using MemoryStream output = new();
        await restored.CopyFileToAsync("value", output, Token);
        Assert.Equal("content", Encoding.UTF8.GetString(output.ToArray()));
    }

    [Fact]
    public async Task GlobalWindowsMetadataIsInheritedByDirectoryValidation()
    {
        byte[] layer = GlobalLayer(new() { ["MSWINDOWS.unknown"] = "1" },
            new PaxTarEntry(TarEntryType.Directory, "Files/App"));
        NotSupportedException error = await Assert.ThrowsAsync<NotSupportedException>(() => Open(Client(layer)));
        Assert.Contains("MSWINDOWS.unknown", error.Message);
    }

    [Theory]
    [InlineData(false, 33u)]
    [InlineData(true, 32u)]
    public async Task PerEntryWindowsAttributesOverrideGlobalDefaults(bool overrideAttributes, uint expected)
    {
        byte[] layer = GlobalLayer(new() { ["MSWINDOWS.fileattr"] = "33" },
            File("Files/value", "value", overrideAttributes ? null : []));
        await using ImageFileSystem files = await Open(Client(layer));
        Assert.Equal(expected, Assert.Single(files.List("value", false, false)).Windows!.Attributes);
    }

    [Fact]
    public async Task UnsupportedHardLinkSourcesPropagateThroughChainsAndSnapshots()
    {
        Mock<IDockerRegistryClient> client = Client(
            Layer(File("Files/App/source", "unsupported", new() { ["MSWINDOWS.unknown"] = "1" }),
                Link("Files/App/alias", "Files/App/source", hard: true)),
            Layer(Link("Files/App/chain", "Files/App/alias", hard: true),
                File("Files/App/source", "replacement")));
        ExplorerSource source = await ExplorerSource.OpenAsync(client.Object,
            Mock.Of<IDockerRegistryClientFactory>(), Name, new(), null, Token);
        ExplorerSession session = await ExplorerSession.CreateAsync(client.Object, source, cache.Store, null, Token);
        await using ImageFileSystem files = session.Files;
        await session.EnsurePackagesAsync(Token);
        foreach (string path in new[] { "App/alias", "App/chain" })
        {
            ImageFileSystemEntry alias = Assert.Single(files.List(path, false, false));
            Assert.Equal(ImageFileType.Other, alias.Type);
            Assert.Contains("unsupported", alias.Windows!.UnsupportedReason);
            Assert.Equal(ImageFileType.Other, files.Analyze().LiveEntries[path].Type);
            Assert.False(files.Analyze().LiveContents.ContainsKey(path));
            await Assert.ThrowsAsync<NotSupportedException>(() =>
                files.CopyFileToAsync(path, new MemoryStream(), Token));
            string destination = Path.Combine(cache.Root, Path.GetFileName(path));
            await Assert.ThrowsAsync<NotSupportedException>(() => files.ExtractAsync(path, destination, Token));
            Assert.False(System.IO.File.Exists(destination));
        }
        Assert.Empty(ExplorerSession.Compare(session, session).Files);
        await using ImageFileSystem snapshot = files.CreateLayerSnapshot(0, Token);
        Assert.Equal(ImageFileType.Other, Assert.Single(snapshot.List("App/alias", false, false)).Type);
    }

    [Fact]
    public async Task AnalysisAndFilesystemBothRejectUnsupportedParentTraversal()
    {
        byte[] first = Layer(Link("Files/App", @"D:\outside"));
        byte[] second = Layer(File("Files/App/value", "value"));
        LayerChanges before = await ImageLayerScanner.ScanAsync(new MemoryStream(first), new(0, "first"),
            Token, ImageFileSystemSemantics.Windows);
        LayerChanges after = await ImageLayerScanner.ScanAsync(new MemoryStream(second), new(1, "second"),
            Token, ImageFileSystemSemantics.Windows);
        Assert.Throws<NotSupportedException>(() =>
            ImageAnalysis.Analyze([before, after], ImageFileSystemSemantics.Windows));
        await Assert.ThrowsAsync<NotSupportedException>(() => Open(Client(first, second)));
    }

    [Fact]
    public async Task SelectedSnapshotDoesNotValidateUnselectedLaterCodecs()
    {
        Mock<IDockerRegistryClient> client = Client(
            Layer(File("Files/value", "first")), Layer(File("Files/value", "later")));
        var manifest = await client.Object.Manifests.GetAsync(Name.Repo, Name.Tag!, Token);
        ((DockerManifest)manifest.Manifest).Layers[1].MediaType = "application/vnd.oci.image.layer.v1.tar+zstd";
        await using ImageFileSystem files = await Open(client, layer: 0);
        using MemoryStream output = new();
        await files.CopyFileToAsync("value", output, Token);
        Assert.Equal("first", Encoding.UTF8.GetString(output.ToArray()));
        await Assert.ThrowsAsync<NotSupportedException>(() => Open(client));
    }

    [Theory]
    [InlineData("C:relative", false)]
    [InlineData(@"D:\value", false)]
    [InlineData("C:relative", true)]
    [InlineData(@"D:\value", true)]
    public async Task InvalidUserPathsAreRejectedBeforeAnyLayerDownload(string path, bool extracting)
    {
        Mock<IDockerRegistryClient> client = Client(Layer(File("Files/value", "value")));
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            Open(client, contentPath: extracting ? null : path, extractionPath: extracting ? path : null));
        client.Verify(item => item.Blobs.GetRangeAsync(Name.Repo, It.IsAny<string>(), 0,
            It.IsAny<long?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task UnknownWindowsTombstonesDoNotInventUnixOwnership()
    {
        Mock<IDockerRegistryClient> client = Client(Layer(File("Files/App/.wh.absent", "")));
        Mock<IDockerRegistryClientFactory> factory = new();
        factory.Setup(item => item.GetClientAsync(Name.Registry, It.IsAny<CancellationToken>()))
            .ReturnsAsync(client.Object);
        StringWriter writer = new();
        IAnsiConsole console = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Ansi = AnsiSupport.No, Out = new AnsiConsoleOutput(writer)
        });
        LsCommand command = new(factory.Object, console, cache.Paths);
        Assert.Equal(0, await command.Parse([Name.ToString(), "--recursive", "--show-deleted", "--long"])
            .InvokeAsync(new(), Token));
        Assert.Contains("win:     ? ? ? 0", writer.ToString());
        Assert.DoesNotContain("?---------", writer.ToString());
    }

    [Fact]
    public async Task ExcludedRuntimePayloadsDoNotAffectContentHashesOrOffsets()
    {
        byte[] layer = Layer(File("UtilityVM/Files/runtime", new string('x', 1024 * 1024)),
            File("Hives/Software", "registry"), File("Files/value", "value"));
        await using ImageFileSystem files = await Open(Client(layer));
        Assert.Equal(["value"], files.List(null, true, false).Select(static entry => entry.Path));
        Assert.Equal(5, files.Analyze().FileBytes);
        using MemoryStream output = new();
        await files.CopyFileToAsync("value", output, Token);
        Assert.Equal("value", Encoding.UTF8.GetString(output.ToArray()));
    }

    private static byte[] GlobalLayer(Dictionary<string, string> attributes, PaxTarEntry entry)
    {
        using MemoryStream output = new();
        using (GZipStream gzip = new(output, CompressionMode.Compress, leaveOpen: true))
        using (TarWriter writer = new(gzip, TarEntryFormat.Pax, leaveOpen: true))
        {
            writer.WriteEntry(new PaxGlobalExtendedAttributesTarEntry(attributes));
            writer.WriteEntry(entry);
            entry.DataStream?.Dispose();
        }
        return output.ToArray();
    }
}
