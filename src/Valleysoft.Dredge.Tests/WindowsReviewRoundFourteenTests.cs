using System.Collections;
using System.Formats.Tar;
using System.IO.Compression;
using System.Reflection;
using Valleysoft.DockerRegistryClient.Models.Manifests;
using Valleysoft.Dredge.Explorer;
using Valleysoft.Dredge.Explorer.Tui;
using static Valleysoft.Dredge.Tests.WindowsImageTestData;

namespace Valleysoft.Dredge.Tests;

[Collection(ExplorerUiCollection.Name)]
public sealed class WindowsReviewRoundFourteenTests : IAsyncDisposable
{
    private readonly LayerCacheTestContext cache = new();
    private CancellationToken Token => TestContext.Current.CancellationToken;
    public ValueTask DisposeAsync() => cache.DisposeAsync();

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AlternatingProfilesAndEquivalentKeyOrdersHaveBoundedAllocation(bool reverseOrder)
    {
        Dictionary<string, string> globals = new() { ["MSWINDOWS.fileattr"] = "33" };
        for (int i = 0; i < 4096; i++) globals[$"MSWINDOWS.xattr.key{i}"] = "AQ==";
        using MemoryStream output = new();
        using (GZipStream gzip = new(output, CompressionMode.Compress, leaveOpen: true))
        using (TarWriter tar = new(gzip, TarEntryFormat.Pax, leaveOpen: true))
        {
            tar.WriteEntry(new PaxGlobalExtendedAttributesTarEntry(globals));
            for (int i = 0; i < 2000; i++)
            {
                bool odd = i % 2 != 0;
                string attributes = reverseOrder || !odd ? "32" : "34";
                Dictionary<string, string> local = reverseOrder && odd
                    ? new() { ["MSWINDOWS.rawsd"] = "descriptor", ["MSWINDOWS.fileattr"] = attributes }
                    : new() { ["MSWINDOWS.fileattr"] = attributes, ["MSWINDOWS.rawsd"] = "descriptor" };
                tar.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, $"Files/file{i}", local));
            }
        }
        byte[] bytes = output.ToArray();
        await ImageLayerScanner.ScanAsync(new MemoryStream(bytes), new(0, "warmup"), Token, ImageFileSystemSemantics.Windows);
        long before = GC.GetTotalAllocatedBytes(precise: true);
        LayerChanges changes = await ImageLayerScanner.ScanAsync(new MemoryStream(bytes),
            new(0, "layer"), Token, ImageFileSystemSemantics.Windows);
        long allocated = GC.GetTotalAllocatedBytes(precise: true) - before;
        Assert.True(allocated < 30_000_000, $"Alternating overrides allocated {allocated:N0} bytes.");
        Assert.Equal(2000, changes.Entries.Count);
        for (int i = 0; i < changes.Entries.Count; i++)
        {
            Assert.Equal(reverseOrder || i % 2 == 0 ? 32u : 34u, changes.Entries[i].Windows!.Attributes);
            Assert.Same(changes.Entries[reverseOrder ? 0 : i % 2].Windows!.Fingerprint, changes.Entries[i].Windows!.Fingerprint);
            Assert.Null(changes.Entries[i].Windows!.UnsupportedReason);
        }
    }

    [Fact]
    public void UnknownLocalMetadataRetainsOrderedDiagnosticPrecedence()
    {
        object metadataCache = NewMetadataCache();
        Dictionary<string, string> first = new() { ["MSWINDOWS.unknownA"] = "a", ["MSWINDOWS.unknownB"] = "b" };
        Dictionary<string, string> second = new() { ["MSWINDOWS.unknownB"] = "b", ["MSWINDOWS.unknownA"] = "a" };
        WindowsFileMetadata a = ReadMetadata(metadataCache, first, new());
        WindowsFileMetadata b = ReadMetadata(metadataCache, second, new());
        WindowsFileMetadata again = ReadMetadata(metadataCache, first, new());
        Assert.Equal(a.Fingerprint, b.Fingerprint);
        Assert.Contains("unknownB", a.UnsupportedReason);
        Assert.Contains("unknownA", b.UnsupportedReason);
        Assert.Equal(a.UnsupportedReason, again.UnsupportedReason);
    }

    [Fact]
    public void KnownOverridesPreservePrecedenceWhenGlobalsContainUnknownMetadata()
    {
        object metadataCache = NewMetadataCache();
        Dictionary<string, string> globals = new() { ["MSWINDOWS.fileattr"] = "32", ["MSWINDOWS.unknown"] = "unknown" };
        Dictionary<string, string> first = new() { ["MSWINDOWS.fileattr"] = "", ["MSWINDOWS.xattr.key"] = "AQ==" };
        Dictionary<string, string> second = new() { ["MSWINDOWS.xattr.key"] = "AQ==", ["MSWINDOWS.fileattr"] = "" };
        WindowsFileMetadata a = ReadMetadata(metadataCache, first, globals, TarEntryType.Directory);
        WindowsFileMetadata b = ReadMetadata(metadataCache, second, globals, TarEntryType.Directory);
        Assert.Equal(a.Fingerprint, b.Fingerprint);
        Assert.Contains("unknown", a.UnsupportedReason);
        Assert.Contains("Directory extended attributes", b.UnsupportedReason);
        Assert.Equal(a.UnsupportedReason, ReadMetadata(metadataCache, first, globals, TarEntryType.Directory).UnsupportedReason);
    }

    [Fact]
    public void OverrideCacheHasEntryAndRetainedByteLimitsAndClearsAllProfiles()
    {
        object metadataCache = NewMetadataCache();
        Type type = metadataCache.GetType();
        for (int i = 0; i < 64; i++)
        {
            ReadMetadata(metadataCache, new() { ["MSWINDOWS.rawsd"] = i + new string('d', 2048) }, new());
        }
        IList profiles = Assert.IsAssignableFrom<IList>(type.GetField("locals", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(metadataCache));
        long retained = (long)type.GetField("retainedBytes", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(metadataCache)!;
        Assert.InRange(profiles.Count, 1, 8);
        Assert.InRange(retained, 1, 128 * 1024);
        int count = profiles.Count;
        Dictionary<string, string> oversized = new() { ["MSWINDOWS.rawsd"] = new string('d', 128 * 1024) };
        ReadMetadata(metadataCache, oversized, new());
        Assert.Equal(count, profiles.Count);
        Assert.Equal(retained, type.GetField("retainedBytes", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(metadataCache));
        type.GetMethod("Clear")!.Invoke(metadataCache, null);
        Assert.Empty(profiles);
        Assert.Equal(0L, type.GetField("retainedBytes", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(metadataCache));
    }

    [Fact]
    public void OverrideCacheEvictsLeastRecentProfilesWhenByteBudgetIsReached()
    {
        object metadataCache = NewMetadataCache();
        Dictionary<string, string> a = new() { ["MSWINDOWS.rawsd"] = "a" + new string('d', 24000) };
        Dictionary<string, string> b = new() { ["MSWINDOWS.rawsd"] = "b" + new string('d', 24000) };
        Dictionary<string, string> c = new() { ["MSWINDOWS.rawsd"] = "c" + new string('d', 24000) };
        WindowsFileMetadata firstA = ReadMetadata(metadataCache, a, new());
        WindowsFileMetadata firstB = ReadMetadata(metadataCache, b, new());
        Assert.Same(firstA.Fingerprint, ReadMetadata(metadataCache, a, new()).Fingerprint);
        ReadMetadata(metadataCache, c, new());
        IList profiles = Assert.IsAssignableFrom<IList>(metadataCache.GetType()
            .GetField("locals", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(metadataCache));
        Assert.Equal(2, profiles.Count);
        Assert.Same(firstA.Fingerprint, ReadMetadata(metadataCache, a, new()).Fingerprint);
        WindowsFileMetadata secondB = ReadMetadata(metadataCache, b, new());
        Assert.Equal(firstB.Fingerprint, secondB.Fingerprint);
        Assert.NotSame(firstB.Fingerprint, secondB.Fingerprint);
        Assert.Equal(2, profiles.Count);
    }

    [Fact]
    public void IndependentFileAliasStorageUsesValuesInsteadOfPerFileSets()
    {
        Dictionary<string, ImageFileSystemEntry> entries = new(WindowsImagePathComparer.Instance);
        ImageFileSystemBuilder builder = new(Mock.Of<IDockerRegistryClient>(), Name, Mock.Of<IImageManifest>(),
            cache.Store, [], entries, new(WindowsImagePathComparer.Instance), ImageFileSystemSemantics.Windows);
        ScannedEntry[] files = [.. Enumerable.Range(0, 100).Select(i => new ScannedEntry($"file{i}",
            ImageFileType.File, 0, 0, 0, 1, DateTime.UnixEpoch, null, i, 0, 0, "hash"))];
        builder.ApplyLayer(new(files, [], []), new(0, "layer"), Token);
        object storage = typeof(ImageFileSystemBuilder).GetField("contentPaths", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(builder)!;
        Assert.True(storage.GetType().GenericTypeArguments[1].IsValueType);
        Assert.Equal(100, Assert.IsAssignableFrom<IDictionary>(storage).Count);
        Assert.Equal(100, ImageAnalysis.Analyze([new(files, [], [])], ImageFileSystemSemantics.Windows).LiveContents.Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ContentPathsPromoteDemoteAndPreservePlatformIdentity(bool windows)
    {
        StringComparer comparer = windows ? WindowsImagePathComparer.Instance : StringComparer.Ordinal;
        ImageContentPaths paths = new();
        paths.Add("Original", comparer);
        paths.Add("ORIGINAL", comparer);
        Assert.Equal(windows ? 1 : 2, paths.Count);
        paths.Add("Alias", comparer);
        Assert.Equal(windows ? 2 : 3, paths.Count);
        paths.Remove("missing", comparer);
        Assert.Equal(windows ? ["Original", "Alias"] : ["Original", "ORIGINAL", "Alias"], [.. paths]);
        paths.Remove("Original", comparer);
        if (!windows) paths.Remove("ORIGINAL", comparer);
        Assert.Equal(1, paths.Count);
        Assert.Equal(["Alias"], [.. paths]);
        paths.Add("\u03C2", comparer);
        paths.Add("\u03A3", comparer);
        Assert.Equal(3, paths.Count);
        paths.Remove("\u03C2", comparer);
        paths.Remove("\u03A3", comparer);
        Assert.Equal(["Alias"], [.. paths]);
        paths.Remove("Alias", comparer);
        Assert.Equal(0, paths.Count);
        string[] empty = [.. paths];
        Assert.Empty(empty);
    }

    [Fact]
    public void SingletonContentPathIndexHasBoundedAllocations()
    {
        string[] paths = [.. Enumerable.Range(0, 200000).Select(i => $"file{i}")];
        long before = GC.GetAllocatedBytesForCurrentThread();
        Dictionary<ImageContentId, ImageContentPaths> index = new(paths.Length);
        for (int i = 0; i < paths.Length; i++)
        {
            ImageContentPaths aliases = new();
            aliases.Add(paths[i], WindowsImagePathComparer.Instance);
            index.Add(new(0, paths[i], i), aliases);
        }
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.True(allocated < 15_000_000, $"Singleton path storage allocated {allocated:N0} bytes.");
        Assert.Equal(200000, index.Count);
        Assert.Equal(["file0"], [.. index[new(0, "file0", 0)]]);
    }

    [Fact]
    public void StreamMutationMethodsDoNotExposeUnusedResults()
    {
        Assert.Equal(typeof(void), typeof(WindowsAlternateStreams).GetMethod("Track")!.ReturnType);
        Assert.Equal(typeof(void), typeof(WindowsAlternateStreams).GetMethod("BindPending")!.ReturnType);
        Assert.Equal(2, typeof(WindowsAlternateStreams).GetMethod("DeleteStream")!.GetParameters().Length);
    }

    [Fact]
    public async Task RawEntryLookupIsCaseAwarePreservesLinksAndAvoidsListAllocation()
    {
        await using ImageFileSystem files = await ImageFileSystem.CreateAsync(Client(
            Layer(File("Files/target", "DATA"), Link("Files/Link", "target"),
                Link("Files/Alias", "Files/target", hard: true))).Object,
            Name, new(), Token, cache.Store, requireLayerIndexes: true);
        MethodInfo? find = typeof(ImageFileSystem).GetMethod("FindEntry", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(find);
        ImageFileSystemEntry link = Assert.IsType<ImageFileSystemEntry>(find.Invoke(files, ["LINK"]));
        Assert.Equal(ImageFileType.SymbolicLink, link.Type);
        Assert.Equal("target", link.LinkTarget);
        Assert.Equal(ImageFileType.HardLink, Assert.IsType<ImageFileSystemEntry>(find.Invoke(files, ["ALIAS"])).Type);
        Assert.Null(find.Invoke(files, ["absent"]));
        object?[] arguments = ["TARGET"];
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 10000; i++) Assert.NotNull(find.Invoke(files, arguments));
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.True(allocated < 500_000, $"Raw entry lookups allocated {allocated:N0} bytes.");
    }

    [Fact]
    public async Task HistoricalPreviewRetainsLinkMetadataDirectoryAndMissingResults()
    {
        Mock<IDockerRegistryClient> client = Client(
            Layer(File("Files/target", "OLD"), Link("Files/Link", "target"),
                Link("Files/Alias", "Files/target", hard: true), File("Files/Dir/child", "CHILD")),
            Layer(File("Files/target", "NEW"), File("Files/.wh.Link", "")));
        ExplorerSource source = await ExplorerSource.OpenAsync(client.Object, Mock.Of<IDockerRegistryClientFactory>(),
            Name, new(), null, Token);
        ExplorerSession session = await ExplorerSession.CreateAsync(client.Object, source, cache.Store, null, Token);
        await using ImageFileSystem files = session.Files;
        ExplorerImage image = ExplorerImage.FromSource(source);
        image.SetSession(session, ExplorerInsightsResult.Empty);
        await using ExplorerHost host = new(client.Object, Mock.Of<IDockerRegistryClientFactory>(), source, cache.Store,
            image, new ExplorerLayerIndexer(0, static (_, _, _) => throw new InvalidOperationException()),
            new(null, null, false, ClipboardMode.Off, "", ""), cache.Root) { Session = session };
        PreviewContent link = await host.PreviewVersionAsync("LINK", 0, Token);
        Assert.Equal(["target"], link.Lines);
        Assert.Equal(ImageFileType.SymbolicLink, link.Entry!.Type);
        Assert.Equal(["OLD"], (await host.PreviewVersionAsync("ALIAS", 1, Token)).Lines);
        Assert.Contains("Directory", (await host.PreviewVersionAsync("DIR", 0, Token)).Message);
        Assert.Contains("absent", (await host.PreviewVersionAsync("LINK", 1, Token)).Message);
        Assert.Equal(["NEW"], (await host.PreviewAsync("target", 1, Token)).Lines);
    }

    private static object NewMetadataCache() => Activator.CreateInstance(
        typeof(ImageLayerScanner).GetNestedType("WindowsMetadataCache", BindingFlags.NonPublic)!, nonPublic: true)!;

    private static WindowsFileMetadata ReadMetadata(object metadataCache, Dictionary<string, string> local,
        Dictionary<string, string> globals, TarEntryType type = TarEntryType.RegularFile) =>
        (WindowsFileMetadata)typeof(ImageLayerScanner).GetMethod("ReadWindowsMetadataCore", BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, [new PaxTarEntry(type, "Files/value", local), "value", globals, metadataCache])!;
}
