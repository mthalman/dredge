using System.CommandLine;
using System.CommandLine.Parsing;
using System.Formats.Tar;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Spectre.Console;
using Valleysoft.DockerRegistryClient.Models.Manifests;
using Valleysoft.DockerRegistryClient.Models.Manifests.Docker;
using Valleysoft.Dredge.Commands;
using Valleysoft.Dredge.Commands.Image;
using Valleysoft.Dredge.Explorer;
using Valleysoft.Dredge.Explorer.Tui;
using static Valleysoft.Dredge.Tests.WindowsImageTestData;

namespace Valleysoft.Dredge.Tests;

public sealed class WindowsImageFileSystemTests : IAsyncDisposable
{
    private readonly LayerCacheTestContext cache = new();
    private CancellationToken Token => TestContext.Current.CancellationToken;

    public ValueTask DisposeAsync() => cache.DisposeAsync();

    private Task<ImageFileSystem> Open(Mock<IDockerRegistryClient> client, int? layer = null) =>
        ImageFileSystem.CreateAsync(client.Object, Name, new(), Token, cache.Store,
            requireLayerIndexes: true, maxLayerIndex: layer);

    [Theory]
    [InlineData(@"C:\WINDOWS\System32\config.txt")]
    [InlineData("/windows/system32/CONFIG.TXT")]
    [InlineData(@"Windows\System32\config.txt")]
    public async Task ResolvesWindowsPathsAndPreservesProvenance(string path)
    {
        Mock<IDockerRegistryClient> client = Client(
            Layer(File("Files/Windows/System32/config.txt", "old"), File("Files/Windows/removed.txt", "removed")),
            Layer(File("Files/windows/system32/CONFIG.txt", "new"), File("Files/WINDOWS/.wh.removed.txt", "")));
        await using ImageFileSystem files = await Open(client);
        using MemoryStream output = new();
        await files.CopyFileToAsync(path, output, Token);
        Assert.Equal("new", Encoding.UTF8.GetString(output.ToArray()));
        ImageFileSystemEntry entry = Assert.Single(files.List(path, false, false));
        Assert.Equal(0, entry.IntroducedLayer.Index);
        Assert.Equal(1, entry.ModifiedLayer!.Index);
        Assert.NotNull(entry.Windows);
        Assert.DoesNotContain(files.List(null, true, false), item => item.Path.EndsWith("removed.txt", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(1, Assert.Single(files.List(null, true, true), item => item.IsDeleted).DeletedLayer!.Index);
        Assert.Equal(LayerChangeKind.Modified, Assert.Single(files.Analyze().Layers[1].Changes,
            item => item.Kind == LayerChangeKind.Modified).Kind);
        Assert.Equal(10, files.Analyze().HiddenBytes);

        await using ImageFileSystem snapshot = files.CreateLayerSnapshot(0, Token);
        using MemoryStream previous = new();
        await snapshot.CopyFileToAsync(path, previous, Token);
        Assert.Equal("old", Encoding.UTF8.GetString(previous.ToArray()));
    }

    [Fact]
    public async Task WhiteoutsDeleteSubtreesAndRespectSameLayerRecreation()
    {
        Mock<IDockerRegistryClient> client = Client(
            Layer(File("Files/App/Dir/old", "old"), File("Files/App/Dir/keep", "keep")),
            Layer(File("Files/app/.wh.DIR", ""), File("Files/App/Dir/new", "new")));
        await using ImageFileSystem files = await Open(client);
        Assert.Equal(["App", "App/Dir", "App/Dir/new"], files.List(null, true, false).Select(static entry => entry.Path));
        IReadOnlyList<LayerFileChange> changes = files.Analyze().Layers[1].Changes;
        Assert.Equal(2, changes.Count(static change => change.Kind == LayerChangeKind.Deleted && change.Type == ImageFileType.File));
        Assert.Equal("app/DIR", Assert.Single(changes,
            static change => change.Kind == LayerChangeKind.Deleted && change.Type == ImageFileType.Directory).Path);
        Assert.Equal("App/Dir", Assert.Single(changes,
            static change => change.Kind == LayerChangeKind.Added && change.Type == ImageFileType.Directory).Path);
    }

    [Fact]
    public async Task OpaqueWhiteoutRemovesOnlyLowerChildren()
    {
        await using ImageFileSystem files = await Open(Client(
            Layer(File("Files/App/old", "old"), File("Files/Elsewhere/keep", "keep")),
            Layer(File("Files/app/.wh..wh..opq", ""), File("Files/App/new", "new"))));
        Assert.DoesNotContain(files.List(null, true, false), static entry => entry.Path == "App/old");
        Assert.Contains(files.List(null, true, false), static entry => entry.Path == "App/new");
        Assert.Contains(files.List(null, true, false), static entry => entry.Path == "Elsewhere/keep");
    }

    [Fact]
    public async Task HardLinksCaptureContentBeforeReplacementAndExportAsFiles()
    {
        await using ImageFileSystem files = await Open(Client(
            Layer(File("Files/App/original", "before"), Link("Files/App/saved", "Files/app/ORIGINAL", hard: true)),
            Layer(File("Files/App/original", "after"))));
        using MemoryStream output = new();
        await files.CopyFileToAsync(@"C:\app\SAVED", output, Token);
        Assert.Equal("before", Encoding.UTF8.GetString(output.ToArray()));
        string destination = Path.Combine(cache.Root, "export");
        await files.ExtractAsync("app", destination, Token);
        Assert.Equal("before", await System.IO.File.ReadAllTextAsync(Path.Combine(destination, "saved"), Token));
        Assert.Equal("after", await System.IO.File.ReadAllTextAsync(Path.Combine(destination, "original"), Token));
        Assert.Equal(0, files.Analyze().HiddenBytes);
    }

    [Theory]
    [InlineData("")]
    [InlineData("APP")]
    public async Task ExportsMixedParentSpellingsThroughOneDirectory(string requested)
    {
        await using ImageFileSystem files = await Open(Client(
            Layer(File("Files/App/original", "old")),
            Layer(File("Files/app/Child/value", "value"), File("Files/APP/child/other", "other"))));
        string destination = Path.Combine(cache.Root, "output");
        await files.ExtractAsync(requested, destination, Token);
        string root = requested.Length == 0 ? Path.Combine(destination, "App") : destination;
        Assert.Equal("value", await System.IO.File.ReadAllTextAsync(Path.Combine(root, "Child", "value"), Token));
        Assert.Equal("other", await System.IO.File.ReadAllTextAsync(Path.Combine(root, "Child", "other"), Token));
        Assert.Equal("Child", Path.GetFileName(Assert.Single(Directory.EnumerateDirectories(root))));
    }

    [Theory]
    [InlineData(0x1412u, 0x606u)]
    [InlineData(0x1410u, 0x1620u)]
    [InlineData(0x1414u, 0x400u)]
    public async Task ReparseAttributesOnOrdinaryTarEntriesPreserveDirectoryAndFileContents(
        uint directoryAttributes, uint fileAttributes)
    {
        PaxTarEntry directory = new(TarEntryType.Directory, "Files/ProgramData",
            new Dictionary<string, string>
            {
                ["MSWINDOWS.fileattr"] = directoryAttributes.ToString(System.Globalization.CultureInfo.InvariantCulture)
            });
        await using ImageFileSystem files = await Open(Client(
            Layer(File("Files/ProgramData/retained", "base")),
            Layer(directory, File("Files/ProgramData/changed", "updated",
                new() { ["MSWINDOWS.fileattr"] = fileAttributes.ToString(System.Globalization.CultureInfo.InvariantCulture) }))));
        ImageFileSystemEntry parent = Assert.Single(files.List(null, true, false),
            static entry => entry.Path == "ProgramData");
        Assert.Equal(ImageFileType.Directory, parent.Type);
        Assert.Equal(directoryAttributes, parent.Windows!.Attributes);
        ImageFileSystemEntry changed = Assert.Single(files.List("ProgramData/changed", false, false));
        Assert.Equal(ImageFileType.File, changed.Type);
        Assert.Equal(fileAttributes, changed.Windows!.Attributes);
        Assert.Null(changed.Windows.UnsupportedReason);
        Assert.Contains(files.Diagnostics, static diagnostic => diagnostic.Message.Contains("reparse attributes"));
        Assert.Contains(files.Analyze().LiveContents.Keys, static path => path == "ProgramData/changed");
        string destination = Path.Combine(cache.Root, "output");
        await files.ExtractAsync("ProgramData", destination, Token);
        Assert.Equal("base", await System.IO.File.ReadAllTextAsync(Path.Combine(destination, "retained"), Token));
        Assert.Equal("updated", await System.IO.File.ReadAllTextAsync(Path.Combine(destination, "changed"), Token));
    }

    [Theory]
    [InlineData("data.txt", false)]
    [InlineData(@"C:\App\data.txt", false)]
    [InlineData(@"C:\App", true)]
    public async Task ResolvesSupportedSymlinksAndJunctionsButRefusesHostReparseExtraction(string target, bool junction)
    {
        PaxTarEntry link = Link("Files/App/alias", target, mountPoint: junction);
        await using ImageFileSystem files = await Open(Client(Layer(File("Files/App/data.txt", "data"), link)));
        using MemoryStream output = new();
        await files.CopyFileToAsync(junction ? "app/alias/data.txt" : "app/alias", output, Token);
        Assert.Equal("data", Encoding.UTF8.GetString(output.ToArray()));
        string destination = Path.Combine(cache.Root, "export");
        await Assert.ThrowsAsync<NotSupportedException>(() => files.ExtractAsync("App", destination, Token));
        Assert.False(Directory.Exists(destination));
    }

    [Theory]
    [InlineData(@"\\server\share\file")]
    [InlineData(@"\\?\C:\file")]
    [InlineData(@"D:\file")]
    [InlineData("../../outside")]
    public async Task UnsupportedLinkDoesNotRevealTheLowerFile(string target)
    {
        await using ImageFileSystem files = await Open(Client(
            Layer(File("Files/App/current", "must not leak")),
            Layer(Link("Files/App/current", target))));
        Assert.Equal(ImageFileType.Other, Assert.Single(files.List("App/current", false, false)).Type);
        await Assert.ThrowsAsync<NotSupportedException>(() => files.CopyFileToAsync("App/current", new MemoryStream(), Token));
        Assert.Contains(files.Diagnostics, static diagnostic => diagnostic.Path == "App/current");
    }

    [Theory]
    [InlineData("Files/App/data.txt:secret", "MSWINDOWS.fileattr", "32")]
    [InlineData("Files/App/data.txt", "MSWINDOWS.mountpoint", "1")]
    [InlineData("Files/App/data.txt", "MSWINDOWS.unknown", "1")]
    public async Task UnsupportedEntriesAreVisibleAndBlockExtractionBeforeWriting(string path, string key, string value)
    {
        await using ImageFileSystem files = await Open(Client(Layer(
            File("Files/App/normal", "normal"), File(path, "unsupported", new() { [key] = value }))));
        ImageFileSystemEntry unsupported = Assert.Single(files.List(null, true, false),
            static entry => entry.Type == ImageFileType.Other);
        Assert.NotNull(unsupported.Windows!.UnsupportedReason);
        string destination = Path.Combine(cache.Root, "new-parent", "output");
        await Assert.ThrowsAsync<NotSupportedException>(() => files.ExtractAsync("App", destination, Token));
        Assert.False(Directory.Exists(Path.GetDirectoryName(destination)));
    }

    [Fact]
    public async Task ExcludesRuntimePayloadsWithVisibleDiagnostics()
    {
        await using ImageFileSystem files = await Open(Client(Layer(
            File("Files/App/data", "data"), File("Hives/Software", "hive"), File("UtilityVM/Files/kernel", "vm"))));
        Assert.Equal(["App", "App/data"], files.List(null, true, false).Select(static entry => entry.Path));
        Assert.Contains(files.Diagnostics, static diagnostic => diagnostic.Message.Contains("excluded"));
    }

    [Fact]
    public async Task RejectsAmbiguousCaseCollisions()
    {
        await Assert.ThrowsAsync<NotSupportedException>(() => Open(Client(Layer(
            File("Files/App/value", "one"), File("Files/App/VALUE", "two")))));
    }

    [Theory]
    [InlineData("MSWINDOWS.xattr.CaseSensitive", "AQ==")]
    [InlineData("MSWINDOWS.mountpoint", "1")]
    [InlineData("MSWINDOWS.unknown", "1")]
    public async Task RejectsUninterpretedDirectorySemantics(string key, string value)
    {
        PaxTarEntry directory = new(TarEntryType.Directory, "Files/App",
            new Dictionary<string, string> { [key] = value, ["MSWINDOWS.fileattr"] = "5136" });
        await Assert.ThrowsAsync<NotSupportedException>(() => Open(Client(Layer(directory, File("Files/App/value", "value")))));
    }

    [Theory]
    [InlineData("../outside")]
    [InlineData(@"C:relative")]
    [InlineData(@"D:\App")]
    [InlineData(@"\\server\share")]
    [InlineData(@"\\?\C:\App")]
    [InlineData("App/name.")]
    public void RejectsUnsupportedUserPaths(string path) =>
        Assert.ThrowsAny<Exception>(() => ImageFileSystemSemantics.Windows.NormalizeRequested(path));

    [Fact]
    public async Task SeparatesWindowsAndLinuxIndexesForTheSameBlob()
    {
        byte[] layer = Layer(File("Files/App/value", "data"));
        Mock<IDockerRegistryClient> client = Client(layer);
        string digest = LayerCacheTestContext.Digest(layer);
        StoredLayerIndex linux = await cache.Store.GetIndexAsync(client.Object, Name, new(0, digest), layer.Length, Token);
        Assert.Equal("Files/App/value", Assert.Single(linux.Changes.Entries).Path);
        Assert.Empty(linux.Changes.Diagnostics);
        await using ImageFileSystem files = await Open(client);
        Assert.Equal("App/value", Assert.Single(files.List(null, true, false), static entry => entry.Type == ImageFileType.File).Path);
        client.Verify(item => item.Blobs.GetRangeAsync(Name.Repo, digest, 0, null, It.IsAny<CancellationToken>()), Times.Once);
        await using ImageFileSystem again = await Open(client);
        Assert.NotEmpty(again.Diagnostics);
        Assert.Equal(files.List(null, true, false), again.List(null, true, false));
        Assert.Equal(2, Directory.GetFiles(Path.Combine(cache.Paths.CachePath, "layer-store", "data"), "*.index").Length);
    }

    [Theory]
    [InlineData("windows-v1.index")]
    [InlineData("windows-v2.index")]
    [InlineData("windows-v3.index")]
    [InlineData("windows-v4.index")]
    [InlineData("windows-v5.index")]
    [InlineData("windows-v6.index")]
    [InlineData("windows-v7.index")]
    [InlineData("windows-v8.index")]
    [InlineData("windows-v9.index")]
    [InlineData("windows-v10.index")]
    [InlineData("windows-v11.index")]
    public async Task RebuildsOldWindowsIndexWithoutDownloadingTheVerifiedBlobAgain(string previousSuffix)
    {
        byte[] layer = Layer(File("Files/App/value", "data", new() { ["MSWINDOWS.fileattr"] = "1024" }));
        Mock<IDockerRegistryClient> client = Client(layer);
        string digest = LayerCacheTestContext.Digest(layer);
        StoredLayerIndex index = await cache.Store.GetIndexAsync(client.Object, Name, new(0, digest),
            layer.Length, Token, semantics: ImageFileSystemSemantics.Windows);
        ScannedEntry entry = Assert.Single(index.Changes.Entries);
        StoredLayerIndex stale = index with
        {
            Changes = index.Changes with
            {
                Entries = [entry with
                {
                    Type = ImageFileType.Other,
                    Windows = entry.Windows! with { UnsupportedReason = "Unsupported Windows reparse-point representation." }
                }]
            }
        };
        await cache.Store.WriteMetadataAsync(digest, ImageFileSystemSemantics.Windows.IndexKind, stale, Token);
        string current = Assert.Single(Directory.GetFiles(Path.GetDirectoryName(cache.Store.GetBlobPath(digest))!, "*.index"));
        string previous = Path.ChangeExtension(cache.Store.GetBlobPath(digest), previousSuffix);
        System.IO.File.Move(current, previous);

        await using ImageFileSystem files = await Open(client);
        Assert.Equal(ImageFileType.File, Assert.Single(files.List("App/value", false, false)).Type);
        using MemoryStream content = new();
        await files.CopyFileToAsync("App/value", content, Token);
        Assert.Equal("data", Encoding.UTF8.GetString(content.ToArray()));
        Assert.True(System.IO.File.Exists(previous));
        client.Verify(item => item.Blobs.GetRangeAsync(Name.Repo, digest, 0, null,
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ReopensWindowsContentAfterBlobEviction()
    {
        byte[] layer = Layer(File("Files/App/value", "data"));
        Mock<IDockerRegistryClient> client = Client(layer);
        await using (ImageFileSystem initial = await Open(client))
        {
            Assert.NotEmpty(initial.List(null, true, false));
        }
        await cache.EvictBlobsAsync();
        await using ImageFileSystem files = await Open(client);
        using MemoryStream output = new();
        await files.CopyFileToAsync("App/value", output, Token);
        Assert.Equal("data", Encoding.UTF8.GetString(output.ToArray()));
    }

    [Fact]
    public async Task RestoresEffectiveViewDiagnosticsAndReadsAnHonoredRange()
    {
        PaxTarEntry padding = File("Files/padding", "");
        byte[] random = new byte[256 * 1024];
        new Random(42).NextBytes(random);
        padding.DataStream = new MemoryStream(random);
        byte[] layer = Layer(File("Files/App/Value", "data"), padding, File("Hives/Software", "hive"));
        Mock<IDockerRegistryClient> client = Client(layer);
        IReadOnlyList<LayerDiagnostic> diagnostics;
        await using (ImageFileSystem initial = await Open(client))
        {
            diagnostics = initial.Diagnostics;
        }
        await cache.EvictBlobsAsync();
        await using ImageFileSystem restored = await ImageFileSystem.CreateAsync(
            client.Object, Name, new(), Token, cache.Store);
        Assert.Equal(diagnostics, restored.Diagnostics);
        Assert.Equal("App/Value", Assert.Single(restored.List("app/value", false, false)).Path);
        string digest = LayerCacheTestContext.Digest(layer);
        long length = 0;
        client.Setup(item => item.Blobs.GetRangeAsync(Name.Repo, digest, 0,
            It.Is<long?>(value => value.HasValue), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string _, string _, long _, long? count, CancellationToken _) =>
            {
                length = count!.Value;
                return new Valleysoft.DockerRegistryClient.BlobDownloadResult(
                    new MemoryStream(layer[..checked((int)length)]), true, 0, length - 1, layer.Length);
            });
        using MemoryStream output = new();
        await restored.CopyFileToAsync(@"C:\APP\value", output, Token);
        Assert.Equal("data", Encoding.UTF8.GetString(output.ToArray()));
        Assert.InRange(length, 1, layer.Length / 2);
        Assert.False(System.IO.File.Exists(cache.Store.GetBlobPath(digest)));
        client.Verify(item => item.Blobs.GetRangeAsync(Name.Repo, digest, 0, null,
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task MissingBaseLayerCannotProduceACompleteFilesystem()
    {
        byte[] first = Layer(File("Files/App/first", "first"));
        Mock<IDockerRegistryClient> client = Client(first, Layer(File("Files/App/last", "last")));
        client.Setup(item => item.Blobs.GetRangeAsync(Name.Repo, LayerCacheTestContext.Digest(first),
            0, It.IsAny<long?>(), It.IsAny<CancellationToken>())).ThrowsAsync(new IOException("Inaccessible base layer."));
        IOException error = await Assert.ThrowsAsync<IOException>(() => Open(client));
        Assert.Contains("Inaccessible base layer", error.Message);
    }

    [Fact]
    public async Task RejectsUnsupportedMediaTypesBeforeDownloadingLayers()
    {
        Mock<IDockerRegistryClient> client = Client(Layer(File("Files/App/value", "data")));
        ManifestInfo info = await client.Object.Manifests.GetAsync(Name.Repo, Name.Tag!, Token);
        ((DockerManifest)info.Manifest).Layers[0].MediaType = "application/vnd.oci.image.layer.v1.tar+zstd";
        await Assert.ThrowsAsync<NotSupportedException>(() => Open(client));
        client.Verify(item => item.Blobs.GetRangeAsync(Name.Repo, It.IsAny<string>(), 0, It.IsAny<long?>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task CommandsSupportWindowsPathsMetadataAndExactContent()
    {
        Mock<IDockerRegistryClient> client = Client(Layer(File("Files/App/Value.txt", "exact\0bytes")));
        Mock<IDockerRegistryClientFactory> factory = new();
        factory.Setup(item => item.GetClientAsync(Name.Registry, It.IsAny<CancellationToken>())).ReturnsAsync(client.Object);
        StringWriter writer = new();
        IAnsiConsole console = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Ansi = AnsiSupport.No, Out = new AnsiConsoleOutput(writer)
        });
        LsCommand ls = new(factory.Object, console, cache.Paths);
        Assert.Equal(0, await ls.Parse([Name.ToString(), @"C:\app", "--output", "json"]).InvokeAsync(new(), Token));
        using JsonDocument document = JsonDocument.Parse(writer.ToString());
        Assert.Equal(32u, Assert.Single(document.RootElement.EnumerateArray()).GetProperty("windows").GetProperty("attributes").GetUInt32());
        writer.GetStringBuilder().Clear();
        Assert.Equal(0, await ls.Parse([Name.ToString(), @"C:\app", "--long"]).InvokeAsync(new(), Token));
        Assert.Contains("win:000020", writer.ToString());
        Assert.DoesNotContain("rwx", writer.ToString());

        using MemoryStream output = new();
        CatCommand cat = new(factory.Object, output, cache.Paths);
        Assert.Equal(0, await cat.Parse([Name.ToString(), @"c:\APP\value.txt"]).InvokeAsync(new(), Token));
        Assert.Equal(Encoding.UTF8.GetBytes("exact\0bytes"), output.ToArray());

        ExtractCommand extract = new(factory.Object, cache.Paths);
        string destination = Path.Combine(cache.Root, "output");
        Assert.Equal(0, await extract.Parse([Name.ToString(), @"C:\app", destination]).InvokeAsync(new(), Token));
        Assert.Equal(output.ToArray(), await System.IO.File.ReadAllBytesAsync(Path.Combine(destination, "Value.txt"), Token));
    }
}

internal static class WindowsImageTestData
{
    public static ImageName Name { get; } = ImageName.Parse("registry.test/windows:current");

    public static PaxTarEntry File(string path, string content, Dictionary<string, string>? attributes = null)
    {
        PaxTarEntry entry = new(TarEntryType.RegularFile, path, attributes ?? new() { ["MSWINDOWS.fileattr"] = "32" })
        {
            DataStream = new MemoryStream(Encoding.UTF8.GetBytes(content)),
            ModificationTime = DateTimeOffset.FromUnixTimeSeconds(1)
        };
        return entry;
    }

    public static PaxTarEntry Link(string path, string target, bool hard = false, bool mountPoint = false) =>
        new(hard ? TarEntryType.HardLink : TarEntryType.SymbolicLink, path,
            mountPoint ? new Dictionary<string, string> { ["MSWINDOWS.mountpoint"] = "1" } : [])
        {
            LinkName = target, ModificationTime = DateTimeOffset.FromUnixTimeSeconds(1)
        };

    public static byte[] Layer(params TarEntry[] entries)
    {
        using MemoryStream output = new();
        using (GZipStream gzip = new(output, CompressionMode.Compress, leaveOpen: true))
        using (TarWriter writer = new(gzip, TarEntryFormat.Pax, leaveOpen: true))
        {
            foreach (TarEntry entry in entries)
            {
                writer.WriteEntry(entry);
                entry.DataStream?.Dispose();
            }
        }
        return output.ToArray();
    }

    public static Mock<IDockerRegistryClient> Client(params byte[][] layers) => Client("windows", "10.0.20348.1", layers);

    public static Mock<IDockerRegistryClient> Client(string os, string version, params byte[][] layers)
    {
        Mock<IDockerRegistryClient> client = new() { DefaultValue = DefaultValue.Mock };
        DockerManifest manifest = new()
        {
            Config = new ManifestConfig { Digest = "sha256:config" },
            Layers = [.. layers.Select(layer => new ManifestLayer
            {
                Digest = LayerCacheTestContext.Digest(layer), Size = layer.Length,
                MediaType = "application/vnd.docker.image.rootfs.diff.tar.gzip"
            })]
        };
        string digest = LayerCacheTestContext.Digest(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(manifest) + os + version));
        client.Setup(item => item.Manifests.GetAsync(Name.Repo, Name.Tag!, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ManifestInfo("application/vnd.docker.distribution.manifest.v2+json", digest, manifest));
        client.Setup(item => item.Blobs.GetAsync(Name.Repo, "sha256:config", It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new MemoryStream(Encoding.UTF8.GetBytes(
                $"{{\"os\":\"{os}\",\"architecture\":\"amd64\",\"os.version\":\"{version}\"}}")));
        foreach (byte[] layer in layers)
        {
            client.Setup(item => item.Blobs.GetRangeAsync(Name.Repo, LayerCacheTestContext.Digest(layer), 0,
                It.IsAny<long?>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => new Valleysoft.DockerRegistryClient.BlobDownloadResult(
                    new MemoryStream(layer), false, null, null, layer.Length));
        }
        return client;
    }
}
