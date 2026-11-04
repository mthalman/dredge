using System.Formats.Tar;
using System.Security.Cryptography;
using System.Text;
using Docker.DotNet.Models;
using Valleysoft.DockerRegistryClient;
using Valleysoft.Dredge.Commands;
using Valleysoft.Dredge.Explorer;
using Valleysoft.Dredge.Explorer.Tui;

namespace Valleysoft.Dredge.Tests;

public sealed class ExplorerDaemonSnapshotRetentionTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task EvictedDaemonTargetReopensItsFrozenIdentityWithoutRefreshingTheTag(
        bool removeOriginal, bool changeDaemon)
    {
        string root = Path.Combine(Directory.GetCurrentDirectory(),
            "explorer-daemon-snapshots-" + Guid.NewGuid().ToString("N"));
        string cache = Path.Combine(root, "cache");
        string scratch = Path.Combine(root, "exports");
        Dictionary<string, Fixture> tags = new()
        {
            ["app:baseline"] = Fixture.Create("baseline"),
            ["app:A"] = Fixture.Create("original A"),
            ["app:B"] = Fixture.Create("B"),
            ["app:C"] = Fixture.Create("C")
        };
        Dictionary<string, Fixture> images = tags.Values.ToDictionary(value => value.Id);
        List<string> inspections = [];
        List<string> saves = [];
        string daemon = "original-daemon";
        int connections = 0;
        DockerConnection connection = new(new Uri("http://selected-daemon:2375"), null);
        IDockerRegistryClientFactory registry = Mock.Of<IDockerRegistryClientFactory>();
        ImageSourceFactory factory = new(registry, null, selected =>
        {
            Assert.Same(connection, selected);
            return new Engine(tags, images, inspections, saves, () => daemon);
        }, scratch, _ =>
        {
            connections++;
            return connection;
        }, () => new LayerStore(cache));
        try
        {
            await using LayerStore store = new(cache, maxBytes: 0);
            PlatformOptionsBase options = new() { DockerHost = "tcp://selected-daemon:2375" };
            await using ExplorerSource baseline = await ExplorerSource.OpenAsync(
                factory, registry, "docker://app:baseline", options, null, Token, store: store);
            ExplorerSession session = await ExplorerSession.CreateAsync(null, baseline, store, null, Token);
            ExplorerLayerIndexer indexer = ExplorerLayerIndexer.Create(null, baseline, store);
            await using ExplorerHost host = new(null, registry, baseline, store,
                ExplorerImage.FromSource(baseline), indexer,
                new(null, null, false, ClipboardMode.Off, "", ""), root) { Session = session };

            ExplorerComparison first = await host.CompareAsync("A", () => { }, Token);
            string originalId = first.Target.Source!.Identity;
            TextDiffContent originalDiff = await host.DiffAsync(first, "payload.txt", Token);
            Assert.Contains(originalDiff.Lines!, line => line.Text == "original A");
            string originalBlob = store.GetBlobPath(first.Target.Resolved.Manifest.Layers[0].Digest!);
            await host.CompareAsync("B", () => { }, Token);
            await host.CompareAsync("C", () => { }, Token);
            await store.ClearAsync(Token);
            Assert.False(File.Exists(originalBlob));
            Assert.Equal(3, Directory.GetDirectories(scratch, "*", SearchOption.TopDirectoryOnly).Length);

            Fixture replacement = Fixture.Create("retagged A");
            images.Add(replacement.Id, replacement);
            tags["app:A"] = replacement;
            options.DockerHost = "tcp://changed-options:2375";
            if (removeOriginal)
            {
                images.Remove(originalId);
            }
            if (changeDaemon)
            {
                daemon = "replacement-daemon";
            }
            int before = inspections.Count;
            if (changeDaemon)
            {
                await Assert.ThrowsAsync<InvalidOperationException>(() =>
                    host.CompareAsync("A", () => { }, Token));
                Assert.Equal(before, inspections.Count);
            }
            else if (removeOriginal)
            {
                await Assert.ThrowsAsync<IOException>(() => host.CompareAsync("A", () => { }, Token));
                Assert.Equal([originalId], inspections.Skip(before));
            }
            else
            {
                ExplorerComparison revisited = await host.CompareAsync("A", () => { }, Token);
                Assert.Equal(originalId, revisited.Target.Source!.Identity);
                Assert.Equal("docker://" + originalId, revisited.Target.Source.Reference.ToString());
                Assert.Equal("docker://" + originalId, revisited.Target.SnapshotReference);
                Assert.True(first.Target.ComparisonPlatform.IsEquivalentTo(revisited.Target.ComparisonPlatform));
                Assert.Same(connection, revisited.Target.Source.Connection);
                Assert.Equal(originalDiff.Lines, (await host.DiffAsync(revisited, "payload.txt", Token)).Lines);
                Assert.Equal([originalId], inspections.Skip(before));
                Assert.Equal(2, saves.Count(id => id == originalId));
            }
            Assert.DoesNotContain(replacement.Id, saves);
            Assert.Equal(1, connections);
            await host.DisposeAsync();
            Assert.Single(Directory.GetDirectories(scratch, "*", SearchOption.TopDirectoryOnly));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }
    }

    private sealed record Fixture(string Id, byte[] Export)
    {
        public static Fixture Create(string payload)
        {
            byte[] layer = Tar(("payload.txt", Encoding.UTF8.GetBytes(payload)));
            byte[] config = Encoding.UTF8.GetBytes($$"""
                {"os":"linux","architecture":"amd64","rootfs":{"type":"layers","diff_ids":["{{Hash(layer)}}"]},"history":[{"created_by":"COPY payload.txt /"}]}
                """);
            return new(Hash(config), Tar(
                ("manifest.json", """[{"Config":"config.json","Layers":["layer.tar"]}]"""u8.ToArray()),
                ("config.json", config), ("layer.tar", layer)));
        }

        private static string Hash(byte[] bytes) =>
            "sha256:" + Convert.ToHexStringLower(SHA256.HashData(bytes));

        private static byte[] Tar(params (string Path, byte[] Bytes)[] entries)
        {
            using MemoryStream stream = new();
            using (TarWriter writer = new(stream, TarEntryFormat.Pax, leaveOpen: true))
            {
                foreach ((string path, byte[] bytes) in entries)
                {
                    writer.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, path)
                    {
                        ModificationTime = DateTimeOffset.UnixEpoch,
                        DataStream = new MemoryStream(bytes)
                    });
                }
            }
            return stream.ToArray();
        }
    }

    private sealed class Engine(
        Dictionary<string, Fixture> tags, Dictionary<string, Fixture> images,
        List<string> inspections, List<string> saves, Func<string> daemon) : IDockerImageEngine
    {
        public Task<string> GetDaemonIdentityAsync(CancellationToken token) => Task.FromResult(daemon());

        public Task<ImageInspectResponse> InspectAsync(string reference, CancellationToken token)
        {
            inspections.Add(reference);
            if (!(reference.StartsWith("sha256:", StringComparison.Ordinal) ? images : tags)
                .TryGetValue(reference, out Fixture? image))
            {
                throw new IOException("Frozen image is no longer available.");
            }
            return Task.FromResult(new ImageInspectResponse
            {
                ID = image.Id, Os = "linux", Architecture = "amd64"
            });
        }

        public Task<Stream> SaveAsync(string id, CancellationToken token)
        {
            saves.Add(id);
            return Task.FromResult<Stream>(new MemoryStream(images[id].Export));
        }

        public void Dispose() { }
    }
}
