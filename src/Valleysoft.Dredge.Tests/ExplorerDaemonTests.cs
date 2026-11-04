using System.Formats.Tar;
using System.IO.Compression;
using System.Text;
using Valleysoft.DockerRegistryClient;
using Valleysoft.DockerRegistryClient.Models.Images;
using Valleysoft.DockerRegistryClient.Models.Manifests;
using Valleysoft.DockerRegistryClient.Models.Manifests.Oci;
using Valleysoft.Dredge.Commands;
using Valleysoft.Dredge.Commands.Image;
using Valleysoft.Dredge.Explorer;
using Valleysoft.Dredge.Explorer.Tui;
using static Valleysoft.Dredge.Tests.LayerCacheTestContext;

namespace Valleysoft.Dredge.Tests;

public sealed class ExplorerDaemonTests
{
    [Theory]
    [InlineData("docker://app:current", "previous", "docker://app:previous")]
    [InlineData("docker://team/app:current", "previous", "docker://team/app:previous")]
    [InlineData("docker://app", "previous", "docker://app:previous")]
    [InlineData("docker://sha256:dev", "previous", "docker://sha256:previous")]
    [InlineData("docker://sha256:abc", "previous", "docker://sha256:previous")]
    [InlineData("docker://sha256:0123456789ab", "previous", "docker://sha256:previous")]
    [InlineData("docker://sha256:5000/repo:dev", "previous", "docker://sha256:5000/repo:previous")]
    [InlineData("docker://library/app:current", "previous", "docker://library/app:previous")]
    [InlineData("docker://localhost:5000/team/app", "previous", "docker://localhost:5000/team/app:previous")]
    [InlineData("docker://localhost:5000/team/app:current", "previous", "docker://localhost:5000/team/app:previous")]
    [InlineData("docker://[::1]:5000/team/app:current", "previous", "docker://[::1]:5000/team/app:previous")]
    [InlineData("docker://[::1]:5000/team/app@sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "previous", "docker://[::1]:5000/team/app:previous")]
    [InlineData("docker://team/app@sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "previous", "docker://team/app:previous")]
    [InlineData("docker://app@sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "previous", "docker://app:previous")]
    [InlineData("registry://registry.test/team/app:current", "previous", "registry://registry.test/team/app:previous")]
    [InlineData("registry.test/team/app:current", "previous", "registry.test/team/app:previous")]
    [InlineData("docker://app:current", "registry://registry.test/app:previous", "registry://registry.test/app:previous")]
    [InlineData("docker://app:current", "other:previous", "other:previous")]
    [InlineData("registry.test/app:current", "docker://app:previous", "docker://app:previous")]
    public void CompareReferencesPreserveSourceSemantics(string baseline, string input, string expected) =>
        Assert.Equal(expected, ExploreCommand.ResolveCompareReference(baseline, input));

    [Fact]
    public void CompareImageIdCannotUseTagShorthand()
    {
        ArgumentException error = Assert.Throws<ArgumentException>(() =>
            ExploreCommand.ResolveCompareReference("docker://sha256:" + new string('a', 64), "previous"));
        Assert.Contains("full docker://", error.Message);
    }

    [Fact]
    public void CompareImageIdAcceptsExplicitReferences()
    {
        string baseline = "docker://sha256:" + new string('a', 64);
        Assert.Equal("docker://team/app:previous",
            ExploreCommand.ResolveCompareReference(baseline, "docker://team/app:previous"));
        Assert.Equal("registry://registry.test/team/app:previous",
            ExploreCommand.ResolveCompareReference(baseline, "registry://registry.test/team/app:previous"));
    }

    [Fact]
    public void DaemonModelPinsCommandsToImageIdWithoutRegistryDigest()
    {
        string id = "sha256:" + new string('a', 64);
        ExplorerImage image = new("docker://app:current", "linux/amd64", id,
            ["sha256:stored"], [10240], null);

        Assert.Equal("docker://" + id, image.ResolvedReference);
        Assert.False(image.DownloadSizeAvailable);
        Assert.Equal("stored", image.TransferLabel);
        ExplorerPresenter presenter = new(image, 180, 50);
        string header = string.Join('\n', presenter.Header(new()));
        Assert.Contains("stored", header);
        Assert.DoesNotContain(" download", header);
    }

    [Theory]
    [InlineData("docker://app:current", true)]
    [InlineData("registry://registry.test/app:current", false)]
    [InlineData("registry.test/app:current", false)]
    public void ModelDerivesSourceKindFromItsReference(string reference, bool daemon)
    {
        string id = "sha256:" + new string('a', 64);
        ExplorerImage image = new(reference, "linux/amd64", id, [], [], null);

        Assert.Equal(daemon, image.IsDaemon);
        Assert.Equal(!daemon, image.DownloadSizeAvailable);
        Assert.Equal(daemon ? "stored" : "download", image.TransferLabel);
        Assert.Equal(daemon, image.ResolvedReference.StartsWith("docker://", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task EquivalentRawAndGzipSourcesShareFilesPackagesAndProvenance(bool beforeDaemon, bool afterDaemon)
    {
        string cache = CachePath();
        try
        {
            await using LayerStore store = new(cache);
            byte[] tar = Layer();
            await using ResolvedImage before = Source(beforeDaemon, tar);
            await using ResolvedImage after = Source(afterDaemon, tar);
            ExplorerSession baseline = await ExplorerSession.CreateAsync(
                null, Explorer(before), store, null, Token);
            ExplorerSession target = await ExplorerSession.CreateAsync(
                null, Explorer(after), store, null, Token);
            await using ImageFileSystem beforeFiles = baseline.Files;
            await using ImageFileSystem afterFiles = target.Files;
            if (beforeDaemon || afterDaemon)
            {
                await baseline.EnsureContentIdentitiesAsync(Token);
                await target.EnsureContentIdentitiesAsync(Token);
            }
            await baseline.EnsurePackagesAsync(Token);
            await target.EnsurePackagesAsync(Token);
            ExplorerComparison comparison = ExplorerSession.Compare(baseline, target);
            Assert.Empty(comparison.Files);
            Assert.Empty(comparison.Packages);
            Assert.Equal(["1.0"], baseline.Packages.Ecosystems[InstalledPackageEcosystem.Npm].Packages["example"]);
            Assert.Equal(baseline.Analysis.LiveLayers["app/node_modules/example/package.json"],
                target.Analysis.LiveLayers["app/node_modules/example/package.json"]);
            CompareState state = new(comparison, "before", "after");
            Assert.True(state.Shared(0));
            Assert.Equal(!beforeDaemon && !afterDaemon, comparison.DownloadSizeAvailable);
            Assert.Equal(0, comparison.AdditionalDownloadBytes);
            CompareView view = new(new(ExplorerImage.FromSource(Explorer(before)), 180, 50), state);
            string details = string.Join('\n', view.Details().Lines);
            if (beforeDaemon || afterDaemon)
            {
                Assert.Contains("Download size unavailable", details);
                Assert.Contains("verified diff ID", details);
            }
        }
        finally
        {
            if (Directory.Exists(cache))
            {
                Directory.Delete(cache, true);
            }
        }
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, false, true)]
    [InlineData(false, true, false)]
    [InlineData(false, true, true)]
    [InlineData(true, false, false)]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    [InlineData(true, true, true)]
    public async Task ComparisonAlwaysValidatesBothSourcesLayerDigests(
        bool beforeDaemon, bool afterDaemon, bool invalidTarget)
    {
        await using LayerCacheTestContext cache = new();
        byte[] layer = Layer();
        await using ResolvedImage before = Source(beforeDaemon, layer);
        await using ResolvedImage after = Source(afterDaemon, layer);
        ExplorerSession baseline = await ExplorerSession.CreateAsync(null, Explorer(before), cache.Store, null, Token);
        await using ImageFileSystem beforeFiles = baseline.Files;
        ExplorerSession target = await ExplorerSession.CreateAsync(null, Explorer(after), cache.Store, null, Token);
        await using ImageFileSystem afterFiles = target.Files;
        ResolvedImage invalid = invalidTarget ? after : before;
        ((OciDescriptor)invalid.Resolved.Manifest.Layers[0]).Digest = null!;

        InvalidDataException error = Assert.Throws<InvalidDataException>(() => ExplorerSession.Compare(baseline, target));

        Assert.Equal("Layer digest is missing.", error.Message);
    }

    [Fact]
    public async Task DaemonLoadingAndTagCapabilitiesNeverOpenARegistry()
    {
        string cache = CachePath();
        Owner owner = new();
        ResolvedImage image = Source(true, Layer(), owner);
        Factory sources = new((reference, _) =>
        {
            Assert.Equal("docker://app:current", reference);
            return image;
        });
        Mock<IDockerRegistryClientFactory> registries = new(MockBehavior.Strict);
        try
        {
            await using LayerStore store = new(cache);
            await using ExplorerSource source = await ExplorerSource.OpenAsync(sources,
                registries.Object, "docker://app:current", new(), null, Token);
            ExplorerSession session = await ExplorerSession.CreateAsync(null, source, store, null, Token);
            ExplorerLayerIndexer indexer = ExplorerLayerIndexer.Create(null, source, store);
            await using ExplorerHost host = new(null, registries.Object, source, store,
                ExplorerImage.FromSource(source), indexer, new(null, null, false, ClipboardMode.Off, "", ""))
                { Session = session };
            Assert.False(owner.Disposed);
            Assert.Equal(["{\"name\":\"example\",\"version\":\"1.0\"}"],
                (await host.PreviewAsync("app/node_modules/example/package.json", 0, Token)).Lines);
            NotSupportedException error = await Assert.ThrowsAsync<NotSupportedException>(() => host.ListTagsAsync(Token));
            Assert.Contains("Docker daemon", error.Message);
            await Assert.ThrowsAsync<NotSupportedException>(() => host.DescribeTagAsync(new("previous"), Token));
            registries.VerifyNoOtherCalls();
            Assert.False(owner.Disposed);
        }
        finally
        {
            if (Directory.Exists(cache))
            {
                Directory.Delete(cache, true);
            }
        }
        Assert.True(owner.Disposed);
    }

    [Fact]
    public async Task OpensWindowsDaemonAndReleasesSnapshot()
    {
        Owner owner = new();
        ResolvedImage image = Source(true, Layer(), owner, os: "windows");
        Factory sources = new((_, _) => image);
        await using (ExplorerSource explorer = await ExplorerSource.OpenAsync(
            sources, Mock.Of<IDockerRegistryClientFactory>(), "docker://app:current", new(), null, Token))
        {
            Assert.Equal("windows/amd64", explorer.Platform!.ToString());
            Assert.False(owner.Disposed);
        }
        Assert.True(owner.Disposed);
    }

    [Fact]
    public async Task BackgroundLoadingKeepsDaemonSnapshotAliveThroughCallbacks()
    {
        string cache = CachePath();
        Owner owner = new();
        try
        {
            await using LayerStore store = new(cache);
            await using ExplorerSource source = Explorer(Source(true, Layer(), owner));
            await using ExplorerApp app = new(null, Mock.Of<IDockerRegistryClientFactory>(), source,
                store, new(null, null, false, ClipboardMode.Off, "", ""), Token);
            app.Start();
            using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(Token);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            while (app.Host.Session is null)
            {
                await Task.Delay(10, timeout.Token);
            }
            Assert.False(owner.Disposed);
            Assert.NotNull((await app.Host.PreviewAsync("app/node_modules/example/package.json", 0, Token)).Lines);
            await app.DisposeAsync();
            Assert.False(owner.Disposed);
        }
        finally
        {
            if (Directory.Exists(cache))
            {
                Directory.Delete(cache, true);
            }
        }
        Assert.True(owner.Disposed);
    }

    [Fact]
    public async Task CancellationAfterAcquisitionReleasesDaemonSnapshot()
    {
        Owner owner = new();
        using CancellationTokenSource canceled = new();
        Factory sources = new((_, _) =>
        {
            canceled.Cancel();
            return Source(true, Layer(), owner);
        });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ExplorerSource.OpenAsync(
            sources, Mock.Of<IDockerRegistryClientFactory>(), "docker://app:current", new(), null, canceled.Token));
        Assert.True(owner.Disposed);
    }

    [Fact]
    public async Task DaemonTagPickerExplainsCapabilityWithoutCallingHost()
    {
        int calls = 0;
        FakeExplorerHost host = new()
        {
            ListTagsWork = _ =>
            {
                Interlocked.Increment(ref calls);
                throw new InvalidOperationException("The daemon picker must not list registry tags.");
            },
            DescribeTagWork = (_, _) =>
            {
                Interlocked.Increment(ref calls);
                throw new InvalidOperationException("The daemon picker must not describe registry tags.");
            }
        };
        ExplorerImage image = new("docker://app:current", "linux/amd64", "sha256:image",
            [], [], null);
        TaskCompletionSource<string?> message = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TagPicker.Loading loading = new(host, image, Token, action => action(),
            (_, error) => message.TrySetResult(error), () => { });
        try
        {
            string? explanation = await message.Task.WaitAsync(TimeSpan.FromSeconds(10), Token);
            Assert.Contains("Docker daemon", explanation);
            Assert.Contains("registry://", explanation);
            Assert.Equal(0, calls);
        }
        finally
        {
            await loading.StopAsync();
        }
    }

    [Fact]
    public async Task DaemonCompareShorthandRetainsBothSourcesUntilHostStops()
    {
        string cache = CachePath();
        Owner baselineOwner = new(), targetOwner = new();
        try
        {
            await using LayerStore store = new(cache);
            byte[] tar = Layer();
            Factory sources = new((reference, options) =>
            {
                Assert.Equal("docker://app:previous", reference);
                Assert.Equal("unix:///selected.sock", options.DockerHost);
                return Source(true, tar, targetOwner);
            });
            ResolvedImage baseline = Source(true, tar, baselineOwner);
            await using ExplorerSource source = new()
            {
                Source = baseline, SourceFactory = sources,
                SourceOptions = new() { DockerHost = "unix:///selected.sock" },
                Image = baseline.Name, Config = baseline.Configuration, Resolved = baseline.Resolved,
                Platforms = [], RequestedReference = "docker://app:current"
            };
            Assert.Contains("--docker-host", ExplorerImage.FromSource(source).CommandArguments);
            Assert.Contains("unix:///selected.sock", ExplorerImage.FromSource(source).CommandArguments);
            ExplorerSession session = await ExplorerSession.CreateAsync(null, source, store, null, Token);
            ExplorerLayerIndexer indexer = ExplorerLayerIndexer.Create(null, source, store);
            await using ExplorerHost host = new(null, Mock.Of<IDockerRegistryClientFactory>(), source,
                store, ExplorerImage.FromSource(source), indexer, new(null, null, false, ClipboardMode.Off, "", ""))
                { Session = session };
            ExplorerComparison comparison = await host.CompareAsync("previous", () =>
            {
                Assert.False(baselineOwner.Disposed);
                Assert.False(targetOwner.Disposed);
            }, Token);
            Assert.Empty(comparison.Files);
            Assert.Empty(comparison.Packages);
            Assert.False(targetOwner.Disposed);
            await host.DisposeAsync();
            Assert.True(targetOwner.Disposed);
            Assert.False(baselineOwner.Disposed);
            Assert.Single(sources.Calls);
        }
        finally
        {
            if (Directory.Exists(cache))
            {
                Directory.Delete(cache, true);
            }
        }
        Assert.True(baselineOwner.Disposed);
    }

    [Fact]
    public async Task MixedContentIdentityRejectsIncorrectRegistryDiffId()
    {
        string cache = CachePath();
        try
        {
            await using LayerStore store = new(cache);
            await using ResolvedImage image = Source(false, Layer(), diffId: "sha256:" + new string('b', 64));
            ExplorerSession session = await ExplorerSession.CreateAsync(null, Explorer(image), store, null, Token);
            await using ImageFileSystem files = session.Files;
            InvalidDataException error = await Assert.ThrowsAsync<InvalidDataException>(() =>
                session.EnsureContentIdentitiesAsync(Token));
            Assert.Contains("rootfs diff ID", error.Message);
        }
        finally
        {
            if (Directory.Exists(cache))
            {
                Directory.Delete(cache, true);
            }
        }
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task ExplicitBasesUseContentPrefixAcrossSources(bool targetDaemon, bool baseDaemon)
    {
        string cache = CachePath();
        try
        {
            await using LayerStore store = new(cache);
            byte[] tar = Layer();
            await using ResolvedImage target = Source(targetDaemon, tar);
            Owner owner = new();
            ResolvedImage candidate = Source(baseDaemon, tar, owner);
            string reference = baseDaemon ? "docker://base:current" : "registry://registry.test/base:current";
            Factory sources = new((actualReference, options) =>
            {
                Assert.Equal(reference, actualReference);
                Assert.Equal("linux", options.Os);
                Assert.Equal("amd64", options.Architecture);
                return candidate;
            });
            var (bases, warning) = await ExplorerSource.VerifySourceBasesAsync(sources,
                target.Configuration, target.Resolved, targetDaemon, new(), [reference], Token,
                target.Content, store);
            Assert.Equal(new ExplorerBaseImage(reference, 1), Assert.Single(bases));
            Assert.Null(warning);
            Assert.True(owner.Disposed);
            Assert.Single(sources.Calls);
        }
        finally
        {
            if (Directory.Exists(cache))
            {
                Directory.Delete(cache, true);
            }
        }
    }

    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private static string CachePath() => Path.Combine(Directory.GetCurrentDirectory(),
        "explorer-daemon-cache-" + Guid.NewGuid().ToString("N"));

    private static ExplorerSource Explorer(ResolvedImage source) => new()
    {
        Source = source, Image = source.Name, Config = source.Configuration,
        Resolved = source.Resolved, Platforms = [],
        RequestedReference = source.Reference.ToString()
    };

    private static ResolvedImage Source(bool daemon, byte[] tar, Owner? owner = null,
        string os = "linux", string? diffId = null)
    {
        byte[] blob = tar;
        if (!daemon)
        {
            using MemoryStream compressed = new();
            using (GZipStream gzip = new(compressed, CompressionLevel.SmallestSize, leaveOpen: true))
            {
                gzip.Write(tar);
            }
            blob = compressed.ToArray();
        }
        string digest = Digest(blob);
        string json = $$"""{"os":"{{os}}","architecture":"amd64","rootfs":{"type":"layers","diff_ids":["{{diffId ?? Digest(tar)}}"]},"history":[{"created_by":"COPY app /app"}]}""";
        Image config = JsonHelper.Deserialize<Image>(json)!;
        string identity = Digest(Encoding.UTF8.GetBytes(json));
        OciImageManifest manifest = new()
        {
            Config = new OciDescriptor { Digest = identity },
            Layers = [new OciDescriptor { Digest = digest, Size = blob.Length }]
        };
        ManifestInfo info = new("application/vnd.oci.image.manifest.v1+json",
            "sha256:" + new string('c', 64), manifest);
        ImageSourceReference reference = ImageSourceReference.Parse(daemon
            ? "docker://app:current" : "registry://registry.test/app:current");
        return new(reference, ImageName.Parse("registry.test/app:current"), new(info, manifest),
            daemon ? null : info, config, Encoding.UTF8.GetBytes(json), new Content(blob),
            daemon ? identity : info.DockerContentDigest, os + "/amd64", owner);
    }

    private static byte[] Layer()
    {
        using MemoryStream tar = new();
        using (TarWriter writer = new(tar, TarEntryFormat.Pax, leaveOpen: true))
        {
            writer.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, "app/node_modules/example/package.json")
            {
                DataStream = new MemoryStream("""{"name":"example","version":"1.0"}"""u8.ToArray()),
                Mode = UnixFileMode.UserRead | UnixFileMode.UserWrite
            });
        }
        return tar.ToArray();
    }

    private sealed class Content(byte[] bytes) : IImageContentSource
    {
        public Task<BlobDownloadResult> GetRangeAsync(string digest, long start, long? length, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            return Task.FromResult(new BlobDownloadResult(new MemoryStream(bytes), false, null, null, bytes.Length));
        }
    }

    private sealed class Factory(Func<string, PlatformOptionsBase, ResolvedImage> open) : IImageSourceFactory
    {
        public List<string> Calls { get; } = [];
        public Task<ResolvedImage> OpenAsync(string reference, PlatformOptionsBase options, CancellationToken token,
            ImageSourceRequirements requirements = ImageSourceRequirements.LayerContent)
        {
            token.ThrowIfCancellationRequested();
            Calls.Add(reference);
            return Task.FromResult(open(reference, options));
        }
    }

    private sealed class Owner : IAsyncDisposable
    {
        public bool Disposed { get; private set; }
        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }
}
