using System.Text;
using Valleysoft.Dredge.Explorer;
using static Valleysoft.Dredge.Tests.WindowsImageTestData;

namespace Valleysoft.Dredge.Tests;

public sealed class WindowsFileSystemIdentityTests : IAsyncDisposable
{
    private readonly LayerCacheTestContext cache = new();
    private CancellationToken Token => TestContext.Current.CancellationToken;
    public ValueTask DisposeAsync() => cache.DisposeAsync();

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SupplementaryCasePairsRemainIndependent(bool separateLayers)
    {
        byte[] first = Layer(File("Files/\U00010400/value", "FIRST"));
        byte[] second = Layer(File("Files/\U00010428/value", "SECOND"));
        Mock<IDockerRegistryClient> client = separateLayers ? Client(first, second)
            : Client(Layer(File("Files/\U00010400/value", "FIRST"), File("Files/\U00010428/value", "SECOND")));
        await using ImageFileSystem files = await ImageFileSystem.CreateAsync(
            client.Object, Name, new(), Token, cache.Store, requireLayerIndexes: true);
        await Check(files);
        Assert.Equal(2, files.Analyze().LiveContents.Count);
        string destination = Path.Combine(cache.Root, "unicode-output");
        string probe = Path.Combine(cache.Root, "\U00010400");
        Directory.CreateDirectory(probe);
        bool distinctNames = !Directory.Exists(Path.Combine(cache.Root, "\U00010428"));
        Directory.Delete(probe);
        if (distinctNames)
        {
            await files.ExtractAsync("", destination, Token);
            Assert.Equal("FIRST", await System.IO.File.ReadAllTextAsync(Path.Combine(destination, "\U00010400", "value"), Token));
            Assert.Equal("SECOND", await System.IO.File.ReadAllTextAsync(Path.Combine(destination, "\U00010428", "value"), Token));
            Assert.Equal(2, Directory.GetDirectories(destination).Length);
        }
        else
        {
            await Assert.ThrowsAsync<IOException>(() => files.ExtractAsync("", destination, Token));
            Assert.False(Directory.Exists(destination));
        }
        await using ImageFileSystem restored = await ImageFileSystem.CreateAsync(client.Object, Name, new(), Token, cache.Store);
        await Check(restored);

        async Task Check(ImageFileSystem view)
        {
            Assert.Equal(4, view.List(null, true, false).Count);
            Assert.Single(view.List("\U00010400", true, false));
            Assert.Single(view.List("\U00010428", true, false));
            await Content(view, "\U00010400/value", "FIRST");
            await Content(view, "\U00010428/value", "SECOND");
        }
    }

    [Fact]
    public async Task SupplementaryDirectoryWhiteoutsDoNotDeleteTheOtherName()
    {
        Mock<IDockerRegistryClient> client = Client(
            Layer(File("Files/\U00010400/value", "FIRST"), File("Files/\U00010428/value", "SECOND")),
            Layer(File("Files/.wh.\U00010400", "")));
        await using ImageFileSystem files = await ImageFileSystem.CreateAsync(
            client.Object, Name, new(), Token, cache.Store, requireLayerIndexes: true);
        await Content(files, "\U00010428/value", "SECOND");
        Assert.DoesNotContain(files.List(null, true, false), entry => entry.Path.StartsWith("\U00010400", StringComparison.Ordinal));
        Assert.Single(files.Analyze().LiveContents);
    }

    [Theory]
    [InlineData("App", "aPP", true)]
    [InlineData("\u00C9", "\u00E9", true)]
    [InlineData("\U00010400", "\U00010428", false)]
    public void WindowsIdentityUsesConsistentEqualityHashOrderingAndPrefixes(string first, string second, bool equal)
    {
        ImageFileSystemSemantics semantics = ImageFileSystemSemantics.Windows;
        Assert.Equal(equal, semantics.Comparer.Equals(first, second));
        Assert.Equal(equal, semantics.Comparer.Compare(first, second) == 0);
        Assert.Equal(equal, semantics.StartsWith(first + "/child", second + "/"));
        Assert.Equal(equal ? 1 : 2, new HashSet<string>([first, second], semantics.Comparer).Count);
        if (equal) Assert.Equal(semantics.Comparer.GetHashCode(first), semantics.Comparer.GetHashCode(second));
    }

    [Fact]
    public void UnpairedSurrogateCodeUnitsRemainDistinct() =>
        WindowsIdentityUsesConsistentEqualityHashOrderingAndPrefixes(new string('\uD801', 1), new string('\uD802', 1), false);

    [Fact]
    public void ExplorerTreesRetainSupplementaryCasePairs()
    {
        ExplorerImage image = new(Name.ToString(), "windows/amd64", "digest", ["layer"], [1], null);
        image.SetIndexed(0, new([
            new("\U00010400/value", ImageFileType.File, 0, 0, 0, 1, DateTime.UnixEpoch, null, 0, 0, 0, "first"),
            new("\U00010428/value", ImageFileType.File, 0, 0, 0, 1, DateTime.UnixEpoch, null, 1, 0, 0, "second")
        ], [], []));
        Assert.Equal(2, image.SearchIndex.Count);
        Assert.Equal("\U00010400/value", image.Find(image.LayerTree(0), "\U00010400/value")!.Path);
        Assert.Equal("\U00010428/value", image.Find(image.LayerTree(0), "\U00010428/value")!.Path);
    }

    [Fact]
    public async Task CrossImageComparisonDoesNotMergeSupplementaryCasePairs()
    {
        ExplorerSession before = await Load(Layer(File("Files/\U00010400", "same")));
        ExplorerSession after = await Load(Layer(File("Files/\U00010428", "same")));
        await using ImageFileSystem beforeFiles = before.Files;
        await using ImageFileSystem afterFiles = after.Files;
        ExplorerComparison comparison = ExplorerSession.Compare(before, after);
        Assert.Equal(2, comparison.Files.Count);
        Assert.Contains(comparison.Files, entry => entry.Path == "\U00010400" && entry.Kind == LayerChangeKind.Deleted);
        Assert.Contains(comparison.Files, entry => entry.Path == "\U00010428" && entry.Kind == LayerChangeKind.Added);

        async Task<ExplorerSession> Load(byte[] layer)
        {
            Mock<IDockerRegistryClient> client = Client(layer);
            ExplorerSource source = await ExplorerSource.OpenAsync(client.Object,
                Mock.Of<IDockerRegistryClientFactory>(), Name, new(), null, Token);
            ExplorerSession session = await ExplorerSession.CreateAsync(client.Object, source, cache.Store, null, Token);
            await session.EnsurePackagesAsync(Token);
            return session;
        }
    }

    [Theory]
    [InlineData("same-layer")]
    [InlineData("later-stream")]
    [InlineData("alias-stream")]
    [InlineData("old-stream-new-target")]
    [InlineData("new-stream-new-target")]
    [InlineData("removed-stream")]
    [InlineData("stream-before-file")]
    [InlineData("hardlink-chain")]
    [InlineData("old-and-new-streams")]
    [InlineData("removed-through-alias")]
    public async Task StreamRestrictionsFollowCapturedHardLinkIdentity(string scenario)
    {
        byte[] first = scenario is "same-layer" or "old-stream-new-target" or "removed-stream" or "old-and-new-streams" or "removed-through-alias"
            ? Layer(File("Files/original", "OLD"), File("Files/original:secret", "SECRET"),
                Link("Files/alias", "Files/original", hard: true))
            : scenario == "stream-before-file"
                ? Layer(File("Files/original:secret", "SECRET"), File("Files/original", "OLD"),
                    Link("Files/alias", "Files/original", hard: true))
            : scenario == "hardlink-chain"
                ? Layer(File("Files/original", "OLD"), Link("Files/intermediate", "Files/original", hard: true),
                    Link("Files/alias", "Files/intermediate", hard: true), File("Files/alias:secret", "SECRET"))
            : Layer(File("Files/original", "OLD"), Link("Files/alias", "Files/original", hard: true));
        byte[]? second = scenario switch
        {
            "later-stream" => Layer(File("Files/original:secret", "SECRET")),
            "alias-stream" => Layer(File("Files/alias:secret", "SECRET")),
            "old-stream-new-target" => Layer(File("Files/original", "NEW")),
            "new-stream-new-target" => Layer(File("Files/original", "NEW"), File("Files/original:secret", "SECRET")),
            "removed-stream" => Layer(File("Files/.wh.original:secret", "")),
            "removed-through-alias" => Layer(File("Files/.wh.alias:secret", "")),
            "old-and-new-streams" => Layer(File("Files/original", "NEW"), File("Files/original:secret", "NEW STREAM")),
            _ => null
        };
        Mock<IDockerRegistryClient> client = second is null ? Client(first) : Client(first, second);
        await using ImageFileSystem files = await ImageFileSystem.CreateAsync(
            client.Object, Name, new(), Token, cache.Store, requireLayerIndexes: true);
        await Check(files, "fresh");
        await using ImageFileSystem restored = await ImageFileSystem.CreateAsync(client.Object, Name, new(), Token, cache.Store);
        await Check(restored, "restored");
        await using ImageFileSystem snapshot = files.CreateLayerSnapshot(0, Token);
        await Export(snapshot, "alias", "snapshot",
            scenario is "same-layer" or "old-stream-new-target" or "removed-stream" or "stream-before-file" or "hardlink-chain" or "old-and-new-streams" or "removed-through-alias", "OLD");

        async Task Check(ImageFileSystem view, string label)
        {
            await Export(view, "alias", label, scenario is not ("new-stream-new-target" or "removed-stream" or "removed-through-alias"), "OLD");
            await Export(view, "original", label, scenario is not ("old-stream-new-target" or "removed-stream" or "removed-through-alias"),
                scenario is "old-stream-new-target" or "new-stream-new-target" or "old-and-new-streams" ? "NEW" : "OLD");
        }
    }

    [Fact]
    public async Task DirectoryStreamSiblingsBlockDirectoryExtraction()
    {
        Mock<IDockerRegistryClient> client = Client(Layer(
            File("Files/Directory/value", "value"), File("Files/Directory:secret", "SECRET")));
        await using ImageFileSystem files = await ImageFileSystem.CreateAsync(client.Object, Name, new(), Token, cache.Store);
        await Assert.ThrowsAsync<NotSupportedException>(() =>
            files.ExtractAsync("Directory", Path.Combine(cache.Root, "output"), Token));
        Assert.False(Directory.Exists(Path.Combine(cache.Root, "output")));
    }

    [Fact]
    public async Task WindowsPackageMetadataMatchesSharedUnavailableFactory()
    {
        Mock<IDockerRegistryClient> client = Client(Layer(File("Files/value", "value")));
        await using ImageFileSystem files = await ImageFileSystem.CreateAsync(client.Object, Name, new(), Token, cache.Store);
        InstalledPackageMetadata metadata = await InstalledPackageReader.ReadAsync(files, Token);
        foreach (var ecosystem in metadata.Ecosystems.Values)
        {
            InstalledPackageEcosystemMetadata expected = InstalledPackageReader.CreateMetadata(false, []);
            Assert.Equal(expected.Availability, ecosystem.Availability);
            Assert.Empty(ecosystem.Packages);
            Assert.Same(StringComparer.Ordinal,
                Assert.IsType<Dictionary<string, IReadOnlyList<string>>>(ecosystem.Packages).Comparer);
        }
    }

    private async Task Content(ImageFileSystem view, string path, string expected)
    {
        using MemoryStream content = new();
        await view.CopyFileToAsync(path, content, Token);
        Assert.Equal(expected, Encoding.UTF8.GetString(content.ToArray()));
    }

    private async Task Export(ImageFileSystem view, string path, string label, bool blocked, string expected)
    {
        string destination = Path.Combine(cache.Root, $"{label}-{path}");
        if (blocked)
        {
            await Assert.ThrowsAsync<NotSupportedException>(() => view.ExtractAsync(path, destination, Token));
            Assert.False(System.IO.File.Exists(destination));
        }
        else
        {
            await view.ExtractAsync(path, destination, Token);
            Assert.Equal(expected, await System.IO.File.ReadAllTextAsync(destination, Token));
        }
    }
}
