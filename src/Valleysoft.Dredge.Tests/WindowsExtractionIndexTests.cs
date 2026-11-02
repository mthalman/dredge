using System.Collections;
using static Valleysoft.Dredge.Tests.WindowsImageTestData;

namespace Valleysoft.Dredge.Tests;

public sealed class WindowsExtractionIndexTests : IAsyncDisposable
{
    private readonly LayerCacheTestContext cache = new();
    private CancellationToken Token => TestContext.Current.CancellationToken;
    public ValueTask DisposeAsync() => cache.DisposeAsync();

    [Fact]
    public void CapturedFilePlansDoNotEnumerateImagePaths()
    {
        Dictionary<string, ImageFileSystemEntry> entries = new(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < 20_000; i++)
        {
            string path = $"value{i}";
            entries.Add(path, new() { Path = path, Type = ImageFileType.File, IntroducedLayer = new(0, "layer") });
        }
        entries["VALUE0:secret:$DATA"] = new()
        {
            Path = "VALUE0:secret:$DATA", Type = ImageFileType.Other, IntroducedLayer = new(0, "layer")
        };
        entries["value0"] = entries["value0"] with
        {
            Windows = new(null, false, false, "", null) { HasAlternateStreams = true }
        };
        CountedKeysDictionary counted = new(entries);
        ExtractionPlanner planner = new(new(counted, ImageFileSystemSemantics.Windows),
            counted, ImageFileSystemSemantics.Windows);
        string destination = Path.Combine(cache.Root, "output");
        for (int i = 1; i <= 3; i++)
        {
            ExtractionPlan plan = planner.CreatePlan($"value{i}", destination, false, entries[$"value{i}"],
                counted, useCapturedStreamState: true);
            Assert.Single(plan.Entries);
        }
        Assert.Throws<NotSupportedException>(() =>
            planner.CreatePlan("value0", destination, false, entries["value0"], counted,
                useCapturedStreamState: true));
        Assert.Equal(0, counted.KeyVisits);
        Assert.False(System.IO.File.Exists(destination));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CachedViewsAndHistoricalSnapshotsRetainTheirOwnStreamState(bool removed)
    {
        byte[] first = removed
            ? Layer(File("Files/value", "value"), File("Files/value:secret", "stream"), File("Files/clean", "clean"))
            : Layer(File("Files/value", "value"), File("Files/clean", "clean"));
        byte[] second = removed
            ? Layer(File("Files/.wh.value:secret", ""))
            : Layer(File("Files/value:secret", "stream"));
        Mock<IDockerRegistryClient> client = Client(first, second);
        await using ImageFileSystem files = await ImageFileSystem.CreateAsync(
            client.Object, Name, new(), Token, cache.Store, requireLayerIndexes: true);
        await files.ExtractAsync("clean", Path.Combine(cache.Root, "clean-full"), Token);
        await Check(files, "full", blocked: !removed);
        await using ImageFileSystem snapshot = files.CreateLayerSnapshot(0, Token);
        await snapshot.ExtractAsync("clean", Path.Combine(cache.Root, "clean-snapshot"), Token);
        await Check(snapshot, "snapshot", blocked: removed);
        await using ImageFileSystem restored = await ImageFileSystem.CreateAsync(
            client.Object, Name, new(), Token, cache.Store);
        await restored.ExtractAsync("clean", Path.Combine(cache.Root, "clean-restored"), Token);
        await Check(restored, "restored", blocked: !removed);

        async Task Check(ImageFileSystem view, string label, bool blocked)
        {
            string destination = Path.Combine(cache.Root, label);
            if (blocked)
            {
                await Assert.ThrowsAsync<NotSupportedException>(() => view.ExtractAsync("VALUE", destination, Token));
                Assert.False(System.IO.File.Exists(destination));
            }
            else
            {
                await view.ExtractAsync("VALUE", destination, Token);
                Assert.Equal("value", await System.IO.File.ReadAllTextAsync(destination, Token));
            }
        }
    }

    private sealed class CountedKeysDictionary(IReadOnlyDictionary<string, ImageFileSystemEntry> entries)
        : IReadOnlyDictionary<string, ImageFileSystemEntry>
    {
        public int KeyVisits { get; private set; }
        public ImageFileSystemEntry this[string key] => entries[key];
        public int Count => entries.Count;
        public IEnumerable<ImageFileSystemEntry> Values => entries.Values;
        public IEnumerable<string> Keys
        {
            get
            {
                foreach (string key in entries.Keys)
                {
                    KeyVisits++;
                    yield return key;
                }
            }
        }
        public bool ContainsKey(string key) => entries.ContainsKey(key);
        public bool TryGetValue(string key, out ImageFileSystemEntry value) => entries.TryGetValue(key, out value!);
        public IEnumerator<KeyValuePair<string, ImageFileSystemEntry>> GetEnumerator() => entries.GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
