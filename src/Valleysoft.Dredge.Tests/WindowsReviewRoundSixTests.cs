using System.Reflection;
using System.Text;
using Valleysoft.DockerRegistryClient.Models.Manifests.Docker;
using static Valleysoft.Dredge.Tests.WindowsImageTestData;

namespace Valleysoft.Dredge.Tests;

public sealed class WindowsReviewRoundSixTests : IAsyncDisposable
{
    private readonly LayerCacheTestContext cache = new();
    private CancellationToken Token => TestContext.Current.CancellationToken;
    public ValueTask DisposeAsync() => cache.DisposeAsync();

    [Theory]
    [InlineData("a")]
    [InlineData("C")]
    [InlineData("file")]
    [InlineData("sub/a")]
    public async Task HardLinksToArchiveStreamsRemainUnsupportedWithoutBlockingOtherFiles(string basename)
    {
        Mock<IDockerRegistryClient> client = Client(Layer(
            File($"Files/{basename}:secret", "SECRET"),
            Link("Files/alias", $"Files/{basename}:secret", hard: true),
            Link("Files/second-alias", "Files/alias", hard: true),
            File("Files/value", "VALUE")));
        await using ImageFileSystem files = await ImageFileSystem.CreateAsync(client.Object, Name, new(), Token, cache.Store);
        await Check(files);
        await using ImageFileSystem restored = await ImageFileSystem.CreateAsync(client.Object, Name, new(), Token, cache.Store);
        await Check(restored);

        async Task Check(ImageFileSystem view)
        {
            foreach (string alias in new[] { "alias", "second-alias" })
            {
                ImageFileSystemEntry entry = Assert.Single(view.List(alias, false, false));
                Assert.Equal(ImageFileType.Other, entry.Type);
                Assert.Contains("unsupported", entry.Windows!.UnsupportedReason);
                await Assert.ThrowsAsync<NotSupportedException>(() => view.CopyFileToAsync(alias, new MemoryStream(), Token));
            }
            await Content(view, "value", "VALUE");
        }
    }

    [Theory]
    [InlineData("", false)]
    [InlineData("sub/", false)]
    [InlineData("", true)]
    [InlineData("sub/", true)]
    public async Task DeletingStreamThroughAliasRemovesEveryVisibleStreamName(string prefix, bool multipleNames)
    {
        byte[] first = multipleNames
            ? Layer(File($"Files/{prefix}original", "BASE"), File($"Files/{prefix}original:secret", "SECRET"),
                Link($"Files/{prefix}alias", $"Files/{prefix}original", hard: true),
                File($"Files/{prefix}alias:SECRET", "SECRET"))
            : Layer(File($"Files/{prefix}original", "BASE"), File($"Files/{prefix}original:secret", "SECRET"),
                Link($"Files/{prefix}alias", $"Files/{prefix}original", hard: true));
        Mock<IDockerRegistryClient> client = Client(first,
            Layer(File($"Files/{prefix}.wh.ALIAS:Secret", "")));
        await using ImageFileSystem files = await ImageFileSystem.CreateAsync(
            client.Object, Name, new(), Token, cache.Store, requireLayerIndexes: true);
        await Check(files, "fresh");
        await using ImageFileSystem restored = await ImageFileSystem.CreateAsync(client.Object, Name, new(), Token, cache.Store);
        await Check(restored, "restored");
        await using ImageFileSystem snapshot = files.CreateLayerSnapshot(0, Token);
        await Assert.ThrowsAsync<NotSupportedException>(() =>
            snapshot.ExtractAsync(prefix.TrimEnd('/'), Path.Combine(cache.Root, "snapshot"), Token));

        async Task Check(ImageFileSystem view, string label)
        {
            Assert.DoesNotContain(view.List(null, true, false), entry => entry.Path.Contains(':'));
            Assert.Contains(view.List(null, true, true), entry =>
                entry.Path == prefix + "original:secret" && entry.DeletedLayer?.Index == 1);
            Assert.False(Assert.Single(view.List(prefix + "original", false, false)).Windows!.HasAlternateStreams);
            Assert.False(Assert.Single(view.List(prefix + "alias", false, false)).Windows!.HasAlternateStreams);
            string destination = Path.Combine(cache.Root, label);
            await view.ExtractAsync(prefix.TrimEnd('/'), destination, Token);
            Assert.Equal("BASE", await System.IO.File.ReadAllTextAsync(Path.Combine(destination, "original"), Token));
            Assert.Equal("BASE", await System.IO.File.ReadAllTextAsync(Path.Combine(destination, "alias"), Token));
            Assert.Equal(2, Directory.GetFiles(destination).Length);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DeletingOldAliasStreamDoesNotDeleteTheReplacementFilesStream(bool removeReplacementStream)
    {
        List<byte[]> layers =
        [
            Layer(File("Files/original", "OLD"), File("Files/original:secret", "OLD STREAM"),
                Link("Files/alias", "Files/original", hard: true)),
            Layer(File("Files/original", "NEW"), File("Files/original:secret", "NEW STREAM")),
            Layer(File("Files/.wh.alias:secret", ""))
        ];
        if (removeReplacementStream) layers.Add(Layer(File("Files/.wh.original:secret", "")));
        Mock<IDockerRegistryClient> client = Client([.. layers]);
        await using ImageFileSystem files = await ImageFileSystem.CreateAsync(
            client.Object, Name, new(), Token, cache.Store, requireLayerIndexes: true);
        await Check(files, "fresh");
        await using ImageFileSystem restored = await ImageFileSystem.CreateAsync(client.Object, Name, new(), Token, cache.Store);
        await Check(restored, "restored");

        async Task Check(ImageFileSystem view, string label)
        {
            string alias = Path.Combine(cache.Root, label + "-alias");
            await view.ExtractAsync("alias", alias, Token);
            Assert.Equal("OLD", await System.IO.File.ReadAllTextAsync(alias, Token));
            Assert.Equal(!removeReplacementStream, view.List(null, true, false).Any(entry => entry.Path == "original:secret"));
            string original = Path.Combine(cache.Root, label + "-original");
            if (removeReplacementStream)
            {
                await view.ExtractAsync("original", original, Token);
                Assert.Equal("NEW", await System.IO.File.ReadAllTextAsync(original, Token));
            }
            else
            {
                await Assert.ThrowsAsync<NotSupportedException>(() => view.ExtractAsync("original", original, Token));
            }
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EmptyLayersDoNotReconcileEveryContentIdentity(bool removeStream)
    {
        Dictionary<string, ImageFileSystemEntry> entries = new(ImageFileSystemSemantics.Windows.Comparer);
        for (int i = 0; i < 10_000; i++)
        {
            string path = $"value{i}";
            entries.Add(path, new()
            {
                Path = path, Type = ImageFileType.File, IntroducedLayer = new(0, "base"),
                ContentPath = path, ContentEntryIndex = i, Windows = new(32, false, false, "", null)
            });
        }
        ImageFileSystemBuilder builder = new(Mock.Of<IDockerRegistryClient>(), Name, new DockerManifest(),
            cache.Store, [], entries, new(ImageFileSystemSemantics.Windows.Comparer), ImageFileSystemSemantics.Windows);
        ScannedEntry stream = new("value0:secret", ImageFileType.Other, 0, 0, 0, 0, DateTime.UnixEpoch,
            null, entries.Count, 0, 0, "hash") { Windows = new(32, false, false, "", "Unsupported stream.") };
        builder.ApplyLayer(new([stream], [], []), new(1, "stream"), Token);
        if (removeStream) builder.ApplyLayer(new([], ["value0:secret"], []), new(2, "removed"), Token);
        FieldInfo field = typeof(ImageFileSystemBuilder).GetField("alternateStreams", BindingFlags.Instance | BindingFlags.NonPublic)!;
        WindowsAlternateStreams tracker = Assert.IsType<WindowsAlternateStreams>(field.GetValue(builder));
        FieldInfo streamsField = typeof(WindowsAlternateStreams).GetField("streams", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var tracking = Assert.IsType<Dictionary<ImageContentId, HashSet<string>>>(streamsField.GetValue(tracker));
        CountingContentComparer counted = new();
        Dictionary<ImageContentId, HashSet<string>> instrumented = new(1, counted);
        foreach (var pair in tracking) instrumented.Add(pair.Key, pair.Value);
        streamsField.SetValue(tracker, instrumented);
        counted.HashCalls = 0;
        for (int i = 0; i < 10; i++) builder.ApplyLayer(new([], [], []), new(i + 3, "empty"), Token);
        Assert.Equal(0, counted.HashCalls);
        Assert.Equal(!removeStream, entries["value0"].Windows!.HasAlternateStreams);
        ScannedEntry alias = stream with
        {
            Path = "alias", Type = ImageFileType.HardLink, LinkTarget = "value0",
            Windows = new(32, false, false, "", null)
        };
        builder.ApplyLayer(new([alias], [], []), new(13, "alias"), Token);
        Assert.Equal(!removeStream, entries["alias"].Windows!.HasAlternateStreams);
    }

    private async Task Content(ImageFileSystem view, string path, string expected)
    {
        using MemoryStream content = new();
        await view.CopyFileToAsync(path, content, Token);
        Assert.Equal(expected, Encoding.UTF8.GetString(content.ToArray()));
    }

    private sealed class CountingContentComparer : IEqualityComparer<ImageContentId>
    {
        public long HashCalls { get; set; }
        public bool Equals(ImageContentId first, ImageContentId second) => first == second;
        public int GetHashCode(ImageContentId value)
        {
            HashCalls++;
            return value.GetHashCode();
        }
    }
}
