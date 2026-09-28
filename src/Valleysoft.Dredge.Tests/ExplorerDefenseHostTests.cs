using System.Formats.Tar;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Terminal.Gui.App;
using Valleysoft.DockerRegistryClient;
using Valleysoft.DockerRegistryClient.Models.Images;
using Valleysoft.DockerRegistryClient.Models.Manifests;
using Valleysoft.DockerRegistryClient.Models.Manifests.Oci;
using Valleysoft.Dredge.Commands;
using Valleysoft.Dredge.Explorer;
using Valleysoft.Dredge.Explorer.Tui;

namespace Valleysoft.Dredge.Tests;

public sealed class ExplorerDefenseHostTests : IAsyncDisposable
{
    private readonly string cachePath = Path.Combine(Directory.GetCurrentDirectory(),
        "host-defense-cache-" + Guid.NewGuid().ToString("N"));
    private readonly List<LayerStore> stores = [];
    private readonly List<ExplorerHost> hosts = [];
    private static readonly ImageName Image = ImageName.Parse("registry.test/repo:current");
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task PackageInventoriesAreLazyCachedAndIsolatedByLayer()
    {
        const string path = "app/node_modules/example/package.json";
        TestImage image = await CreateAsync(
            Blob((path, """{"name":"example","version":"1.0"}""")),
            Blob((path, """{"name":"example","version":"2.0"}""")),
            Blob(("app/node_modules/example/.wh.package.json", "")));
        ExplorerHost host = Host(image);
        Assert.Throws<InvalidOperationException>(() => image.Session.Packages);

        InstalledPackageMetadata first = await host.PackagesAsync(0, Token);
        InstalledPackageMetadata second = await host.PackagesAsync(1, Token);
        Assert.Equal(["1.0"], first.Ecosystems[InstalledPackageEcosystem.Npm].Packages["example"]);
        Assert.Equal(["2.0"], second.Ecosystems[InstalledPackageEcosystem.Npm].Packages["example"]);
        Assert.Equal(["app/node_modules/example"], first.NpmPackageRoots["example"]);
        Assert.Equal(first.NpmPackageRoots["example"], second.NpmPackageRoots["example"]);
        Assert.Throws<InvalidOperationException>(() => image.Session.Packages);
        Assert.Same(first, await host.PackagesAsync(0, Token));
        Assert.Same(second, await host.PackagesAsync(1, Token));

        InstalledPackageMetadata final = await host.PackagesAsync(2, Token);
        Assert.Empty(final.Ecosystems[InstalledPackageEcosystem.Npm].Packages);
        Assert.Empty(final.NpmPackageRoots);
        await image.Session.EnsurePackagesAsync(Token);
        Assert.Same(final, image.Session.Packages);
        using CancellationTokenSource canceled = new();
        canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => host.PackagesAsync(0, canceled.Token));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => host.PackagesAsync(3, Token));
    }

    [Fact]
    public async Task EmptyImageHasAnUnavailablePackageInventory()
    {
        TestImage image = await CreateAsync();
        InstalledPackageMetadata metadata = await Host(image).PackagesAsync(0, Token);
        Assert.All(metadata.Ecosystems.Values, ecosystem =>
        {
            Assert.Equal(InstalledPackageMetadataAvailability.Unavailable, ecosystem.Availability);
            Assert.Empty(ecosystem.Packages);
        });
    }

    [Fact]
    public async Task TerminalViewerExitCanceledBeforeLaunchDeletesItsStagedFile()
    {
        TestImage image = await CreateAsync(Blob(("file", "private bytes")));
        string file = await Host(image).PrepareForViewerAsync("file", Token);
        using CancellationTokenSource cancellation = CancellationTokenSource.CreateLinkedTokenSource(Token);
        await using ExplorerApp app = new(image.Client.Object, Mock.Of<IDockerRegistryClientFactory>(),
            image.Source, image.Store, new(null, null, false, ClipboardMode.Off, KeyMap.Default, "", ""),
            cancellation.Token);
        bool launched = false;

        Assert.ThrowsAny<OperationCanceledException>(() => app.Run(_ =>
        {
            cancellation.Cancel();
            return new(ExplorerExitKind.Viewer, file);
        }, _ =>
        {
            launched = true;
            return null;
        }));

        Assert.False(launched);
        Assert.False(System.IO.File.Exists(file));
        Assert.False(Directory.Exists(Path.GetDirectoryName(file)));
    }

    [Fact]
    public async Task BackgroundDispatchDoesNotHoldExplorerLockWhileInvokingUi()
    {
        TestImage image = await CreateAsync(Blob(("file", "value")));
        await using ExplorerApp app = new(image.Client.Object, Mock.Of<IDockerRegistryClientFactory>(),
            image.Source, image.Store, new(null, null, false, ClipboardMode.Off, KeyMap.Default, "", ""), Token);
        TaskCompletionSource<bool> observed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Mock<IApplication> application = new();
        using ExplorerWindow window = app.Attach(application.Object, new());
        application.Setup(value => value.Invoke(It.IsAny<Action>())).Callback(() =>
        {
            ManualResetEventSlim detached = new(false);
            Task detach = Task.Run(() =>
            {
                app.Detach();
                detached.Set();
            });
            bool completed = detached.Wait(TimeSpan.FromSeconds(2), Token);
            _ = detach.ContinueWith(_ => detached.Dispose(), TaskScheduler.Default);
            observed.TrySetResult(completed);
        });
        app.Start();

        Assert.True(await observed.Task.WaitAsync(TimeSpan.FromSeconds(10), Token),
            "A UI dispatch must not retain the explorer lock while waiting for Terminal.Gui's timer lock.");
    }

    [Fact]
    public async Task IndexerStopDrainsIgnoredCancellationWithoutPublishingLateResults()
    {
        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int publications = 0;
        ExplorerLayerIndexer indexer = new(1, async (_, progress, _) =>
        {
            entered.SetResult();
            await release.Task;
            progress.Report(1);
            return new("sha256:index", 1, new([], [], []));
        });
        indexer.Indexed += (_, _) => publications++;
        indexer.Completed += _ => publications++;
        indexer.Progress += (_, _) => publications++;
        indexer.Start(Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10), Token);

        Task stop = indexer.StopAsync();
        Assert.False(stop.IsCompleted);
        Assert.False(indexer.Retry(0));
        release.SetResult();
        await stop.WaitAsync(TimeSpan.FromSeconds(10), Token);

        Assert.Equal(0, publications);
        Assert.Empty(indexer.Snapshot());
        Assert.Same(stop, indexer.StopAsync());
    }

    [Fact]
    public async Task EmptyIndexerStopDrainsItsCompletionCallback()
    {
        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using ManualResetEventSlim release = new(false);
        ExplorerLayerIndexer indexer = new(0, (_, _, _) => throw new InvalidOperationException());
        indexer.Completed += _ =>
        {
            entered.SetResult();
            Assert.True(release.Wait(TimeSpan.FromSeconds(10), Token));
        };
        indexer.Start(Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10), Token);
        Task stop = indexer.StopAsync();
        Assert.False(stop.IsCompleted);
        release.Set();
        await stop.WaitAsync(TimeSpan.FromSeconds(10), Token);
    }

    [Fact]
    public async Task HostDisposalDrainsComparisonAndGateWaitersBeforeDisposingClients()
    {
        TestImage baseline = await CreateAsync();
        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource<ManifestInfo> release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Mock<IDockerRegistryClient> other = new() { DefaultValue = DefaultValue.Mock };
        other.Setup(c => c.Manifests.GetAsync("repo", "previous", It.IsAny<CancellationToken>()))
            .Returns(() =>
            {
                entered.TrySetResult();
                return release.Task;
            });
        other.Setup(c => c.Blobs.GetAsync("repo", "sha256:config", It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new MemoryStream(Encoding.UTF8.GetBytes("""{"os":"linux","architecture":"amd64"}""")));
        Mock<IDockerRegistryClientFactory> factory = new();
        factory.Setup(f => f.GetClientAsync("other.test", It.IsAny<CancellationToken>())).ReturnsAsync(other.Object);
        ExplorerHost host = Host(baseline, factory.Object);
        Task<ExplorerComparison> first = host.CompareAsync("other.test/repo:previous", () => { }, Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10), Token);
        Task<ExplorerComparison> waiting = host.CompareAsync("waiting", () => { }, Token);

        Task disposal = host.DisposeAsync().AsTask();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
        Assert.False(disposal.IsCompleted);
        other.Verify(c => c.Dispose(), Times.Never);
        release.SetResult(baseline.Source.Resolved.ManifestInfo);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        await disposal.WaitAsync(TimeSpan.FromSeconds(10), Token);

        other.Verify(c => c.Dispose(), Times.Once);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => host.ListTagsAsync(Token));
        await host.DisposeAsync();
        other.Verify(c => c.Dispose(), Times.Once);
    }

    [Fact]
    public async Task AppDisposalWaitsForIndexerAndDiscardsItsPendingUpdates()
    {
        byte[] blob = Blob(("file", "value"));
        TestImage image = await CreateAsync(blob);
        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        image.Client.Setup(c => c.Blobs.GetRangeAsync(Image.Repo, LayerCacheTestContext.Digest(blob),
            0, It.IsAny<long?>(), It.IsAny<CancellationToken>()))
            .Returns(async () =>
            {
                entered.SetResult();
                await release.Task;
                return new BlobDownloadResult(new MemoryStream(blob), false, null, null, blob.Length);
            });
        await using ExplorerApp app = new(image.Client.Object, Mock.Of<IDockerRegistryClientFactory>(),
            image.Source, image.Store, new(null, null, false, ClipboardMode.Off, KeyMap.Default, "", ""), Token);
        app.Start();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10), Token);

        Task disposal = app.DisposeAsync().AsTask();
        Assert.False(disposal.IsCompleted);
        release.SetResult();
        await disposal.WaitAsync(TimeSpan.FromSeconds(10), Token);

        Assert.Null(app.Host.Session);
        Assert.All(app.Image.States, state => Assert.Equal(ExplorerLayerState.Waiting, state));
    }

    [Fact]
    public async Task ViewerStagingUsesPrivateDirectoryAndPreservesContent()
    {
        TestImage image = await CreateAsync(Blob(("private.txt", "private image bytes")));
        string file = await Host(image).PrepareForViewerAsync("private.txt", Token);
        string directory = Path.GetDirectoryName(file)!;
        try
        {
            CacheFileSystem.CreateDirectory(directory);
            Assert.Equal("private image bytes", await System.IO.File.ReadAllTextAsync(file, Token));
            Assert.Throws<IOException>(() => CacheFileSystem.CreateFile(file));
            if (!OperatingSystem.IsWindows())
            {
                Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, System.IO.File.GetUnixFileMode(file));
            }
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task FailedViewerStagingRemovesItsPrivateDirectory()
    {
        TestImage image = await CreateAsync();
        await Assert.ThrowsAsync<FileNotFoundException>(() => Host(image).PrepareForViewerAsync("missing", Token));
        Assert.Empty(Directory.GetDirectories(cachePath, "dredge-*"));
    }

    [Fact]
    public async Task PreviewRetainsTruncationAlongsideFinalVersionProvenance()
    {
        TestImage image = await CreateAsync(Blob(("file", "old")),
            Blob(("file", new string('x', ExplorerApp.PreviewLimit + 1))));
        PreviewContent preview = await Host(image).PreviewAsync("file", 0, Token);

        Assert.NotNull(preview.Lines);
        Assert.Contains("Showing the first", preview.Message);
        Assert.Contains("Showing the final version from layer 1.", preview.Message);
        Assert.Equal(ExplorerApp.PreviewLimit, preview.Bytes);
    }

    [Fact]
    public async Task CachedTagDescriptionUsesTheComparisonSnapshotAfterTagMoves()
    {
        TestImage baseline = await CreateAsync(Blob(("baseline", "base")));
        byte[] targetBlob = Blob(("target", "old tag"));
        TestImage target = await CreateAsync(targetBlob);
        TestImage moved = await CreateAsync(Blob(("moved", "new tag")), Blob(("extra", "new layer")));
        baseline.Client.Setup(c => c.Manifests.GetAsync(Image.Repo, "previous", It.IsAny<CancellationToken>()))
            .ReturnsAsync(target.Source.Resolved.ManifestInfo);
        baseline.Client.Setup(c => c.Blobs.GetRangeAsync(Image.Repo, LayerCacheTestContext.Digest(targetBlob),
            0, It.IsAny<long?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new BlobDownloadResult(new MemoryStream(targetBlob), false, null, null, targetBlob.Length));
        ExplorerHost host = Host(baseline);
        ExplorerComparison comparison = await host.CompareAsync("registry.test/repo:previous", () => { }, Token);
        baseline.Client.Setup(c => c.Manifests.GetAsync(Image.Repo, "previous", It.IsAny<CancellationToken>()))
            .ReturnsAsync(moved.Source.Resolved.ManifestInfo);

        TagChoice choice = new("previous");
        await host.DescribeTagAsync(choice, Token);
        ExplorerComparison repeated = await host.CompareAsync("previous", () => { }, Token);

        Assert.Same(comparison.Target, repeated.Target);
        Assert.Equal(comparison.Target.Resolved.ManifestInfo.DockerContentDigest, choice.Digest);
        Assert.Equal(1, choice.LayerCount);
        Assert.Equal(targetBlob.Length, choice.AdditionalDownload);
        Assert.Contains("cached session snapshot", choice.Note);
        Assert.Contains("reopen explorer", choice.Note);
        baseline.Client.Verify(c => c.Manifests.GetAsync(Image.Repo, "previous", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task CurrentTagDescriptionPinsItsSnapshotBeforeComparison()
    {
        TestImage baseline = await CreateAsync();
        TestImage moved = await CreateAsync(Blob(("new", "tag moved")));
        TagChoice choice = new("current");
        ExplorerHost host = Host(baseline);
        await host.DescribeTagAsync(choice, Token);
        baseline.Client.Setup(c => c.Manifests.GetAsync(Image.Repo, "current", It.IsAny<CancellationToken>()))
            .ReturnsAsync(moved.Source.Resolved.ManifestInfo);

        ExplorerComparison comparison = await host.CompareAsync("registry.test/repo:current", () => { }, Token);

        Assert.Equal(baseline.Source.Resolved.ManifestInfo.DockerContentDigest, choice.Digest);
        Assert.Equal(choice.Digest, comparison.Target.Resolved.ManifestInfo.DockerContentDigest);
        Assert.Empty(comparison.Target.Entries);
        Assert.Contains("session snapshot", choice.Note);
        baseline.Client.Verify(c => c.Manifests.GetAsync(Image.Repo, It.IsAny<string>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task MovedCurrentTagDescriptionMatchesItsCachedComparisonThroughBothAliases()
    {
        TestImage baseline = await CreateAsync();
        byte[] movedBlob = Blob(("new", "tag moved"));
        TestImage moved = await CreateAsync(movedBlob);
        baseline.Client.Setup(c => c.Manifests.GetAsync(Image.Repo, "current", It.IsAny<CancellationToken>()))
            .ReturnsAsync(moved.Source.Resolved.ManifestInfo);
        baseline.Client.Setup(c => c.Blobs.GetRangeAsync(Image.Repo, LayerCacheTestContext.Digest(movedBlob),
            0, It.IsAny<long?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new BlobDownloadResult(new MemoryStream(movedBlob), false, null, null, movedBlob.Length));
        ExplorerHost host = Host(baseline);
        ExplorerComparison comparison = await host.CompareAsync("current", () => { }, Token);
        baseline.Client.Setup(c => c.Manifests.GetAsync(Image.Repo, "current", It.IsAny<CancellationToken>()))
            .ReturnsAsync(baseline.Source.Resolved.ManifestInfo);

        foreach (string alias in new[] { "current", "registry.test/repo:current" })
        {
            TagChoice choice = new(alias);
            await host.DescribeTagAsync(choice, Token);
            ExplorerComparison repeated = await host.CompareAsync(alias, () => { }, Token);

            Assert.Same(comparison.Target, repeated.Target);
            Assert.Equal(comparison.Target.Resolved.ManifestInfo.DockerContentDigest, choice.Digest);
            Assert.Equal(1, choice.LayerCount);
            Assert.Equal(0, choice.Shared);
            Assert.Equal(movedBlob.Length, choice.AdditionalDownload);
            Assert.DoesNotContain("same digest", choice.Note);
        }
        baseline.Client.Verify(c => c.Manifests.GetAsync(Image.Repo, "current",
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task OversizedOwnershipIsRejectedBeforeDownloadWhileReadableFilesRemain()
    {
        byte[] large = Blob(("other/example-1.dist-info/RECORD", "oversized,,"));
        TestImage baseline = await CreateAsync(
            [Blob(("site/example-1.dist-info/RECORD", "good,,"), ("site/good", "old")), large],
            indexes =>
            {
                StoredLayerIndex index = indexes[1];
                indexes[1] = index with
                {
                    Changes = index.Changes with
                    {
                        Entries = index.Changes.Entries.Select(entry =>
                            entry with { Size = ExplorerHost.MaxPackageOwnershipBytes + 1 }).ToArray()
                    }
                };
            });
        TestImage target = await CreateAsync();
        PackageFilesContent result = await Host(baseline).PackageFilesAsync(await CompareAsync(baseline, target),
            new(InstalledPackageEcosystem.Pip, "example", "1", null), Token);

        Assert.Equal(("site/good", Change.Removed), Assert.Single(result.Files!));
        Assert.Equal(1, result.Total);
        Assert.Contains("incomplete", result.Message);
        Assert.Contains("67108864", Assert.Single(result.Warnings!));
        baseline.Client.Verify(c => c.Blobs.GetRangeAsync(Image.Repo, LayerCacheTestContext.Digest(large),
            0, It.IsAny<long?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task MalformedAndInvalidUtf8OwnershipDoNotDiscardReadableRecords()
    {
        TestImage baseline = await CreateAsync(Archive(
            ("site/example-1.dist-info/RECORD", Encoding.UTF8.GetBytes("good,,")),
            ("bad/example-1.dist-info/RECORD", Encoding.UTF8.GetBytes("\"unterminated,,\nfabricated,,")),
            ("invalid/example-1.dist-info/RECORD", [0xff]),
            ("site/good", Encoding.UTF8.GetBytes("old"))));
        TestImage target = await CreateAsync();
        PackageFilesContent result = await Host(baseline).PackageFilesAsync(await CompareAsync(baseline, target),
            new(InstalledPackageEcosystem.Pip, "example", "1", null), Token);

        Assert.Equal(("site/good", Change.Removed), Assert.Single(result.Files!));
        Assert.Equal(2, result.Warnings!.Count);
        Assert.All(result.Warnings, warning => Assert.StartsWith("Baseline /", warning));
    }

    [Fact]
    public async Task UnsupportedNuGetOwnershipIsExplicitlyUnavailable()
    {
        TestImage baseline = await CreateAsync();
        PackageFilesContent result = await Host(baseline).PackageFilesAsync(await CompareAsync(baseline, baseline),
            new(InstalledPackageEcosystem.NuGet, "example", "1", "2"), Token);

        Assert.Null(result.Files);
        Assert.Contains("unavailable", result.Message);
        Assert.All(result.Warnings!, warning => Assert.Contains("does not establish deployed file ownership", warning));
    }

    [Fact]
    public async Task OwnershipCancellationIsNotReturnedAsPartialSuccess()
    {
        TestImage baseline = await CreateAsync(Blob(("var/lib/dpkg/info/example.list", "/file")));
        baseline.Client.Setup(c => c.Blobs.GetRangeAsync(Image.Repo, It.IsAny<string>(),
            0, It.IsAny<long?>(), It.IsAny<CancellationToken>())).ThrowsAsync(new OperationCanceledException());
        ExplorerComparison comparison = new(baseline.Session, baseline.Session, 0, [], []);

        await Assert.ThrowsAsync<OperationCanceledException>(() => Host(baseline).PackageFilesAsync(comparison,
            new(InstalledPackageEcosystem.Dpkg, "example", "1", null), Token));
    }

    [Theory]
    [InlineData("1", "2", 2)]
    [InlineData("1", null, 1)]
    [InlineData(null, "2", 1)]
    public async Task PackageOwnershipIncludesApplicableBaselineAndTargetFiles(
        string? baselineVersion, string? targetVersion, int expectedCount)
    {
        TestImage baseline = await CreateAsync(Blob(("var/lib/dpkg/info/example.list", "/old\n/shared\n"),
            ("old", "old"), ("shared", "same")));
        TestImage target = await CreateAsync(Blob(("var/lib/dpkg/info/example.list", "/new\n/shared\n"),
            ("new", "new"), ("shared", "same")));
        ExplorerComparison comparison = await CompareAsync(baseline, target);

        PackageFilesContent result = await Host(baseline).PackageFilesAsync(comparison,
            new(InstalledPackageEcosystem.Dpkg, "example", baselineVersion, targetVersion), Token);

        Assert.Equal(expectedCount, result.Files!.Count);
        Assert.Equal(expectedCount + 1, result.Total);
        if (baselineVersion is not null)
        {
            Assert.Contains(("old", Change.Removed), result.Files);
        }
        if (targetVersion is not null)
        {
            Assert.Contains(("new", Change.Added), result.Files);
        }
    }

    [Fact]
    public async Task PackageDiagnosticsAreStructuredAndRetainedAcrossCachedReads()
    {
        TestImage image = await CreateAsync(Archive(
            ("node_modules/good/package.json", Encoding.UTF8.GetBytes("""{"name":"good","version":"1"}""")),
            ("node_modules/bad/package.json", Encoding.UTF8.GetBytes("{")),
            ("node_modules/invalid/package.json", [0xff]),
            ("node_modules/large/package.json", new byte[InstalledPackageReader.MaxPackageManifestBytes + 1])));

        await image.Session.EnsurePackagesAsync(Token);
        InstalledPackageMetadata metadata = image.Session.Packages;
        Assert.Equal(["good"], metadata.Ecosystems[InstalledPackageEcosystem.Npm].Packages.Keys);
        Assert.Equal("good", Assert.Single(metadata.NpmPackageRoots).Key);
        Assert.Equal(["node_modules/bad/package.json", "node_modules/invalid/package.json", "node_modules/large/package.json"],
            metadata.Diagnostics.Select(item => item.Path).Order(StringComparer.Ordinal));
        Assert.All(metadata.Diagnostics, item => Assert.NotEmpty(item.Message));
        await image.Session.EnsurePackagesAsync(Token);
        Assert.Same(metadata, image.Session.Packages);
    }

    internal async Task<(ExplorerImage Image, IReadOnlyList<TagChoice> Choices)> PickerChoicesAsync()
    {
        byte[] shared = Blob(("base", "shared"));
        TestImage baseline = await CreateAsync([shared], null, baseLayerCount: 1);
        TestImage compatible = await CreateAsync(shared, Blob(("app", "target")));
        TestImage unrelated = await CreateAsync(Blob(("other", "unrelated")));
        baseline.Client.Setup(c => c.Manifests.GetAsync(Image.Repo, "compatible", It.IsAny<CancellationToken>()))
            .ReturnsAsync(compatible.Source.Resolved.ManifestInfo);
        baseline.Client.Setup(c => c.Manifests.GetAsync(Image.Repo, "unrelated", It.IsAny<CancellationToken>()))
            .ReturnsAsync(unrelated.Source.Resolved.ManifestInfo);
        ExplorerHost host = Host(baseline);
        TagChoice[] choices = [new("compatible"), new("current"), new("unrelated")];
        foreach (TagChoice choice in choices)
        {
            await host.DescribeTagAsync(choice, Token);
        }
        return (ExplorerImage.FromSource(baseline.Source), choices);
    }

    [Fact]
    public async Task ChangedSymbolicLinksDiffTheirTargetsWithoutDereferencing()
    {
        TestImage baseline = await CreateAsync(ArchiveEntries(
            new PaxTarEntry(TarEntryType.SymbolicLink, "current") { LinkName = "old-target" }));
        TestImage target = await CreateAsync(ArchiveEntries(
            new PaxTarEntry(TarEntryType.SymbolicLink, "current") { LinkName = "new-target" }));

        TextDiffContent diff = await Host(baseline).DiffAsync(await CompareAsync(baseline, target), "current", Token);

        Assert.Equal([new DiffLine(DiffOp.Delete, 1, null, "old-target"),
            new DiffLine(DiffOp.Insert, null, 1, "new-target")], diff.Lines);
        Assert.Contains("Symbolic link target", diff.Message);
    }

    [Theory]
    [InlineData(TarEntryType.RegularFile)]
    [InlineData(TarEntryType.SymbolicLink)]
    public async Task ChangedHardLinksDiffTheirCapturedContents(TarEntryType originalType)
    {
        byte[] Layer(string value) => ArchiveEntries(
            originalType == TarEntryType.RegularFile
                ? new PaxTarEntry(originalType, "original") { DataStream = new MemoryStream(Encoding.UTF8.GetBytes(value)) }
                : new PaxTarEntry(originalType, "original") { LinkName = value },
            new PaxTarEntry(TarEntryType.HardLink, "saved") { LinkName = "original" });
        TestImage baseline = await CreateAsync(Layer("old"), Blob((".wh.original", "")));
        TestImage target = await CreateAsync(Layer("new"), Blob((".wh.original", "")));
        ExplorerComparison comparison = await CompareAsync(baseline, target);
        Assert.Equal("saved", Assert.Single(comparison.Files).Path);

        TextDiffContent diff = await Host(baseline).DiffAsync(comparison, "saved", Token);

        Assert.Equal([new DiffLine(DiffOp.Delete, 1, null, "old"),
            new DiffLine(DiffOp.Insert, null, 1, "new")], diff.Lines);
    }

    [Fact]
    public async Task DiffExplainsFileTypeChangesEvenWhenTextMatches()
    {
        TestImage baseline = await CreateAsync(Blob(("current", "target")));
        TestImage target = await CreateAsync(ArchiveEntries(
            new PaxTarEntry(TarEntryType.SymbolicLink, "current") { LinkName = "target" }));

        TextDiffContent diff = await Host(baseline).DiffAsync(await CompareAsync(baseline, target), "current", Token);

        Assert.Equal(DiffOp.Same, Assert.Single(diff.Lines!).Op);
        Assert.Contains("File -> SymbolicLink", diff.Message);
    }

    [Fact]
    public async Task DiffExplainsHardLinkTargetChangesWithIdenticalContents()
    {
        byte[] files = Blob(("first", "same"), ("second", "same"));
        TestImage baseline = await CreateAsync(files, ArchiveEntries(
            new PaxTarEntry(TarEntryType.HardLink, "saved") { LinkName = "first" }));
        TestImage target = await CreateAsync(files, ArchiveEntries(
            new PaxTarEntry(TarEntryType.HardLink, "saved") { LinkName = "second" }));

        TextDiffContent diff = await Host(baseline).DiffAsync(await CompareAsync(baseline, target), "saved", Token);

        Assert.Equal(DiffOp.Same, Assert.Single(diff.Lines!).Op);
        Assert.Contains("first -> second", diff.Message);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FileDirectoryReplacementDiffRetainsTheFileContents(bool reverse)
    {
        TestImage baseline = await CreateAsync(Blob(("item", "old")));
        TestImage target = await CreateAsync(ArchiveEntries(
            new PaxTarEntry(TarEntryType.Directory, "item"),
            new PaxTarEntry(TarEntryType.RegularFile, "item/child") { DataStream = new MemoryStream("new"u8.ToArray()) }));
        if (reverse)
        {
            (baseline, target) = (target, baseline);
        }

        TextDiffContent diff = await Host(baseline).DiffAsync(await CompareAsync(baseline, target), "item", Token);

        DiffLine line = Assert.Single(diff.Lines!);
        Assert.Equal("old", line.Text);
        Assert.Equal(reverse ? DiffOp.Insert : DiffOp.Delete, line.Op);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PackageOwnershipResolvesEachSidesParentsButPreservesFinalLinks(bool moved)
    {
        byte[] Layer(string root, string text, string link) => ArchiveEntries(
            new PaxTarEntry(TarEntryType.RegularFile, "var/lib/dpkg/info/bash.list")
                { DataStream = new MemoryStream("/bin/bash\n/bin/bash-link\n"u8.ToArray()) },
            new PaxTarEntry(TarEntryType.SymbolicLink, "bin") { LinkName = root },
            new PaxTarEntry(TarEntryType.RegularFile, root + "/bash")
                { DataStream = new MemoryStream(Encoding.UTF8.GetBytes(text)) },
            new PaxTarEntry(TarEntryType.SymbolicLink, root + "/bash-link") { LinkName = link });
        TestImage baseline = await CreateAsync(Layer("usr/bin", "old", "bash"));
        TestImage target = await CreateAsync(Layer(moved ? "opt/bin" : "usr/bin", "new", "other"));

        PackageFilesContent result = await Host(baseline).PackageFilesAsync(await CompareAsync(baseline, target),
            new(InstalledPackageEcosystem.Dpkg, "bash", "1", "2"), Token);

        Assert.Empty(result.Warnings!);
        Assert.Equal(moved ? 4 : 2, result.Total);
        Assert.Equal(result.Total, result.Files!.Count);
        Assert.Contains(("usr/bin/bash", moved ? Change.Removed : Change.Modified), result.Files);
        Assert.Contains(("usr/bin/bash-link", moved ? Change.Removed : Change.Modified), result.Files);
        if (moved)
        {
            Assert.Contains(("opt/bin/bash", Change.Added), result.Files);
            Assert.Contains(("opt/bin/bash-link", Change.Added), result.Files);
        }
    }

    [Fact]
    public async Task UnresolvableOwnershipParentsWarnWithoutDiscardingReadableFiles()
    {
        TestImage baseline = await CreateAsync(ArchiveEntries(
            new PaxTarEntry(TarEntryType.RegularFile, "var/lib/dpkg/info/example.list")
                { DataStream = new MemoryStream("/bin/tool\n/good\n"u8.ToArray()) },
            new PaxTarEntry(TarEntryType.SymbolicLink, "bin") { LinkName = "bin" },
            new PaxTarEntry(TarEntryType.RegularFile, "good") { DataStream = new MemoryStream("old"u8.ToArray()) }));
        TestImage target = await CreateAsync();

        PackageFilesContent result = await Host(baseline).PackageFilesAsync(await CompareAsync(baseline, target),
            new(InstalledPackageEcosystem.Dpkg, "example", "1", null), Token);

        Assert.Equal(("good", Change.Removed), Assert.Single(result.Files!));
        Assert.Contains("Baseline /bin/tool", Assert.Single(result.Warnings!));
        Assert.Contains("incomplete", result.Message);
    }

    [Theory]
    [InlineData("react")]
    [InlineData("@scope/react")]
    public async Task NpmOwnershipUsesAllManifestInstallationRootsIncludingAliases(string name)
    {
        byte[] Layer(string version) => Blob(
            ("app/node_modules/alias/package.json", JsonSerializer.Serialize(new { name, version })),
            ("app/node_modules/alias/index.js", version),
            ("app/node_modules/@aliases/second/package.json", JsonSerializer.Serialize(new { name, version })),
            ("app/node_modules/@aliases/second/index.js", version),
            ("app/node_modules/alias/node_modules/nested/package.json", """{"name":"nested","version":"1"}"""),
            ("app/node_modules/alias/node_modules/nested/index.js", version),
            ("app/node_modules/react/package.json", """{"name":"unrelated","version":"1"}"""),
            ("app/node_modules/react/index.js", version));
        TestImage baseline = await CreateAsync(Layer("1"));
        TestImage target = await CreateAsync(Layer("2"));
        ExplorerComparison comparison = await CompareAsync(baseline, target);
        ExplorerPackageDifference package = Assert.Single(comparison.Packages, package => package.Name == name);

        PackageFilesContent result = await Host(baseline).PackageFilesAsync(comparison, package, Token);

        Assert.Empty(result.Warnings!);
        Assert.NotNull(result.Files);
        Assert.Equal(new[] { "app/node_modules/@aliases/second/index.js", "app/node_modules/@aliases/second/package.json",
            "app/node_modules/alias/index.js", "app/node_modules/alias/package.json" },
            result.Files!.Select(file => file.Path));
        Assert.All(result.Files, file => Assert.Equal(Change.Modified, file.Change));
    }

    [Theory]
    [InlineData("1")]
    [InlineData("2")]
    public async Task PythonInventoryAndComparisonUseCanonicalIdentities(string targetVersion)
    {
        TestImage baseline = await CreateAsync(Blob(
            ("site/friendly_bard-1.dist-info/METADATA", "Name: Friendly_Bard\nVersion: 1\n"),
            ("venv/friendly_bard-1.dist-info/METADATA", "Name: FRIENDLY..BARD\nVersion: 1\n")));
        TestImage target = await CreateAsync(Blob(
            ("site/friendly_bard-1.dist-info/METADATA", $"Name: friendly-bard\nVersion: {targetVersion}\n")));

        ExplorerComparison comparison = await CompareAsync(baseline, target);

        var inventory = baseline.Session.Packages.Ecosystems[InstalledPackageEcosystem.Pip].Packages;
        Assert.Equal("friendly-bard", Assert.Single(inventory).Key);
        Assert.Equal(["1"], inventory["friendly-bard"]);
        if (targetVersion == "1")
        {
            Assert.Empty(comparison.Packages);
        }
        else
        {
            Assert.Equal(new(InstalledPackageEcosystem.Pip, "friendly-bard", "1", "2"),
                Assert.Single(comparison.Packages));
        }
    }

    private Task<TestImage> CreateAsync(params byte[][] blobs) => CreateAsync(blobs, null);

    private async Task<TestImage> CreateAsync(byte[][] blobs, Action<Dictionary<int, StoredLayerIndex>>? customize,
        int? baseLayerCount = null)
    {
        LayerStore store = new(Path.Combine(cachePath, stores.Count.ToString()));
        stores.Add(store);
        Mock<IDockerRegistryClient> client = new() { DefaultValue = DefaultValue.Mock };
        Dictionary<int, StoredLayerIndex> indexes = [];
        for (int i = 0; i < blobs.Length; i++)
        {
            byte[] blob = blobs[i];
            string digest = LayerCacheTestContext.Digest(blob);
            using MemoryStream input = new(blob);
            indexes.Add(i, new(digest, blob.Length,
                await ImageLayerScanner.ScanAsync(input, new(i, digest), Token)));
            client.Setup(c => c.Blobs.GetRangeAsync(Image.Repo, digest, 0,
                It.IsAny<long?>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => new BlobDownloadResult(new MemoryStream(blob), false, null, null, blob.Length));
        }
        customize?.Invoke(indexes);
        OciImageManifest manifest = new()
        {
            Config = new OciDescriptor { Digest = "sha256:config" },
            Layers = indexes.Values.Select(index => new OciDescriptor
            {
                Digest = index.Digest, Size = index.BlobLength
            }).ToArray()
        };
        ManifestInfo info = new("application/vnd.oci.image.manifest.v1+json",
            LayerCacheTestContext.Digest(JsonSerializer.SerializeToUtf8Bytes(manifest)), manifest);
        client.Setup(c => c.Manifests.GetAsync(Image.Repo, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(info);
        client.Setup(c => c.Blobs.GetAsync(Image.Repo, "sha256:config", It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new MemoryStream(Encoding.UTF8.GetBytes("""{"os":"linux","architecture":"amd64"}""")));
        ExplorerSource source = new()
        {
            Image = Image, Resolved = new(info, manifest),
            Config = new Image { Os = "linux", Architecture = "amd64" },
            BaseImages = baseLayerCount is int count ? [new("registry.test/base:1", count)] : [],
            Platforms = [], Platform = new("linux", "amd64", null, null)
        };
        ExplorerSession session = await ExplorerSession.CreateAsync(client.Object, source, store, indexes, Token);
        return new(session, source, client, store);
    }

    private ExplorerHost Host(TestImage image, IDockerRegistryClientFactory? factory = null)
    {
        ExplorerHost host = new(image.Client.Object, factory ?? Mock.Of<IDockerRegistryClientFactory>(),
            image.Source, image.Store, ExplorerImage.FromSource(image.Source),
            new ExplorerLayerIndexer(0, (_, _, _) => throw new InvalidOperationException()),
            new ExplorerOptions(null, null, false, ClipboardMode.Off, KeyMap.Default, "", ""), cachePath);
        host.Session = image.Session;
        hosts.Add(host);
        return host;
    }

    private static async Task<ExplorerComparison> CompareAsync(TestImage baseline, TestImage target)
    {
        await baseline.Session.EnsurePackagesAsync(Token);
        await target.Session.EnsurePackagesAsync(Token);
        return ExplorerSession.Compare(baseline.Session, target.Session);
    }

    private static byte[] Blob(params (string Path, string Content)[] files) =>
        Archive(files.Select(file => (file.Path, Encoding.UTF8.GetBytes(file.Content))).ToArray());

    private static byte[] Archive(params (string Path, byte[] Content)[] files) =>
        ArchiveEntries(files.Select(file => new PaxTarEntry(TarEntryType.RegularFile, file.Path)
            { DataStream = new MemoryStream(file.Content) }).ToArray());

    private static byte[] ArchiveEntries(params TarEntry[] entries)
    {
        using MemoryStream result = new();
        using (GZipStream gzip = new(result, CompressionMode.Compress, leaveOpen: true))
        using (TarWriter writer = new(gzip, leaveOpen: true))
        {
            foreach (TarEntry entry in entries)
            {
                using Stream? data = entry.DataStream;
                writer.WriteEntry(entry);
            }
        }
        return result.ToArray();
    }

    public async ValueTask DisposeAsync()
    {
        foreach (ExplorerHost host in hosts)
        {
            await host.DisposeAsync();
        }
        foreach (LayerStore store in stores)
        {
            await store.DisposeAsync();
        }
        if (Directory.Exists(cachePath))
        {
            Directory.Delete(cachePath, recursive: true);
        }
    }

    private sealed record TestImage(ExplorerSession Session, ExplorerSource Source,
        Mock<IDockerRegistryClient> Client, LayerStore Store);
}

[Collection(ExplorerUiCollection.Name)]
public sealed class ExplorerViewerHandoffTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void AbandonedViewerResultsAreDeletedEvenWithoutDispatchingQueuedCallbacks(bool queued, bool dispose)
    {
        string file = CreateStage();
        try
        {
            using ExplorerUiHarness ui = ExplorerWindowTests.Open(out _);
            TaskCompletionSource<StagedViewerFile> pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
            TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);
            bool delivered = false;
            Task work = ui.Window.RunAsync(_ =>
            {
                started.SetResult();
                return pending.Task;
            }, _ => delivered = true, abandoned: staged => staged.Dispose());
            Assert.True(SpinWait.SpinUntil(() => started.Task.IsCompleted, TimeSpan.FromSeconds(10)));
            if (queued)
            {
                pending.SetResult(new(file));
                Assert.True(SpinWait.SpinUntil(() => work.IsCompleted, TimeSpan.FromSeconds(10)));
            }
            if (dispose)
            {
                ui.Window.Dispose();
            }
            else
            {
                ui.Window.Apply(new Quit());
            }
            if (!queued)
            {
                pending.SetResult(new(file));
                Assert.True(SpinWait.SpinUntil(() => work.IsCompleted, TimeSpan.FromSeconds(10)));
            }

            Assert.False(delivered);
            Assert.False(File.Exists(file));
            Assert.False(Directory.Exists(Path.GetDirectoryName(file)));
        }
        finally
        {
            ExplorerApp.TryDelete(file);
        }
    }

    [Fact]
    public void AcceptedWindowedViewerRetainsItsFileWhenExplorerCloses()
    {
        string file = CreateStage();
        try
        {
            using ExplorerUiHarness ui = ExplorerWindowTests.Open(out _);
            string? accepted = null;
            ui.Window.RunAsync(_ => Task.FromResult(new StagedViewerFile(file)),
                staged => accepted = staged.TryTake(), abandoned: staged => staged.Dispose());
            ui.Until(() => accepted is not null, "the viewer handoff");

            ui.Window.Apply(new Quit());
            ui.Window.Dispose();

            Assert.Equal(file, accepted);
            Assert.True(File.Exists(file));
            ExplorerApp.TryDelete(accepted!);
            Assert.False(File.Exists(file));
        }
        finally
        {
            ExplorerApp.TryDelete(file);
        }
    }

    private static string CreateStage()
    {
        string directory = Path.Combine(Directory.GetCurrentDirectory(), "viewer-handoff-" + Guid.NewGuid().ToString("N"));
        CacheFileSystem.CreateDirectory(directory);
        string file = Path.Combine(directory, "image.txt");
        using FileStream output = CacheFileSystem.CreateFile(file);
        output.Write("private bytes"u8);
        return file;
    }
}
