using System.Security.Cryptography;
using Valleysoft.Dredge.Explorer;

namespace Valleysoft.Dredge.Tests;

[Trait("Category", "Integration")]
public sealed class WindowsRegistryTests
{
    [Fact]
    public async Task IndexesPinnedDotnetSdkApplicationLayersWithReparseAttributes()
    {
        Assert.SkipUnless(Environment.GetEnvironmentVariable("DREDGE_TEST_WINDOWS_REGISTRY") == "1",
            "Set DREDGE_TEST_WINDOWS_REGISTRY=1 to download the public Windows acceptance layers.");
        CancellationToken token = TestContext.Current.CancellationToken;
        await using LayerCacheTestContext cache = new();
        IDockerRegistryClientFactory factory = new DockerRegistryClientFactory();
        ImageName image = ImageName.Parse(
            "mcr.microsoft.com/dotnet/sdk@sha256:792521792925990b3dc45189f2f548840212dfe36d7e5265f786996807cc7bfe");
        using IDockerRegistryClient client = await factory.GetClientAsync(image.Registry, token);
        ExplorerSource source = await ExplorerSource.OpenAsync(client, factory, image, new(), null, token);
        foreach (int layer in new[] { 3, 10, 15 })
        {
            var descriptor = source.Resolved.Manifest.Layers[layer];
            StoredLayerIndex index = await cache.Store.GetIndexAsync(client, image,
                new(layer, descriptor.Digest), descriptor.Size, token, semantics: ImageFileSystemSemantics.Windows);
            Assert.Contains(index.Changes.Entries, static entry =>
                entry.Type == ImageFileType.Directory && (entry.Windows!.Attributes.GetValueOrDefault() & 0x400) != 0);
            Assert.All(index.Changes.Entries, static entry => Assert.Null(entry.Windows!.UnsupportedReason));
            Assert.Contains(index.Changes.Diagnostics, static diagnostic => diagnostic.Message.Contains("reparse attributes"));
        }
    }

    [Fact]
    public async Task ExploresPinnedNanoServerAndServerCoreAndComparesBaseUpdates()
    {
        Assert.SkipUnless(Environment.GetEnvironmentVariable("DREDGE_TEST_WINDOWS_REGISTRY") == "1",
            "Set DREDGE_TEST_WINDOWS_REGISTRY=1 to download the public Windows acceptance images.");
        CancellationToken token = TestContext.Current.CancellationToken;
        await using LayerCacheTestContext cache = new();
        IDockerRegistryClientFactory factory = new DockerRegistryClientFactory();
        List<ExplorerSession> nano = [];
        (string Repository, string Digest, int Layers)[] samples =
        [
            ("nanoserver", "sha256:808d344f215d2cf5327474d4b8857881862956c3950aab220f72e8cf946ce480", 1),
            ("nanoserver", "sha256:6020b6edd804e7cf8cbca9142c351f597a59f53c5c7f07a6caa2261dcc1d8e8f", 1),
            ("servercore", "sha256:df8887a9e3ea17894720a023154fecce9dc6cc685ca5ff718a7f197f1dcff182", 2),
            ("servercore", "sha256:12eb9fb0087ea6c512db739291992948caa98f6fbef9e85ec4749ad4df3f0735", 2)
        ];
        foreach (var sample in samples)
        {
            ImageName image = ImageName.Parse($"mcr.microsoft.com/windows/{sample.Repository}@{sample.Digest}");
            using IDockerRegistryClient client = await factory.GetClientAsync(image.Registry, token);
            ExplorerSource source = await ExplorerSource.OpenAsync(client, factory, image, new(), null, token);
            Assert.Equal(sample.Layers, source.LayerCount);
            ExplorerSession session = await ExplorerSession.CreateAsync(client, source, cache.Store, null, token);
            await using ImageFileSystem files = session.Files;
            Assert.True(session.Entries.Count > 1000);
            Assert.Contains(session.Entries, static entry => entry.Path.Equals("Windows/System32/cmd.exe", StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(session.Entries, static entry => entry.Path.StartsWith("Files/", StringComparison.OrdinalIgnoreCase));
            using MemoryStream content = new();
            await files.CopyFileToAsync(@"C:\License.txt", content, token);
            Assert.Equal(5647, content.Length);
            string hash = Convert.ToHexStringLower(SHA256.HashData(content.ToArray()));
            Assert.Equal(session.Analysis.LiveContents["license.txt"].ContentHash, hash);
            ExplorerImage model = ExplorerImage.FromSource(source);
            model.SetSession(session, ExplorerInsightsResult.Empty);
            Assert.NotNull(model.Find(model.WholeTree(sample.Layers - 1)!, "WINDOWS/system32/CMD.EXE"));
            await using ImageFileSystem snapshot = files.CreateLayerSnapshot(0, token);
            Assert.NotEmpty(snapshot.List(null, true, false));
            await session.EnsurePackagesAsync(token);
            if (sample.Repository == "nanoserver")
            {
                nano.Add(session);
            }
        }
        ExplorerComparison comparison = ExplorerSession.Compare(nano[0], nano[1]);
        Assert.NotEmpty(comparison.Files);
        Assert.Contains(comparison.Files, static file => file.Kind == LayerChangeKind.Modified);
        Assert.NotEqual(nano[0].Config.OsVersion, nano[1].Config.OsVersion);
    }
}
