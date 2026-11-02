using System.Formats.Tar;
using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;
using Valleysoft.Dredge.Explorer;
using static Valleysoft.Dredge.Tests.WindowsImageTestData;

namespace Valleysoft.Dredge.Tests;

[Collection(ExplorerUiCollection.Name)]
public sealed class WindowsReviewRoundTwelveTests : IAsyncDisposable
{
    private readonly LayerCacheTestContext cache = new();
    private CancellationToken Token => TestContext.Current.CancellationToken;
    public ValueTask DisposeAsync() => cache.DisposeAsync();

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FinalSigmaAndCapitalSigmaHaveIndependentContentAndWhiteouts(bool separateLayers)
    {
        byte[] first = Layer(File("Files/\u03a3/value", "CAPITAL"));
        byte[] second = Layer(File("Files/\u03c2/value", "FINAL"));
        Mock<IDockerRegistryClient> client = separateLayers ? Client(first, second) :
            Client(Layer(File("Files/\u03a3/value", "CAPITAL"), File("Files/\u03c2/value", "FINAL")));
        await using ImageFileSystem files = await ImageFileSystem.CreateAsync(
            client.Object, Name, new(), Token, cache.Store, requireLayerIndexes: true);
        await Check(files);
        await using ImageFileSystem restored = await ImageFileSystem.CreateAsync(client.Object, Name, new(), Token, cache.Store);
        await Check(restored);
        Mock<IDockerRegistryClient> deletedClient = Client(
            Layer(File("Files/\u03a3/value", "CAPITAL"), File("Files/\u03c2/value", "FINAL")),
            Layer(File("Files/.wh.\u03c3", "")));
        await using ImageFileSystem deleted = await ImageFileSystem.CreateAsync(
            deletedClient.Object, Name, new(), Token, cache.Store, requireLayerIndexes: true);
        Assert.Contains("\u03c2/value", deleted.Analyze().LiveContents.Keys);
        Assert.DoesNotContain("\u03a3/value", deleted.Analyze().LiveContents.Keys);

        async Task Check(ImageFileSystem view)
        {
            using MemoryStream capital = new(), final = new();
            await view.CopyFileToAsync("\u03c3/value", capital, Token);
            await view.CopyFileToAsync("\u03c2/value", final, Token);
            Assert.Equal("CAPITAL"u8.ToArray(), capital.ToArray());
            Assert.Equal("FINAL"u8.ToArray(), final.ToArray());
            Assert.Equal(2, view.Analyze().LiveContents.Count);
        }
    }

    [Theory]
    [InlineData("\u03a3", "\u03c3", true)]
    [InlineData("\u03a3", "\u03c2", false)]
    [InlineData("S", "\u017f", false)]
    [InlineData("\u00c9", "\u00e9", true)]
    public void NativeCodeUnitIdentityIsConsistent(string first, string second, bool equal)
    {
        StringComparer comparer = WindowsImagePathComparer.Instance;
        Assert.Equal(equal, comparer.Equals(first, second));
        Assert.Equal(equal, comparer.Compare(first, second) == 0);
        Assert.Equal(equal, WindowsImagePathComparer.StartsWith(first + "/value", second + "/"));
        Assert.Equal(equal ? 1 : 2, new HashSet<string>([first, second], comparer).Count);
        if (equal) Assert.Equal(comparer.GetHashCode(first), comparer.GetHashCode(second));
    }

    [Fact]
    public void FixedUpcaseTablePreservesEveryCapturedCodeUnit()
    {
        char[] table = Assert.IsType<char[]>(typeof(WindowsImagePathComparer)
            .GetField("upcase", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null));
        Assert.Equal(65536, table.Length);
        byte[] bytes = new byte[table.Length * 2];
        for (int i = 0; i < table.Length; i++)
        {
            Assert.Equal(table[i], table[table[i]]);
            bytes[2 * i] = (byte)(table[i] & 255);
            bytes[2 * i + 1] = (byte)(table[i] >> 8);
        }
        Assert.Equal("41c26bc7a12bdaeb26025c93118697c7e3ef81ee048b00fe5cce2a472e0e0742",
            Convert.ToHexStringLower(SHA256.HashData(bytes)));
    }

    [Fact]
    public async Task CachedGlobalsRetainEntrySpecificValidationAndLocalRemoval()
    {
        using MemoryStream output = new();
        using (GZipStream gzip = new(output, CompressionMode.Compress, leaveOpen: true))
        using (TarWriter tar = new(gzip, TarEntryFormat.Pax, leaveOpen: true))
        {
            tar.WriteEntry(new PaxGlobalExtendedAttributesTarEntry(new Dictionary<string, string>
                { ["MSWINDOWS.fileattr"] = "32", ["MSWINDOWS.xattr.example"] = "AQ==" }));
            tar.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, "Files/file"));
            tar.WriteEntry(new PaxTarEntry(TarEntryType.Directory, "Files/allowed",
                new Dictionary<string, string> { ["MSWINDOWS.xattr.example"] = "" }));
            tar.WriteEntry(new PaxTarEntry(TarEntryType.Directory, "Files/rejected"));
        }
        NotSupportedException error = await Assert.ThrowsAsync<NotSupportedException>(() =>
            ImageLayerScanner.ScanAsync(new MemoryStream(output.ToArray()), new(0, "layer"), Token,
                ImageFileSystemSemantics.Windows));
        Assert.Contains("rejected", error.Message);
        Assert.Contains("Directory extended attributes", error.Message);
    }

    [Fact]
    public async Task CachedGlobalsRetainMountpointRepresentationChecks()
    {
        using MemoryStream output = new();
        using (GZipStream gzip = new(output, CompressionMode.Compress, leaveOpen: true))
        using (TarWriter tar = new(gzip, TarEntryFormat.Pax, leaveOpen: true))
        {
            tar.WriteEntry(new PaxGlobalExtendedAttributesTarEntry(new Dictionary<string, string>
                { ["MSWINDOWS.mountpoint"] = "1" }));
            tar.WriteEntry(Link("Files/junction", "C:/target"));
            tar.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, "Files/unsupported"));
        }
        LayerChanges changes = await ImageLayerScanner.ScanAsync(new MemoryStream(output.ToArray()),
            new(0, "layer"), Token, ImageFileSystemSemantics.Windows);
        Assert.Null(changes.Entries[0].Windows!.UnsupportedReason);
        Assert.Equal(ImageFileType.SymbolicLink, changes.Entries[0].Type);
        Assert.Equal(ImageFileType.Other, changes.Entries[1].Type);
        Assert.Contains("symbolic-link tar representation", changes.Entries[1].Windows!.UnsupportedReason);
    }

    [Fact]
    public async Task UnchangedGlobalMetadataHasBoundedPerEntryAllocation()
    {
        Dictionary<string, string> global = new() { ["MSWINDOWS.fileattr"] = "32" };
        for (int i = 0; i < 4096; i++) global[$"MSWINDOWS.xattr.key{i}"] = "AQ==";
        using MemoryStream output = new();
        using (GZipStream gzip = new(output, CompressionMode.Compress, leaveOpen: true))
        using (TarWriter tar = new(gzip, TarEntryFormat.Pax, leaveOpen: true))
        {
            tar.WriteEntry(new PaxGlobalExtendedAttributesTarEntry(global));
            for (int i = 0; i < 2000; i++)
            {
                tar.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, $"Files/file{i}"));
            }
        }
        byte[] bytes = output.ToArray();
        await ImageLayerScanner.ScanAsync(new MemoryStream(bytes), new(0, "warmup"), Token, ImageFileSystemSemantics.Windows);
        long before = GC.GetTotalAllocatedBytes(precise: true);
        LayerChanges changes = await ImageLayerScanner.ScanAsync(new MemoryStream(bytes),
            new(0, "layer"), Token, ImageFileSystemSemantics.Windows);
        long allocated = GC.GetTotalAllocatedBytes(precise: true) - before;
        Assert.Equal(2000, changes.Entries.Count);
        Assert.True(allocated < 30_000_000, $"Inherited metadata allocated {allocated:N0} bytes.");
        Assert.All(changes.Entries, entry => Assert.Null(entry.Windows!.UnsupportedReason));
        Assert.All(changes.Entries, entry => Assert.Same(changes.Entries[0].Windows!.Fingerprint, entry.Windows!.Fingerprint));
    }

    [Fact]
    public void TreeNodesDoNotStoreImageComparers()
    {
        Assert.Null(typeof(Node).GetProperty("PathComparer", BindingFlags.Instance | BindingFlags.Public));
        ScannedEntry entry = new("App/Value", ImageFileType.File, 0, 0, 0, 1, DateTime.UnixEpoch, null, 0, 0, 0, "hash");
        ExplorerImage windows = new(Name.ToString(), "windows/amd64", "digest", ["layer"], [1], null);
        ExplorerImage linux = new(Name.ToString(), "linux/amd64", "digest", ["layer"], [1], null);
        windows.SetIndexed(0, new([entry], [], []));
        linux.SetIndexed(0, new([entry], [], []));
        Assert.NotNull(windows.Find(windows.LayerTree(0), "app/value"));
        Assert.Null(linux.Find(linux.LayerTree(0), "app/value"));
    }
}
