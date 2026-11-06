namespace Valleysoft.Dredge.Tests;

using System.Text;
using System.Text.Json;
using System.CommandLine;
using System.Formats.Tar;
using Spectre.Console;
using Valleysoft.DockerRegistryClient.Models.Images;
using Valleysoft.DockerRegistryClient.Models.Manifests;
using Valleysoft.DockerRegistryClient.Models.Manifests.Docker;
using Valleysoft.Dredge.Commands.Image;
using Valleysoft.Dredge.Commands;

public class OsCommandTests
{
    [Theory]
    [InlineData("file-parent", "non-directory parent")]
    [InlineData("invalid-target", "invalid target")]
    [InlineData("parent-cycle", "exceeded")]
    public async Task LinuxOsDoesNotIgnoreInvalidUnrelatedHardLinks(string invalidTarget, string error)
    {
        await using LayerCacheTestContext cache = new();
        byte[] lower = WindowsImageTestData.Layer(WindowsImageTestData.File("base", "unrelated"));
        List<TarEntry> upperEntries = [WindowsImageTestData.File("etc/os-release", "NAME=ReportedLinux\n")];
        if (invalidTarget == "file-parent")
        {
            upperEntries.Add(WindowsImageTestData.File("payload", "not a directory"));
        }
        else if (invalidTarget == "parent-cycle")
        {
            upperEntries.Add(new PaxTarEntry(TarEntryType.SymbolicLink, "payload") { LinkName = "payload" });
        }
        upperEntries.Add(new PaxTarEntry(TarEntryType.HardLink, "app/alias")
            { LinkName = invalidTarget == "invalid-target" ? "bad\\name" : "payload/file" });
        byte[] upper = WindowsImageTestData.Layer([.. upperEntries]);
        Mock<IDockerRegistryClient> client = WindowsImageTestData.Client("linux", "", lower, upper);
        Mock<IDockerRegistryClientFactory> factory = new();
        factory.Setup(item => item.GetClientAsync(WindowsImageTestData.Name.Registry, It.IsAny<CancellationToken>()))
            .ReturnsAsync(client.Object);
        using StringWriter output = new();
        using StringWriter errors = new();
        CapturedOsCommand command = new(factory.Object, output, errors, cache.Paths);
        ((IProcessTerminationAware)command).ProcessTerminator = new TestProcessTerminator();

        int exitCode = await command.Parse([WindowsImageTestData.Name.ToString()])
            .InvokeAsync(new InvocationConfiguration(), TestContext.Current.CancellationToken);

        Assert.Equal(1, exitCode);
        Assert.Equal("", output.ToString());
        Assert.Contains(error, errors.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(ImageFileType.File)]
    [InlineData(ImageFileType.Directory)]
    public async Task DeferredHardLinkRemovesReplacedEntries(ImageFileType previousType)
    {
        await using LayerCacheTestContext cache = new();
        Dictionary<string, ImageFileSystemEntry> entries = new(StringComparer.Ordinal)
        {
            ["release"] = new()
            {
                Path = "release",
                Type = previousType,
                Size = previousType == ImageFileType.File ? 1 : 0,
                IntroducedLayer = new(0, "base")
            }
        };
        if (previousType == ImageFileType.Directory)
        {
            entries.Add("release/obsolete", new()
            {
                Path = "release/obsolete",
                Type = ImageFileType.File,
                IntroducedLayer = new(0, "base")
            });
        }
        Dictionary<string, ImageFileSystemEntry> deletedEntries = new(StringComparer.Ordinal);
        ImageFileSystemBuilder builder = new(
            new RegistryImageContentSource(Mock.Of<IDockerRegistryClient>(), WindowsImageTestData.Name),
            WindowsImageTestData.Name, new DockerManifest(), cache.Store, [], entries, deletedEntries);

        builder.ApplyLayer(new([new("release", ImageFileType.HardLink, 0, 0, 0, 0,
            DateTime.UnixEpoch, "original", 0, 0, 0, null)], [], []), new(1, "layer"),
            TestContext.Current.CancellationToken, deferUnresolvedHardLinks: true);

        Assert.Empty(entries);
        Assert.Equal(previousType, deletedEntries["release"].Type);
        if (previousType == ImageFileType.Directory)
        {
            Assert.Contains("release/obsolete", deletedEntries.Keys);
        }
    }

    [Fact]
    public async Task DeferringMissingHardLinksDoesNotScanEveryExistingEntry()
    {
        const int count = 50_000;
        await using LayerCacheTestContext cache = new();
        Dictionary<string, ImageFileSystemEntry> entries = new(StringComparer.Ordinal);
        for (int index = 0; index < count; index++)
        {
            string path = $"file{index}";
            entries.Add(path, new ImageFileSystemEntry
            {
                Path = path,
                Type = ImageFileType.File,
                IntroducedLayer = new(0, "base")
            });
        }
        LayerChanges changes = new([.. Enumerable.Range(0, count).Select(index =>
            new ScannedEntry($"alias{index}", ImageFileType.HardLink, 0, 0, 0, 0,
                DateTime.UnixEpoch, "original", 0, 0, 0, null))], [], []);
        ImageFileSystemBuilder builder = new(
            new RegistryImageContentSource(Mock.Of<IDockerRegistryClient>(), WindowsImageTestData.Name),
            WindowsImageTestData.Name, new DockerManifest(), cache.Store, [], entries, new(StringComparer.Ordinal));

        System.Diagnostics.Stopwatch elapsed = System.Diagnostics.Stopwatch.StartNew();
        builder.ApplyLayer(changes, new(1, "layer"), TestContext.Current.CancellationToken,
            deferUnresolvedHardLinks: true);
        elapsed.Stop();

        Assert.Equal(count, entries.Count);
        Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(5),
            $"Deferring {count} hardlinks among {count} existing files took {elapsed.Elapsed}.");
    }

    [Theory]
    [InlineData("available")]
    [InlineData("unavailable")]
    [InlineData("missing")]
    public async Task LinuxOsDoesNotExposeReplacedContentWhileReleaseHardLinkIsUnresolved(string targetState)
    {
        await using LayerCacheTestContext cache = new();
        List<TarEntry> baseEntries = [WindowsImageTestData.File("usr/lib/os-release", "NAME=FallbackLinux\n")];
        if (targetState != "missing")
        {
            baseEntries.Add(WindowsImageTestData.File("opt/os-release", "NAME=NewLinux\n"));
        }
        byte[] lower = WindowsImageTestData.Layer([.. baseEntries]);
        byte[] older = WindowsImageTestData.Layer(WindowsImageTestData.File("app/older", "unrelated"));
        byte[] previous = WindowsImageTestData.Layer(WindowsImageTestData.File("etc/os-release", "NAME=OldLinux\n"));
        byte[] middle = WindowsImageTestData.Layer(WindowsImageTestData.File("app/middle", "unrelated"));
        byte[] upper = WindowsImageTestData.Layer(new PaxTarEntry(TarEntryType.HardLink, "etc/os-release")
            { LinkName = "opt/os-release" });
        Mock<IDockerRegistryClient> client = WindowsImageTestData.Client("linux", "", lower, older, previous, middle, upper);
        if (targetState == "unavailable")
        {
            client.Setup(item => item.Blobs.GetRangeAsync(WindowsImageTestData.Name.Repo,
                LayerCacheTestContext.Digest(lower), 0, It.IsAny<long?>(), It.IsAny<CancellationToken>()))
                .ThrowsAsync(new IOException("Required release target unavailable."));
        }
        Mock<IDockerRegistryClientFactory> factory = new();
        factory.Setup(item => item.GetClientAsync(WindowsImageTestData.Name.Registry, It.IsAny<CancellationToken>()))
            .ReturnsAsync(client.Object);
        using StringWriter output = new();
        using StringWriter errors = new();
        CapturedOsCommand command = new(factory.Object, output, errors, cache.Paths);
        ((IProcessTerminationAware)command).ProcessTerminator = new TestProcessTerminator();

        int exitCode = await command.Parse([WindowsImageTestData.Name.ToString()])
            .InvokeAsync(new InvocationConfiguration(), TestContext.Current.CancellationToken);

        if (targetState == "available")
        {
            Assert.Equal(0, exitCode);
            using JsonDocument document = JsonDocument.Parse(output.ToString());
            Assert.Equal("NewLinux", document.RootElement.GetProperty("NAME").GetString());
        }
        else
        {
            Assert.Equal(1, exitCode);
            Assert.Equal("", output.ToString());
            Assert.Contains(targetState == "missing" ? "missing path" : "Required release target unavailable",
                errors.ToString(), StringComparison.OrdinalIgnoreCase);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LinuxOsIgnoresUnrelatedHardLinkDependenciesInPartialSuffix(bool parentDependency)
    {
        await using LayerCacheTestContext cache = new();
        byte[] lower = WindowsImageTestData.Layer(WindowsImageTestData.File(
            parentDependency ? "payload/original" : "original", "unavailable"));
        byte[] dependency = WindowsImageTestData.Layer(new PaxTarEntry(TarEntryType.HardLink, "app/alias")
            { LinkName = parentDependency ? "payload/original" : "original" });
        byte[] release = WindowsImageTestData.Layer(WindowsImageTestData.File("etc/os-release", "NAME=ResolvedLinux\n"));
        byte[] middle = WindowsImageTestData.Layer(WindowsImageTestData.File("app/middle", "unrelated"));
        byte[] upper = WindowsImageTestData.Layer(WindowsImageTestData.File("app/upper", "unrelated"));
        Mock<IDockerRegistryClient> client = WindowsImageTestData.Client("linux", "", lower, dependency, release, middle, upper);
        client.Setup(item => item.Blobs.GetRangeAsync(WindowsImageTestData.Name.Repo,
            LayerCacheTestContext.Digest(lower), 0, It.IsAny<long?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new IOException("Unneeded hardlink target layer unavailable."));
        Mock<IDockerRegistryClientFactory> factory = new();
        factory.Setup(item => item.GetClientAsync(WindowsImageTestData.Name.Registry, It.IsAny<CancellationToken>()))
            .ReturnsAsync(client.Object);
        using StringWriter output = new();
        using StringWriter errors = new();
        CapturedOsCommand command = new(factory.Object, output, errors, cache.Paths);
        ((IProcessTerminationAware)command).ProcessTerminator = new TestProcessTerminator();

        Assert.Equal(0, await command.Parse([WindowsImageTestData.Name.ToString()])
            .InvokeAsync(new InvocationConfiguration(), TestContext.Current.CancellationToken));

        using JsonDocument document = JsonDocument.Parse(output.ToString());
        Assert.Equal("ResolvedLinux", document.RootElement.GetProperty("NAME").GetString());
        client.Verify(item => item.Blobs.GetRangeAsync(WindowsImageTestData.Name.Repo,
            LayerCacheTestContext.Digest(lower), It.IsAny<long>(), It.IsAny<long?>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    [InlineData(false, false, true)]
    [InlineData(true, false, true)]
    public async Task LinuxOsUsesDeferredSuffixBeforeFailingOnUnneededLowerLayer(
        bool linked, bool missingBlob, bool malformed = false)
    {
        await using LayerCacheTestContext cache = new();
        byte[] lower = WindowsImageTestData.Layer(WindowsImageTestData.File("unrelated/base", "unavailable"));
        byte[] release = WindowsImageTestData.Layer(
            WindowsImageTestData.File(linked ? "opt/os-release" : "etc/os-release", "NAME=ResolvedLinux\n"));
        byte[] middle = WindowsImageTestData.Layer(WindowsImageTestData.File("app/middle", "unrelated"));
        byte[] upper = linked
            ? WindowsImageTestData.Layer(new PaxTarEntry(TarEntryType.SymbolicLink, "etc/os-release")
                { LinkName = "/opt/os-release" })
            : WindowsImageTestData.Layer(WindowsImageTestData.File("app/upper", "unrelated"));
        Mock<IDockerRegistryClient> client = WindowsImageTestData.Client("linux", "", lower, release, middle, upper);
        client.Setup(item => item.Blobs.GetRangeAsync(WindowsImageTestData.Name.Repo,
            LayerCacheTestContext.Digest(lower), 0, It.IsAny<long?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(malformed ? new InvalidDataException("Unneeded lower blob malformed.") :
                missingBlob ? new FileNotFoundException("Unneeded lower blob unavailable.") :
                    new IOException("Unneeded lower blob unavailable."));
        Mock<IDockerRegistryClientFactory> factory = new();
        factory.Setup(item => item.GetClientAsync(WindowsImageTestData.Name.Registry, It.IsAny<CancellationToken>()))
            .ReturnsAsync(client.Object);
        using StringWriter output = new();
        using StringWriter errors = new();
        CapturedOsCommand command = new(factory.Object, output, errors, cache.Paths);
        ((IProcessTerminationAware)command).ProcessTerminator = new TestProcessTerminator();

        Assert.Equal(0, await command.Parse([WindowsImageTestData.Name.ToString()])
            .InvokeAsync(new InvocationConfiguration(), TestContext.Current.CancellationToken));

        using JsonDocument document = JsonDocument.Parse(output.ToString());
        Assert.Equal("ResolvedLinux", document.RootElement.GetProperty("NAME").GetString());
        Assert.Equal("", errors.ToString());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LinuxOsDoesNotRecoverWithUnprovenPreferredPath(bool cycle)
    {
        await using LayerCacheTestContext cache = new();
        byte[] lower = WindowsImageTestData.Layer(WindowsImageTestData.File("etc/os-release", "NAME=PreferredLinux\n"));
        byte[] middle = WindowsImageTestData.Layer(WindowsImageTestData.File("app/middle", "unrelated"));
        byte[] older = WindowsImageTestData.Layer(WindowsImageTestData.File("app/older", "unrelated"));
        List<TarEntry> upperEntries = [WindowsImageTestData.File("usr/lib/os-release", "NAME=FallbackLinux\n")];
        if (cycle)
        {
            upperEntries.Add(new PaxTarEntry(TarEntryType.SymbolicLink, "etc/os-release") { LinkName = "os-release" });
        }
        byte[] upper = WindowsImageTestData.Layer([.. upperEntries]);
        Mock<IDockerRegistryClient> client = WindowsImageTestData.Client("linux", "", lower, older, middle, upper);
        client.Setup(item => item.Blobs.GetRangeAsync(WindowsImageTestData.Name.Repo,
            LayerCacheTestContext.Digest(lower), 0, It.IsAny<long?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new IOException("Required lower blob unavailable."));
        Mock<IDockerRegistryClientFactory> factory = new();
        factory.Setup(item => item.GetClientAsync(WindowsImageTestData.Name.Registry, It.IsAny<CancellationToken>()))
            .ReturnsAsync(client.Object);
        using StringWriter output = new();
        using StringWriter errors = new();
        CapturedOsCommand command = new(factory.Object, output, errors, cache.Paths);
        ((IProcessTerminationAware)command).ProcessTerminator = new TestProcessTerminator();

        Assert.Equal(1, await command.Parse([WindowsImageTestData.Name.ToString()])
            .InvokeAsync(new InvocationConfiguration(), TestContext.Current.CancellationToken));

        Assert.Equal("", output.ToString());
        Assert.Contains("Required lower blob unavailable", errors.ToString());
    }

    [Theory]
    [InlineData(false, 32)]
    [InlineData(true, 32)]
    [InlineData(false, 33)]
    [InlineData(true, 100)]
    [InlineData(false, 100, true)]
    [InlineData(true, 100, true)]
    public async Task ReleaseCandidateLookupBoundsLayerReplay(bool fallback, int layerCount, bool unrelatedHardLinks = false)
    {
        int applications = 0;
        DockerManifest manifest = new()
        {
            Layers = [.. Enumerable.Range(0, layerCount).Select(index =>
                new ManifestLayer { Digest = $"sha256:{index:D64}", Size = 1 })]
        };
        Dictionary<int, StoredLayerIndex> indexes = [];
        for (int index = 0; index < layerCount; index++)
        {
            string path = index == 0
                ? fallback ? "usr/lib/os-release" : "etc/os-release"
                : fallback && index == layerCount - 1 ? "etc/os-release" : $"app/file{index}";
            bool hardLink = unrelatedHardLinks && index > 0 && path.StartsWith("app/", StringComparison.Ordinal);
            ScannedEntry entry = new(path, hardLink ? ImageFileType.HardLink : ImageFileType.File, 0, 0, 0,
                fallback && index == layerCount - 1 ? 0 : 1, DateTime.UnixEpoch,
                hardLink ? fallback ? "usr/lib/os-release" : "etc/os-release" : null, 0, 0, 0, null);
            indexes.Add(index, new(manifest.Layers[index].Digest!, 1, new([], [], []), "tar")
            {
                Changes = new(new CountingEntries([entry], () => applications++), [], [])
            });
        }
        await using LayerCacheTestContext cache = new();
        Dictionary<string, ImageFileSystemEntry> entries = new(StringComparer.Ordinal);
        ImageFileSystemBuilder builder = new(
            new RegistryImageContentSource(Mock.Of<IDockerRegistryClient>(), WindowsImageTestData.Name),
            WindowsImageTestData.Name, manifest, cache.Store, indexes, entries, new(StringComparer.Ordinal));

        await builder.BuildAsync(null, false, TestContext.Current.CancellationToken,
            contentCandidates: ["etc/os-release", "usr/lib/os-release"]);

        string selectedPath = fallback ? "usr/lib/os-release" : "etc/os-release";
        Assert.Equal(1, new ImagePathResolver(entries).ResolveContentEntry(selectedPath, entries).Size);
        Assert.InRange(applications, layerCount, layerCount * 3);
        if (layerCount == 32)
        {
            Assert.Equal(63, applications);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LinuxOsPreservesReleasePreferenceAcrossSuffixes(bool emptyPreferred)
    {
        await using LayerCacheTestContext cache = new();
        byte[] lower = WindowsImageTestData.Layer(
            WindowsImageTestData.File("etc/os-release", "NAME=PreferredLinux\n"));
        List<byte[]> layers = [lower];
        for (int index = 0; index < 6; index++)
        {
            layers.Add(WindowsImageTestData.Layer(WindowsImageTestData.File($"app/file{index}", "unrelated")));
        }
        List<TarEntry> upperEntries = [WindowsImageTestData.File("usr/lib/os-release", "NAME=FallbackLinux\n")];
        if (emptyPreferred)
        {
            upperEntries.Add(WindowsImageTestData.File("etc/os-release", ""));
        }
        layers.Add(WindowsImageTestData.Layer([.. upperEntries]));
        Mock<IDockerRegistryClient> client = WindowsImageTestData.Client("linux", "", [.. layers]);
        Mock<IDockerRegistryClientFactory> factory = new();
        factory.Setup(item => item.GetClientAsync(WindowsImageTestData.Name.Registry, It.IsAny<CancellationToken>()))
            .ReturnsAsync(client.Object);
        using StringWriter output = new();
        using StringWriter errors = new();
        CapturedOsCommand command = new(factory.Object, output, errors, cache.Paths);
        ((IProcessTerminationAware)command).ProcessTerminator = new TestProcessTerminator();

        Assert.Equal(0, await command.Parse([WindowsImageTestData.Name.ToString()])
            .InvokeAsync(new InvocationConfiguration(), TestContext.Current.CancellationToken));

        using JsonDocument document = JsonDocument.Parse(output.ToString());
        Assert.Equal(emptyPreferred ? "FallbackLinux" : "PreferredLinux", document.RootElement.GetProperty("NAME").GetString());
        if (emptyPreferred)
        {
            client.Verify(item => item.Blobs.GetRangeAsync(WindowsImageTestData.Name.Repo,
                LayerCacheTestContext.Digest(lower), It.IsAny<long>(), It.IsAny<long?>(),
                It.IsAny<CancellationToken>()), Times.Never);
        }
    }

    private sealed class CountingEntries(IReadOnlyList<ScannedEntry> entries, Action enumerated) : IReadOnlyList<ScannedEntry>
    {
        public int Count => entries.Count;
        public ScannedEntry this[int index] => entries[index];
        public IEnumerator<ScannedEntry> GetEnumerator()
        {
            enumerated();
            return entries.GetEnumerator();
        }
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LinuxOsDoesNotDownloadShadowedLowerLayers(bool linked)
    {
        await using LayerCacheTestContext cache = new();
        byte[] lower = WindowsImageTestData.Layer(
            WindowsImageTestData.File("etc/os-release", "NAME=OldLinux\n"),
            WindowsImageTestData.File("unrelated/large", "unneeded"));
        byte[] upper = linked
            ? WindowsImageTestData.Layer(
                WindowsImageTestData.File("opt/os-release", "NAME=NewLinux\n"),
                new PaxTarEntry(TarEntryType.SymbolicLink, "etc/os-release") { LinkName = "/opt/os-release" })
            : WindowsImageTestData.Layer(WindowsImageTestData.File("etc/os-release", "NAME=NewLinux\n"));
        Mock<IDockerRegistryClient> client = WindowsImageTestData.Client("linux", "", lower, upper);
        Mock<IDockerRegistryClientFactory> factory = new();
        factory.Setup(item => item.GetClientAsync(WindowsImageTestData.Name.Registry, It.IsAny<CancellationToken>()))
            .ReturnsAsync(client.Object);
        using StringWriter output = new();
        using StringWriter errors = new();
        CapturedOsCommand command = new(factory.Object, output, errors, cache.Paths);
        ((IProcessTerminationAware)command).ProcessTerminator = new TestProcessTerminator();

        Assert.Equal(0, await command.Parse([WindowsImageTestData.Name.ToString()])
            .InvokeAsync(new InvocationConfiguration(), TestContext.Current.CancellationToken));

        using JsonDocument document = JsonDocument.Parse(output.ToString());
        Assert.Equal("NewLinux", document.RootElement.GetProperty("NAME").GetString());
        client.Verify(item => item.Blobs.GetRangeAsync(WindowsImageTestData.Name.Repo,
            LayerCacheTestContext.Digest(lower), It.IsAny<long>(), It.IsAny<long?>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [InlineData("relative")]
    [InlineData("absolute")]
    [InlineData("hard")]
    [InlineData("hard-to-symlink")]
    [InlineData("parent-symlink")]
    [InlineData("fallback-parent-symlink")]
    public async Task LinuxOsFollowsEffectiveReleaseLinks(string linkKind)
    {
        await using LayerCacheTestContext cache = new();
        byte[] lower = DockerArchiveFixture.BuildTar(
            ("etc/os-release", "NAME=OldLinux\n"u8.ToArray()),
            ("usr/lib/os-release", "NAME=FallbackLinux\n"u8.ToArray()));
        string targetPath = linkKind == "parent-symlink" ? "opt/etc/os-release" : "opt/os-release";
        List<TarEntry> entries = [WindowsImageTestData.File(targetPath, "NAME=NewLinux\n")];
        if (linkKind == "hard-to-symlink")
        {
            entries.Add(new PaxTarEntry(TarEntryType.SymbolicLink, "opt/alias")
            {
                LinkName = "/opt/os-release"
            });
        }
        entries.Add(linkKind switch
        {
            "hard" => new PaxTarEntry(TarEntryType.HardLink, "etc/os-release") { LinkName = "opt/os-release" },
            "hard-to-symlink" => new PaxTarEntry(TarEntryType.HardLink, "etc/os-release") { LinkName = "opt/alias" },
            "parent-symlink" => new PaxTarEntry(TarEntryType.SymbolicLink, "etc") { LinkName = "/opt/etc" },
            "fallback-parent-symlink" => new PaxTarEntry(TarEntryType.SymbolicLink, "usr/lib") { LinkName = "/opt" },
            _ => new PaxTarEntry(TarEntryType.SymbolicLink, "etc/os-release")
            {
                LinkName = linkKind == "absolute" ? "/opt/os-release" : "../opt/os-release"
            }
        });
        if (linkKind == "fallback-parent-symlink")
        {
            entries.Add(WindowsImageTestData.File("etc/.wh.os-release", ""));
        }
        byte[] upper = WindowsImageTestData.Layer([.. entries]);
        Mock<IDockerRegistryClient> client = WindowsImageTestData.Client("linux", "", lower, upper);
        Mock<IDockerRegistryClientFactory> factory = new();
        factory.Setup(item => item.GetClientAsync(WindowsImageTestData.Name.Registry, It.IsAny<CancellationToken>()))
            .ReturnsAsync(client.Object);
        for (int run = 0; run < 2; run++)
        {
            if (run == 1)
            {
                await using ImageFileSystem fullView = await ImageFileSystem.CreateAsync(
                    client.Object, WindowsImageTestData.Name, new(), TestContext.Current.CancellationToken, cache.Store);
            }
            using StringWriter output = new();
            using StringWriter errors = new();
            CapturedOsCommand command = new(factory.Object, output, errors, cache.Paths);
            ((IProcessTerminationAware)command).ProcessTerminator = new TestProcessTerminator();

            Assert.Equal(0, await command.Parse([WindowsImageTestData.Name.ToString()])
                .InvokeAsync(new InvocationConfiguration(), TestContext.Current.CancellationToken));

            using JsonDocument document = JsonDocument.Parse(output.ToString());
            Assert.Equal("NewLinux", document.RootElement.GetProperty("NAME").GetString());
        }
    }

    [Theory]
    [InlineData("missing", "FallbackLinux", null)]
    [InlineData("empty", "FallbackLinux", null)]
    [InlineData("cycle", null, "link")]
    public async Task LinuxOsDistinguishesMissingOrEmptyLinkedContentFromInvalidLinks(
        string target, string? expected, string? error)
    {
        await using LayerCacheTestContext cache = new();
        List<TarEntry> entries =
        [
            WindowsImageTestData.File("usr/lib/os-release", "NAME=FallbackLinux\n"),
            new PaxTarEntry(TarEntryType.SymbolicLink, "etc/os-release")
            {
                LinkName = target == "cycle" ? "os-release" : "/opt/os-release"
            }
        ];
        if (target == "empty")
        {
            entries.Add(WindowsImageTestData.File("opt/os-release", ""));
        }
        Mock<IDockerRegistryClient> client =
            WindowsImageTestData.Client("linux", "", WindowsImageTestData.Layer([.. entries]));
        Mock<IDockerRegistryClientFactory> factory = new();
        factory.Setup(item => item.GetClientAsync(WindowsImageTestData.Name.Registry, It.IsAny<CancellationToken>()))
            .ReturnsAsync(client.Object);
        using StringWriter output = new();
        using StringWriter errors = new();
        CapturedOsCommand command = new(factory.Object, output, errors, cache.Paths);
        ((IProcessTerminationAware)command).ProcessTerminator = new TestProcessTerminator();

        int exitCode = await command.Parse([WindowsImageTestData.Name.ToString()])
            .InvokeAsync(new InvocationConfiguration(), TestContext.Current.CancellationToken);

        if (expected is null)
        {
            Assert.Equal(1, exitCode);
            Assert.Equal("", output.ToString());
            Assert.Contains(error!, errors.ToString(), StringComparison.OrdinalIgnoreCase);
        }
        else
        {
            Assert.Equal(0, exitCode);
            using JsonDocument document = JsonDocument.Parse(output.ToString());
            Assert.Equal(expected, document.RootElement.GetProperty("NAME").GetString());
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LinuxOsDoesNotTreatContentReadFailuresAsMissingReleasePaths(bool missingBlob)
    {
        await using LayerCacheTestContext cache = new();
        byte[] lower = WindowsImageTestData.Layer(
            WindowsImageTestData.File("usr/lib/os-release", "NAME=FallbackLinux\n"));
        byte[] upper = WindowsImageTestData.Layer(
            WindowsImageTestData.File("opt/os-release", "NAME=NewLinux\n"),
            new PaxTarEntry(TarEntryType.SymbolicLink, "etc/os-release") { LinkName = "/opt/os-release" });
        Mock<IDockerRegistryClient> client = WindowsImageTestData.Client("linux", "", lower, upper);
        Mock<IDockerRegistryClientFactory> factory = new();
        factory.Setup(item => item.GetClientAsync(WindowsImageTestData.Name.Registry, It.IsAny<CancellationToken>()))
            .ReturnsAsync(client.Object);
        using StringWriter output = new();
        using StringWriter errors = new();
        CapturedOsCommand initial = new(factory.Object, output, errors, cache.Paths);
        ((IProcessTerminationAware)initial).ProcessTerminator = new TestProcessTerminator();
        Assert.Equal(0, await initial.Parse([WindowsImageTestData.Name.ToString()])
            .InvokeAsync(new InvocationConfiguration(), TestContext.Current.CancellationToken));
        await cache.EvictBlobsAsync();
        client.Setup(item => item.Blobs.GetRangeAsync(WindowsImageTestData.Name.Repo,
            LayerCacheTestContext.Digest(upper), 0, It.IsAny<long?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(missingBlob ? new FileNotFoundException("Release content unavailable.") :
                new IOException("Release content unavailable."));
        output.GetStringBuilder().Clear();
        errors.GetStringBuilder().Clear();
        CapturedOsCommand command = new(factory.Object, output, errors, cache.Paths);
        ((IProcessTerminationAware)command).ProcessTerminator = new TestProcessTerminator();

        int exitCode = await command.Parse([WindowsImageTestData.Name.ToString()])
            .InvokeAsync(new InvocationConfiguration(), TestContext.Current.CancellationToken);

        Assert.Equal(1, exitCode);
        Assert.Equal("", output.ToString());
        Assert.Contains("Release content unavailable", errors.ToString());
    }

    [Theory]
    [InlineData("replace", "NewLinux")]
    [InlineData("introduce", "NewLinux")]
    [InlineData("whiteout", null)]
    [InlineData("opaque", null)]
    [InlineData("parent-whiteout", null)]
    [InlineData("fallback", "FallbackLinux")]
    [InlineData("opaque-fallback", "FallbackLinux")]
    public async Task LinuxOsUsesEffectiveFilesystem(string change, string? expected)
    {
        await using LayerCacheTestContext cache = new();
        byte[] lower = change == "introduce"
            ? DockerArchiveFixture.BuildTar(("placeholder", "empty"u8.ToArray()))
            : DockerArchiveFixture.BuildTar(
                ("etc/os-release", "NAME=OldLinux\n"u8.ToArray()),
                ("usr/lib/os-release", "NAME=FallbackLinux\n"u8.ToArray()));
        byte[] upper = DockerArchiveFixture.BuildTar(change switch
        {
            "replace" or "introduce" => ("etc/os-release", "NAME=NewLinux\n"u8.ToArray()),
            "opaque" or "opaque-fallback" => ("etc/.wh..wh..opq", []),
            "parent-whiteout" => (".wh.etc", []),
            _ => ("etc/.wh.os-release", [])
        });
        if (expected is null)
        {
            lower = DockerArchiveFixture.BuildTar(("etc/os-release", "NAME=OldLinux\n"u8.ToArray()));
        }
        Mock<IDockerRegistryClient> client = WindowsImageTestData.Client("linux", "", lower, upper);
        Mock<IDockerRegistryClientFactory> factory = new();
        factory.Setup(item => item.GetClientAsync(WindowsImageTestData.Name.Registry, It.IsAny<CancellationToken>()))
            .ReturnsAsync(client.Object);
        using StringWriter output = new();
        using StringWriter errors = new();
        CapturedOsCommand command = new(factory.Object, output, errors, cache.Paths);
        ((IProcessTerminationAware)command).ProcessTerminator = new TestProcessTerminator();

        int exitCode = await command.Parse([WindowsImageTestData.Name.ToString()])
            .InvokeAsync(new InvocationConfiguration(), TestContext.Current.CancellationToken);

        if (expected is null)
        {
            Assert.Equal(1, exitCode);
            Assert.Equal("", output.ToString());
            Assert.Contains("Unable to derive OS information", errors.ToString());
        }
        else
        {
            Assert.Equal(0, exitCode);
            using JsonDocument document = JsonDocument.Parse(output.ToString());
            Assert.Equal(expected, document.RootElement.GetProperty("NAME").GetString());
        }
    }

    [Theory]
    [InlineData(null, "10.0.20348.1366")]
    [InlineData("10.0.26100.1", "10.0.26100.1")]
    public async Task RegistryDescriptorPlatformReachesSourceOsAndDockerfile(string? configVersion, string expectedVersion)
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using LayerCacheTestContext cache = new();
        Mock<IDockerRegistryClient> registry = new() { DefaultValue = DefaultValue.Mock };
        Mock<IDockerRegistryClient> mcr = new() { DefaultValue = DefaultValue.Mock };
        Mock<IDockerRegistryClientFactory> factory = new();
        factory.Setup(item => item.GetClientAsync("registry.test", It.IsAny<CancellationToken>()))
            .ReturnsAsync(registry.Object);
        factory.Setup(item => item.GetClientAsync(RegistryHelper.McrRegistry, It.IsAny<CancellationToken>()))
            .ReturnsAsync(mcr.Object);
        ExplorerPlatform descriptorPlatform = new("windows", "amd64", null, "10.0.20348.1366");
        ManifestList index = new()
        {
            Manifests = [new ManifestReference
            {
                Digest = "sha256:selected",
                Platform = new ManifestPlatform
                {
                    Os = descriptorPlatform.Os, Architecture = descriptorPlatform.Architecture,
                    OsVersion = descriptorPlatform.OsVersion
                }
            }]
        };
        DockerManifest manifest = new()
        {
            Config = new ManifestConfig { Digest = "sha256:config" },
            Layers = [new ManifestLayer { Digest = "baseLayer" }]
        };
        registry.Setup(item => item.Manifests.GetAsync("image", "latest", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ManifestInfo("application/index", "sha256:index", index));
        registry.Setup(item => item.Manifests.GetAsync("image", "sha256:selected", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ManifestInfo("application/manifest", "sha256:selected", manifest));
        byte[] config = JsonSerializer.SerializeToUtf8Bytes(new Image
        {
            Os = "windows", Architecture = "amd64", OsVersion = configVersion,
            History = [new LayerHistory { CreatedBy = "base instruction" }]
        });
        registry.Setup(item => item.Blobs.GetAsync("image", "sha256:config", It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new MemoryStream(config));
        mcr.Setup(item => item.Blobs.ExistsAsync("windows/nanoserver", "baseLayer", It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        ImageSourceFactory sources = new(factory.Object, new TestAppSettingsStore(new()));
        await using (ResolvedImage source = await sources.OpenAsync("registry.test/image:latest",
            new() { Os = "windows", Architecture = "amd64" }, token, ImageSourceRequirements.Metadata))
        {
            Assert.Equal(descriptorPlatform, source.SelectedPlatform);
            Assert.Equal(descriptorPlatform, source.Resolved.SelectedPlatform);
            Assert.Equal(configVersion, source.Configuration.OsVersion);
            Assert.Equal(config, source.ConfigurationJson);
            Assert.Equal($"windows/amd64 ({expectedVersion})", source.Platform);
            WindowsImageInfo? info = await OsCommand.GetWindowsOsInfoAsync(source.Configuration,
                source.Resolved.Manifest, factory.Object, token, selectedPlatform: source.SelectedPlatform);
            Assert.Equal(expectedVersion, info!.Info.Version);
        }

        using StringWriter osOutput = new();
        OsCommand os = new(factory.Object, osOutput, cache.Paths) { ImageSourceFactory = sources };
        ((IProcessTerminationAware)os).ProcessTerminator = new TestProcessTerminator();
        string[] arguments = ["registry.test/image:latest", "--os", "windows", "--arch", "amd64"];
        Assert.Equal(0, await os.Parse(arguments).InvokeAsync(new InvocationConfiguration(), token));
        using JsonDocument osDocument = JsonDocument.Parse(osOutput.ToString());
        Assert.Equal("Nano Server", osDocument.RootElement.GetProperty("Type").GetString());
        Assert.Equal(expectedVersion, osDocument.RootElement.GetProperty("Version").GetString());

        using StringWriter dockerfileOutput = new();
        IAnsiConsole console = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Ansi = AnsiSupport.No, Out = new AnsiConsoleOutput(dockerfileOutput)
        });
        DockerfileCommand dockerfile = new(factory.Object, console) { ImageSourceFactory = sources };
        ((IProcessTerminationAware)dockerfile).ProcessTerminator = new TestProcessTerminator();
        Assert.Equal(0, await dockerfile.Parse(arguments).InvokeAsync(new InvocationConfiguration(), token));
        Assert.Contains($"FROM mcr.microsoft.com/windows/nanoserver:{expectedVersion}-amd64", dockerfileOutput.ToString());
    }

    [Fact]
    public async Task GetWindowsOsInfoAsyncUsesLongestDiffIdPrefixMatch()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        const string OsVersion = "10.0.20348.1366";
        const string Architecture = "amd64";
        const string BaseImageTag = $"{OsVersion}-{Architecture}";

        Image targetImage = new()
        {
            Os = "windows",
            OsVersion = OsVersion,
            Architecture = Architecture,
            RootFilesystem = new RootFilesystem
            {
                Type = "layers",
                DiffIds = ["baseDiff0", "baseDiff1", "appDiff"]
            }
        };
        DockerManifest targetManifest = new()
        {
            Layers =
            [
                new ManifestLayer
                {
                    Digest = "repackedLayerDigest"
                }
            ]
        };

        Mock<IDockerRegistryClientFactory> clientFactoryMock = new();
        Mock<IDockerRegistryClient> mcrClientMock = new();
        clientFactoryMock
            .Setup(static o => o.GetClientAsync(RegistryHelper.McrRegistry, It.IsAny<CancellationToken>()))
            .ReturnsAsync(mcrClientMock.Object);
        mcrClientMock
            .Setup(static o => o.Blobs.ExistsAsync(
                It.IsAny<string>(), "repackedLayerDigest", It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);
        mcrClientMock
            .Setup(static o => o.Manifests.ExistsAsync(
                It.IsAny<string>(), BaseImageTag, It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        SetupBaseImage(
            mcrClientMock,
            "windows/nanoserver",
            BaseImageTag,
            "nanoConfig",
            ["baseDiff0"],
            historyCount: 1);
        SetupBaseImage(
            mcrClientMock,
            "windows/servercore",
            BaseImageTag,
            "serverCoreConfig",
            ["baseDiff0", "baseDiff1"],
            historyCount: 2);

        WindowsImageInfo? result = await OsCommand.GetWindowsOsInfoAsync(
            targetImage, targetManifest, clientFactoryMock.Object, cancellationToken);

        Assert.NotNull(result);
        Assert.Equal(WindowsType.ServerCore, result.Info.Type);
        Assert.Equal(OsVersion, result.Info.Version);
        Assert.Equal("windows/servercore", result.Repo);
        Assert.Equal(2, result.BaseHistoryCount);
        mcrClientMock.Verify(static o => o.Manifests.ExistsAsync(
            "windows/servercore", BaseImageTag, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task GetWindowsOsInfoAsyncRetainsLegacyDigestDetection()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        Image targetImage = new()
        {
            Os = "windows",
            OsVersion = "10.0.17763.1",
            Architecture = "amd64"
        };
        DockerManifest targetManifest = new()
        {
            Layers =
            [
                new ManifestLayer { Digest = "baseLayer0" },
                new ManifestLayer { Digest = "baseLayer1" },
                new ManifestLayer { Digest = "appLayer" }
            ]
        };

        Mock<IDockerRegistryClientFactory> clientFactoryMock = new();
        Mock<IDockerRegistryClient> mcrClientMock = new();
        clientFactoryMock
            .Setup(static o => o.GetClientAsync(RegistryHelper.McrRegistry, It.IsAny<CancellationToken>()))
            .ReturnsAsync(mcrClientMock.Object);
        mcrClientMock
            .Setup(static o => o.Blobs.ExistsAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);
        mcrClientMock
            .Setup(static o => o.Blobs.ExistsAsync(
                "windows/servercore",
                It.Is<string>(static digest => digest == "baseLayer0" || digest == "baseLayer1"),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        WindowsImageInfo? result = await OsCommand.GetWindowsOsInfoAsync(
            targetImage, targetManifest, clientFactoryMock.Object, cancellationToken);

        Assert.NotNull(result);
        Assert.Equal(WindowsType.ServerCore, result.Info.Type);
        Assert.Equal(2, result.BaseHistoryCount);
        mcrClientMock.Verify(static o => o.Manifests.ExistsAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    private static void SetupBaseImage(
        Mock<IDockerRegistryClient> mcrClientMock,
        string repo,
        string tag,
        string configDigest,
        string[] diffIds,
        int historyCount)
    {
        mcrClientMock
            .Setup(o => o.Manifests.ExistsAsync(repo, tag, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        mcrClientMock
            .Setup(o => o.Manifests.GetAsync(repo, tag, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ManifestInfo("media-type", "manifest-digest",
                new DockerManifest
                {
                    Config = new ManifestConfig
                    {
                        Digest = configDigest
                    }
                }));

        Image baseImage = new()
        {
            RootFilesystem = new RootFilesystem
            {
                Type = "layers",
                DiffIds = diffIds
            },
            History = [.. Enumerable.Range(0, historyCount).Select(index => new LayerHistory { CreatedBy = $"base instruction {index}" })]
        };
        mcrClientMock
            .Setup(o => o.Blobs.GetAsync(repo, configDigest, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MemoryStream(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(baseImage))));
    }

    private sealed class CapturedOsCommand : OsCommand
    {
        private readonly TextWriter errors;

        public CapturedOsCommand(IDockerRegistryClientFactory factory, TextWriter output,
            TextWriter errors, IDredgePathProvider paths)
            : base(factory, output, paths)
        {
            this.errors = errors;
        }

        protected override TextWriter Error => errors;
    }
}
