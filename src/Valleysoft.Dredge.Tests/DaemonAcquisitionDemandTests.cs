using System.CommandLine;
using System.Formats.Tar;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Spectre.Console;
using Valleysoft.Dredge.Commands;
using Valleysoft.Dredge.Commands.Image;

namespace Valleysoft.Dredge.Tests;

public class DaemonAcquisitionDemandTests : IDisposable
{
    private readonly string directory = Path.GetFullPath(Path.Combine(".test-daemon-demand", Guid.NewGuid().ToString("N")));
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public DaemonAcquisitionDemandTests()
    {
        CacheFileSystem.CreateDirectory(directory);
    }

    [Fact]
    public void DefaultSnapshotRootIsAbsoluteWithoutRequiringApplicationDataToExist()
    {
        string applicationData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData,
            Environment.SpecialFolderOption.DoNotVerify);

        string root = DockerImageArchive.GetDefaultSnapshotRoot();

        Assert.True(Path.IsPathFullyQualified(root));
        Assert.Equal(Path.Combine(applicationData, "Valleysoft.Dredge", "snapshots"), root);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task LayerPrefixCommandsDoNotParseAnUnrelatedLaterLayer(bool gzip, bool cat)
    {
        byte[] selected = DockerArchiveFixture.BuildTar(("selected", "expected"u8.ToArray()));
        byte[] later = DockerArchiveFixture.BuildTar(("valid\nunix-name", "later"u8.ToArray()));
        DockerArchiveFixture fixture = CreateFixture(gzip, [selected, later]);
        TestDredgePathProvider paths = new(Path.Combine(directory, "work"));
        IDockerRegistryClientFactory registry = Mock.Of<IDockerRegistryClientFactory>();
        ImageSourceFactory factory = Factory(fixture, registry);
        using MemoryStream bytes = new();
        using StringWriter listing = new();
        IAnsiConsole console = AnsiConsole.Create(new AnsiConsoleSettings { Out = new AnsiConsoleOutput(listing) });
        Command command;
        if (cat)
        {
            CatCommand implementation = new(registry, bytes, paths) { ImageSourceFactory = factory };
            ((IProcessTerminationAware)implementation).ProcessTerminator = new TestProcessTerminator();
            command = implementation;
        }
        else
        {
            LsCommand implementation = new(registry, console, paths) { ImageSourceFactory = factory };
            ((IProcessTerminationAware)implementation).ProcessTerminator = new TestProcessTerminator();
            command = implementation;
        }
        string[] arguments = cat
            ? ["docker://local", "/selected", "--layer", "0", "--docker-host", "http://localhost:2375"]
            : ["docker://local", "--layer", "0", "--docker-host", "http://localhost:2375"];

        int exitCode = await command.Parse(arguments).InvokeAsync(new InvocationConfiguration(), Token);

        Assert.Equal(0, exitCode);
        if (cat)
        {
            Assert.Equal("expected"u8.ToArray(), bytes.ToArray());
        }
        else
        {
            Assert.Contains("selected", listing.ToString());
            Assert.DoesNotContain("unix-name", listing.ToString());
        }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task SuffixFileCommandsDoNotIndexAnUnrelatedEarlierLayer(bool gzip, bool extract)
    {
        DockerArchiveFixture fixture = CreateFixture(gzip,
            [DockerArchiveFixture.BuildTar(("valid\nunix-name", [1])),
                DockerArchiveFixture.BuildTar(("selected", "expected"u8.ToArray()))]);
        IDockerRegistryClientFactory registry = Mock.Of<IDockerRegistryClientFactory>();
        int indexStores = 0;
        ImageSourceFactory factory = Factory(fixture, registry, () =>
        {
            indexStores++;
            return new LayerStore(Path.Combine(directory, "indexes"));
        });
        TestDredgePathProvider paths = new(Path.Combine(directory, "work"));
        using MemoryStream output = new();
        string destination = Path.Combine(directory, "extracted");
        Command command;
        if (extract)
        {
            ExtractCommand implementation = new(registry, paths) { ImageSourceFactory = factory };
            ((IProcessTerminationAware)implementation).ProcessTerminator = new TestProcessTerminator();
            command = implementation;
        }
        else
        {
            CatCommand implementation = new(registry, output, paths) { ImageSourceFactory = factory };
            ((IProcessTerminationAware)implementation).ProcessTerminator = new TestProcessTerminator();
            command = implementation;
        }
        string[] arguments = extract ? ["docker://local", "/selected", destination] :
            ["docker://local", "/selected"];

        int exitCode = await command.Parse([.. arguments, "--docker-host", "http://localhost:2375"])
            .InvokeAsync(new InvocationConfiguration(), Token);

        Assert.Equal(0, exitCode);
        Assert.Equal(0, indexStores);
        Assert.Equal("expected"u8.ToArray(), extract ?
            await File.ReadAllBytesAsync(destination, Token) : output.ToArray());
        string data = Path.Combine(paths.CachePath, "layer-store", "data");
        Assert.Single(Directory.GetFiles(data, "*.index"));
        Assert.Empty(Directory.GetFiles(data, "*.blob"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SuffixResolutionStillLoadsEarlierLayersNeededByLinks(bool gzip)
    {
        using MemoryStream link = new();
        using (TarWriter writer = new(link, TarEntryFormat.Ustar, leaveOpen: true))
        {
            writer.WriteEntry(new UstarTarEntry(TarEntryType.SymbolicLink, "selected")
            {
                LinkName = "older/target"
            });
        }
        DockerArchiveFixture fixture = CreateFixture(gzip,
            [DockerArchiveFixture.BuildTar(("older/target", "expected"u8.ToArray())), link.ToArray()]);
        IDockerRegistryClientFactory registry = Mock.Of<IDockerRegistryClientFactory>();
        TestDredgePathProvider paths = new(Path.Combine(directory, "work"));
        using MemoryStream output = new();
        CatCommand command = new(registry, output, paths) { ImageSourceFactory = Factory(fixture, registry) };
        ((IProcessTerminationAware)command).ProcessTerminator = new TestProcessTerminator();

        int exitCode = await command.Parse(
            ["docker://local", "/selected", "--docker-host", "http://localhost:2375"])
            .InvokeAsync(new InvocationConfiguration(), Token);

        Assert.Equal(0, exitCode);
        Assert.Equal("expected"u8.ToArray(), output.ToArray());
        Assert.Equal(2, Directory.GetFiles(Path.Combine(paths.CachePath, "layer-store", "data"), "*.index").Length);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SuffixFileCommandStillRejectsInvalidContentInAnUnneededLayer(bool gzip)
    {
        DockerArchiveFixture fixture = CreateFixture(gzip,
            [DockerArchiveFixture.BuildTar(("unrelated\nname", [1])),
                DockerArchiveFixture.BuildTar(("selected", "expected"u8.ToArray()))],
            wrongFirstDiffId: true);
        IDockerRegistryClientFactory registry = Mock.Of<IDockerRegistryClientFactory>();
        TestDredgePathProvider paths = new(Path.Combine(directory, "work"));
        using MemoryStream output = new();
        RecordingProcessTerminator terminator = new();
        CatCommand command = new(registry, output, paths)
        {
            ImageSourceFactory = Factory(fixture, registry)
        };
        ((IProcessTerminationAware)command).ProcessTerminator = terminator;

        await command.Parse(["docker://local", "/selected", "--docker-host", "http://localhost:2375"])
            .InvokeAsync(new InvocationConfiguration(), Token);

        Assert.Equal(1, terminator.ExitCode);
        Assert.Equal(0, output.Length);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LayerPrefixAcquisitionStillVerifiesEveryDiffId(bool gzip)
    {
        DockerArchiveFixture fixture = CreateFixture(gzip,
            [DockerArchiveFixture.BuildTar(("selected", [1])), DockerArchiveFixture.BuildTar(("later\nname", [2]))],
            wrongLastDiffId: true);
        ImageSourceFactory factory = Factory(fixture, Mock.Of<IDockerRegistryClientFactory>());

        InvalidDataException error = await Assert.ThrowsAsync<InvalidDataException>(() => factory.OpenAsync(
            "docker://local", new CatOptions { Layer = 0, DockerHost = "http://localhost:2375" },
            Token, ImageSourceRequirements.LinuxFileSystem));

        Assert.Contains("rootfs diff ID", error.Message);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LinuxContentDemandVerifiesButDoesNotPrepareIndexes(bool gzip)
    {
        DockerArchiveFixture fixture = CreateFixture(gzip,
            [DockerArchiveFixture.BuildTar(("valid\nunix-name", [1, 2, 3]))]);
        int indexStores = 0;
        ImageSourceFactory factory = Factory(fixture, Mock.Of<IDockerRegistryClientFactory>(), () =>
        {
            indexStores++;
            return new LayerStore(Path.Combine(directory, "indexes"));
        });

        await using ResolvedImage source = await factory.OpenAsync("docker://local",
            new PlatformOptionsBase { DockerHost = "http://localhost:2375" }, Token,
            ImageSourceRequirements.LinuxLayerContent);

        DockerImageArchive archive = Assert.IsType<DockerImageArchive>(source.Content);
        string digest = source.Resolved.Manifest.Layers[0].Digest!;
        Assert.Equal(0, indexStores);
        Assert.Null(archive.GetPreparedIndex(digest, fixture.StoredLayer.Length));
        Assert.Null(archive.GetRetainedIndex(digest, fixture.StoredLayer.Length));
        Assert.True(archive.TryGetVerifiedContentDigest(digest, fixture.StoredLayer.Length,
            source.Configuration.RootFilesystem!.DiffIds[0], out _));
        Assert.Equal(fixture.Config, source.ConfigurationJson);
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, false, false)]
    [InlineData(true, true, false)]
    [InlineData(false, false, true)]
    [InlineData(false, true, true)]
    public async Task CachedListingViewDoesNotPrepareEvictedIndexesOnRepeatedAcquisitions(bool oci, bool gzip, bool sha512)
    {
        DockerArchiveFixture fixture = sha512
            ? CreateFixture(gzip, [DockerArchiveFixture.BuildTar(("selected", [1]))], sha512: true)
            : DockerArchiveFixture.Create(oci: oci, gzip: gzip);
        await using LayerStore store = new(Path.Combine(directory, "cache"));
        ImageSourceFactory factory = Factory(fixture, Mock.Of<IDockerRegistryClientFactory>(), store.CreateScope);
        string[] expected;
        StoredLayerIndex index;
        await using (ResolvedImage seed = await factory.OpenAsync("docker://local",
            new PlatformOptionsBase { DockerHost = "http://localhost:2375" }, Token))
        {
            await using ImageFileSystem files = await ImageFileSystem.CreateAsync(seed, Token, store);
            expected = [.. files.List(null, recursive: true, showDeleted: false).Select(entry => entry.Path)];
            index = await store.GetIndexAsync(seed.Content, new(0, seed.Resolved.Manifest.Layers[0].Digest!),
                fixture.StoredLayer.Length, Token);
        }
        foreach (string path in Directory.GetFiles(Path.Combine(directory, "cache"), "*.index",
            SearchOption.AllDirectories))
        {
            File.Delete(path);
        }

        for (int attempt = 0; attempt < 2; attempt++)
        {
            await using ResolvedImage source = await factory.OpenAsync("docker://local",
                new LsOptions { DockerHost = "http://localhost:2375" }, Token,
                ImageSourceRequirements.LinuxFileSystem);
            DockerImageArchive archive = Assert.IsType<DockerImageArchive>(source.Content);
            string digest = source.Resolved.Manifest.Layers[0].Digest!;
            ImageFileSystem.ValidatedFileSystem? cached = archive.CachedFileSystem;

            Assert.NotNull(cached);
            Assert.Null(archive.GetPreparedIndex(digest, fixture.StoredLayer.Length));
            Assert.Null(archive.GetRetainedIndex(digest, fixture.StoredLayer.Length));
            Assert.Equal(index.Changes.Entries.Count,
                archive.GetUnverifiedEntries(digest, index.Encoding, index.Changes.Entries).Length);
            Assert.True(archive.TryGetVerifiedContentDigest(digest, fixture.StoredLayer.Length,
                source.Configuration.RootFilesystem!.DiffIds[0], out _));
            string configurationDigest = source.Resolved.Manifest.Config!.Digest!;
            string viewPath = Path.Combine(directory, "cache", "layer-store", "data",
                LayerStore.GetKey(configurationDigest) + ".view");
            File.Delete(viewPath);
            using (CancellationTokenSource cancellation = new())
            {
                cancellation.Cancel();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                    ImageFileSystem.CreateAsync(source, cancellation.Token, store));
            }
            await using ImageFileSystem files = await ImageFileSystem.CreateAsync(source, Token, store);
            Assert.Equal(expected, files.List(null, recursive: true, showDeleted: false).Select(entry => entry.Path));
            Assert.Empty(Directory.GetFiles(Path.Combine(directory, "cache"), "*.index", SearchOption.AllDirectories));
            Assert.False(File.Exists(viewPath));
            await store.WriteMetadataAsync(configurationDigest, "view",
                new ImageFileSystem.StoredFileSystem([.. cached.Layers], [.. cached.Entries], [.. cached.DeletedEntries]), Token);
            await source.DisposeAsync();
            Assert.Null(archive.CachedFileSystem);
        }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task CachedViewDoesNotSuppressExplicitLayerPreparation(bool gzip, bool explorer)
    {
        DockerArchiveFixture fixture = DockerArchiveFixture.Create(oci: true, gzip: gzip);
        await using LayerStore store = new(Path.Combine(directory, "cache"));
        ImageSourceFactory factory = Factory(fixture, Mock.Of<IDockerRegistryClientFactory>(), store.CreateScope);
        await using (ResolvedImage seed = await factory.OpenAsync("docker://local",
            new PlatformOptionsBase { DockerHost = "http://localhost:2375" }, Token))
        {
            await using ImageFileSystem files = await ImageFileSystem.CreateAsync(seed, Token, store);
        }
        foreach (string path in Directory.GetFiles(Path.Combine(directory, "cache"), "*.index",
            SearchOption.AllDirectories))
        {
            File.Delete(path);
        }

        await using ResolvedImage source = await factory.OpenAsync("docker://local",
            explorer ? new PlatformOptionsBase { DockerHost = "http://localhost:2375" } :
                new LsOptions { Layer = 0, DockerHost = "http://localhost:2375" }, Token,
            ImageSourceRequirements.LinuxFileSystem);

        DockerImageArchive archive = Assert.IsType<DockerImageArchive>(source.Content);
        Assert.Null(archive.CachedFileSystem);
        Assert.NotNull(archive.GetPreparedIndex(source.Resolved.Manifest.Layers[0].Digest!, fixture.StoredLayer.Length));
    }

    [Theory]
    [InlineData(false, false, "/")]
    [InlineData(false, true, "/")]
    [InlineData(true, false, "/")]
    [InlineData(true, true, "/")]
    [InlineData(false, false, "")]
    [InlineData(true, false, "///")]
    public async Task CachedRootExtractionIndexesOnlySurvivingContentIncludingOldHardLinks(
        bool gzip, bool hardLink, string requestedPath)
    {
        using MemoryStream lower = new();
        using (TarWriter tar = new(lower, leaveOpen: true))
        {
            tar.WriteEntry(new UstarTarEntry(TarEntryType.RegularFile, "selected")
            {
                DataStream = new MemoryStream("obsolete"u8.ToArray())
            });
            tar.WriteEntry(new UstarTarEntry(TarEntryType.RegularFile, "original")
            {
                DataStream = new MemoryStream("retained"u8.ToArray())
            });
            if (hardLink)
            {
                tar.WriteEntry(new UstarTarEntry(TarEntryType.HardLink, "alias") { LinkName = "original" });
            }
        }
        DockerArchiveFixture fixture = CreateFixture(gzip,
            [lower.ToArray(), DockerArchiveFixture.BuildTar(("selected", "new"u8.ToArray()), (".wh.original", []))]);
        await using LayerStore store = new(Path.Combine(directory, "cache"));
        ImageSourceFactory factory = Factory(fixture, Mock.Of<IDockerRegistryClientFactory>(), store.CreateScope);
        await using (ResolvedImage seed = await factory.OpenAsync("docker://local",
            new PlatformOptionsBase { DockerHost = "http://localhost:2375" }, Token))
        {
            await using ImageFileSystem files = await ImageFileSystem.CreateAsync(seed, Token, store);
        }
        foreach (string path in Directory.GetFiles(Path.Combine(directory, "cache"), "*.index", SearchOption.AllDirectories))
        {
            File.Delete(path);
        }
        await using ResolvedImage source = await factory.OpenAsync("docker://local",
            new ExtractOptions { Path = requestedPath, DockerHost = "http://localhost:2375" }, Token,
            ImageSourceRequirements.LinuxFileSystem);
        DockerImageArchive archive = Assert.IsType<DockerImageArchive>(source.Content);
        Assert.NotNull(archive.CachedFileSystem);
        Assert.Equal(hardLink, archive.GetPreparedIndex(source.Resolved.Manifest.Layers[0].Digest!, null) is not null);
        Assert.NotNull(archive.GetPreparedIndex(source.Resolved.Manifest.Layers[1].Digest!, null));
        await using ImageFileSystem restored = await ImageFileSystem.CreateAsync(source, Token, store, extractionPath: requestedPath);
        Assert.Empty(Directory.GetFiles(Path.Combine(directory, "cache"), "*.index", SearchOption.AllDirectories));
        string destination = Path.Combine(directory, "root-extraction");

        await restored.ExtractAsync(requestedPath, destination, Token);

        Assert.Equal("new", await File.ReadAllTextAsync(Path.Combine(destination, "selected"), Token));
        Assert.False(File.Exists(Path.Combine(destination, "original")));
        Assert.Equal(hardLink, File.Exists(Path.Combine(destination, "alias")));
        if (hardLink)
        {
            Assert.Equal("retained", await File.ReadAllTextAsync(Path.Combine(destination, "alias"), Token));
        }
        Assert.Equal(hardLink ? 2 : 1,
            Directory.GetFiles(Path.Combine(directory, "cache"), "*.index", SearchOption.AllDirectories).Length);
    }

    [Theory]
    [InlineData(false, true, false)]
    [InlineData(true, false, false)]
    [InlineData(true, true, false)]
    [InlineData(true, false, true)]
    public async Task CachedClassicRootReusesMatchingIndexesWithoutTrustingStaleEncodedDigests(
        bool gzip, bool sha512, bool repacked)
    {
        DockerArchiveFixture fixture = CreateFixture(gzip,
            [DockerArchiveFixture.BuildTar(("selected", "expected"u8.ToArray()))], sha512: sha512);
        await using LayerStore store = new(Path.Combine(directory, "cache"));
        ImageSourceFactory factory = Factory(fixture, Mock.Of<IDockerRegistryClientFactory>(), store.CreateScope);
        await using (ResolvedImage seed = await factory.OpenAsync("docker://local",
            new PlatformOptionsBase { DockerHost = "http://localhost:2375" }, Token))
        {
            await using ImageFileSystem files = await ImageFileSystem.CreateAsync(seed, Token, store);
        }
        if (repacked)
        {
            byte[] modified = [.. fixture.StoredLayer];
            modified[4] ^= 1;
            fixture = fixture with
            {
                StoredLayer = modified,
                Export = DockerArchiveFixture.BuildTar(
                    ("config.json", fixture.Config), ("layer-0.tar", modified),
                    ("manifest.json", """[{"Config":"config.json","Layers":["layer-0.tar"]}]"""u8.ToArray()))
            };
            factory = Factory(fixture, Mock.Of<IDockerRegistryClientFactory>(), store.CreateScope);
        }

        await using ResolvedImage source = await factory.OpenAsync("docker://local",
            new ExtractOptions { Path = "/", DockerHost = "http://localhost:2375" }, Token,
            ImageSourceRequirements.LinuxFileSystem);

        DockerImageArchive archive = Assert.IsType<DockerImageArchive>(source.Content);
        string digest = source.Resolved.Manifest.Layers[0].Digest!;
        Assert.NotNull(archive.CachedFileSystem);
        if (!repacked)
        {
            Assert.NotNull(archive.GetRetainedIndex(digest, fixture.StoredLayer.Length));
            Assert.Null(archive.GetPreparedIndex(digest, fixture.StoredLayer.Length));
        }
        else
        {
            Assert.Null(archive.GetRetainedIndex(digest, fixture.StoredLayer.Length));
        }
        await using ImageFileSystem restored = await ImageFileSystem.CreateAsync(source, Token, store, extractionPath: "/");
        string destination = Path.Combine(directory, "warm-root");
        await restored.ExtractAsync("/", destination, Token);
        Assert.Equal("expected", await File.ReadAllTextAsync(Path.Combine(destination, "selected"), Token));
        Assert.Equal(digest, Assert.Single(restored.List(null, true, false)).IntroducedLayer.Digest);
    }

    [Fact]
    public async Task RestoringPrevalidatedDaemonViewDoesNotRepeatPathNormalization()
    {
        const int count = 10_000;
        DockerArchiveFixture fixture = DockerArchiveFixture.Create();
        string digest = DockerImageArchive.Hash(fixture.StoredLayer);
        ImageFileSystem.StoredEntry[] entries = [.. Enumerable.Range(0, count).Select(i =>
            new ImageFileSystem.StoredEntry(new()
            {
                Path = $"a/b/c/d/e/f/g/h/i/j/file{i}",
                Type = ImageFileType.File,
                IntroducedLayer = new(0, digest)
            }, 0, i, null, null))];
        await using LayerStore store = new(Path.Combine(directory, "cache"));
        await store.WriteMetadataAsync(fixture.Id, "view",
            new ImageFileSystem.StoredFileSystem([digest], entries[..(count / 2)], entries[(count / 2)..]), Token);
        ImageSourceFactory factory = Factory(fixture, Mock.Of<IDockerRegistryClientFactory>(), store.CreateScope);
        await using ResolvedImage source = await factory.OpenAsync("docker://local",
            new LsOptions { DockerHost = "http://localhost:2375" }, Token,
            ImageSourceRequirements.LinuxFileSystem);
        await using (ImageFileSystem warmup = await ImageFileSystem.CreateAsync(source, Token, store)) { }
        long start = GC.GetAllocatedBytesForCurrentThread();

        Task<ImageFileSystem> restoring = ImageFileSystem.CreateAsync(source, Token, store);
        bool completedSynchronously = restoring.IsCompletedSuccessfully;
        long allocated = GC.GetAllocatedBytesForCurrentThread() - start;
        await using ImageFileSystem restored = await restoring;

        Assert.True(completedSynchronously);
        Assert.Equal(count, restored.List(null, true, true).Count);
        Assert.True(allocated < count * 700L,
            $"Restoring {count:N0} prevalidated entries allocated {allocated:N0} bytes.");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CachedViewDoesNotBypassLayerContentIntegrity(bool gzip)
    {
        DockerArchiveFixture fixture = CreateFixture(gzip,
            [DockerArchiveFixture.BuildTar(("selected", "original"u8.ToArray()))]);
        await using LayerStore store = new(Path.Combine(directory, "cache"));
        ImageSourceFactory factory = Factory(fixture, Mock.Of<IDockerRegistryClientFactory>(), store.CreateScope);
        await using (ResolvedImage seed = await factory.OpenAsync("docker://local",
            new PlatformOptionsBase { DockerHost = "http://localhost:2375" }, Token))
        {
            await using ImageFileSystem files = await ImageFileSystem.CreateAsync(seed, Token, store);
        }
        byte[] modified = [.. fixture.StoredLayer];
        modified[modified.Length / 2] ^= 1;
        using MemoryStream input = new(fixture.Export);
        using TarReader reader = new(input);
        List<(string Path, byte[] Content)> members = [];
        TarEntry? entry;
        while ((entry = reader.GetNextEntry()) is not null)
        {
            using MemoryStream content = new();
            entry.DataStream!.CopyTo(content);
            members.Add((entry.Name, entry.Name == "layer-0.tar" ? modified : content.ToArray()));
        }
        DockerArchiveFixture invalid = new(DockerArchiveFixture.BuildTar([.. members]), fixture.Config, fixture.Id, modified);
        ImageSourceFactory broken = Factory(invalid, Mock.Of<IDockerRegistryClientFactory>(), store.CreateScope);

        await Assert.ThrowsAsync<InvalidDataException>(() => broken.OpenAsync("docker://local",
            new LsOptions { DockerHost = "http://localhost:2375" }, Token,
            ImageSourceRequirements.LinuxFileSystem));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InvalidCachedViewDoesNotSuppressColdIndexPreparation(bool gzip)
    {
        DockerArchiveFixture fixture = DockerArchiveFixture.Create(oci: true, gzip: gzip);
        await using LayerStore store = new(Path.Combine(directory, "cache"));
        await store.WriteMetadataAsync(fixture.Id, "view",
            new ImageFileSystem.StoredFileSystem(["sha256:" + new string('0', 64)],
                [new(null!, 0, 0, null, null)], []), Token);
        ImageSourceFactory factory = Factory(fixture, Mock.Of<IDockerRegistryClientFactory>(), store.CreateScope);

        await using ResolvedImage source = await factory.OpenAsync("docker://local",
            new LsOptions { DockerHost = "http://localhost:2375" }, Token,
            ImageSourceRequirements.LinuxFileSystem);

        DockerImageArchive archive = Assert.IsType<DockerImageArchive>(source.Content);
        Assert.NotNull(archive.GetPreparedIndex(source.Resolved.Manifest.Layers[0].Digest!, fixture.StoredLayer.Length));
        await using ImageFileSystem files = await ImageFileSystem.CreateAsync(source, Token, store);
        Assert.NotEmpty(files.List(null, recursive: true, showDeleted: false));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task DuplicateCachedViewPathsDoNotSuppressIndexPreparation(bool deleted, bool extract)
    {
        DockerArchiveFixture fixture = DockerArchiveFixture.Create(oci: true);
        await using LayerStore store = new(Path.Combine(directory, "cache"));
        string digest = DockerImageArchive.Hash(fixture.StoredLayer);
        ImageFileSystem.StoredEntry entry = new(new()
        {
            Path = "duplicate",
            Type = ImageFileType.File,
            IntroducedLayer = new(0, digest)
        }, 0, 0, null, null);
        await store.WriteMetadataAsync(fixture.Id, "view",
            new ImageFileSystem.StoredFileSystem([digest], deleted ? [] : [entry, entry],
                deleted ? [entry, entry] : []), Token);
        ImageSourceFactory factory = Factory(fixture, Mock.Of<IDockerRegistryClientFactory>(), store.CreateScope);

        await using ResolvedImage source = await factory.OpenAsync("docker://local",
            extract ? new ExtractOptions { Path = "/", DockerHost = "http://localhost:2375" } :
                new LsOptions { DockerHost = "http://localhost:2375" }, Token,
            ImageSourceRequirements.LinuxFileSystem);

        DockerImageArchive archive = Assert.IsType<DockerImageArchive>(source.Content);
        Assert.Null(archive.CachedFileSystem);
        Assert.NotNull(archive.GetPreparedIndex(digest, fixture.StoredLayer.Length));
    }

    [Theory]
    [InlineData(false, "save")]
    [InlineData(true, "save")]
    [InlineData(false, "save-separate")]
    [InlineData(true, "save-separate")]
    [InlineData(false, "compare")]
    [InlineData(true, "compare")]
    public async Task LayerExtractionCommandsDoNotPrepareUnusedIndexes(bool gzip, string operation)
    {
        DockerArchiveFixture fixture = CreateFixture(gzip,
            [DockerArchiveFixture.BuildTar(("selected", "expected"u8.ToArray())),
                DockerArchiveFixture.BuildTar(("unrelated\nname", [2]))]);
        TestDredgePathProvider paths = new(Path.Combine(directory, "work"));
        IDockerRegistryClientFactory registry = Mock.Of<IDockerRegistryClientFactory>();
        int indexStores = 0;
        ImageSourceFactory factory = Factory(fixture, registry, () =>
        {
            indexStores++;
            return new LayerStore(Path.Combine(directory, "indexes"));
        });
        TestProcessLauncher launcher = new();
        Command command;
        string[] arguments;
        string destination = Path.Combine(directory, "saved");
        if (operation == "compare")
        {
            AppSettings settings = new();
            settings.FileCompareTool.ExePath = "not-launched";
            settings.FileCompareTool.Args = "\"{0}\" \"{1}\"";
            CompareFilesCommand implementation = new(registry,
                new TestAppSettingsStore(settings), paths, launcher) { ImageSourceFactory = factory };
            ((IProcessTerminationAware)implementation).ProcessTerminator = new TestProcessTerminator();
            command = implementation;
            arguments = ["docker://local", "docker://alias",
                CompareFilesOptions.BaseLayerIndexOptionName, "0",
                CompareFilesOptions.TargetLayerIndexOptionName, "0"];
        }
        else
        {
            SaveLayersCommand implementation = new(registry, paths) { ImageSourceFactory = factory };
            ((IProcessTerminationAware)implementation).ProcessTerminator = new TestProcessTerminator();
            command = implementation;
            arguments = ["docker://local", destination, SaveLayersOptions.LayerIndexOptionName, "0"];
            if (operation == "save-separate")
            {
                arguments = [.. arguments, "--no-squash"];
            }
        }

        int exitCode = await command.Parse([.. arguments, "--docker-host", "http://localhost:2375"])
            .InvokeAsync(new InvocationConfiguration(), Token);

        Assert.Equal(0, exitCode);
        Assert.Equal(0, indexStores);
        string root = operation == "compare" ? Path.Combine(paths.TempPath, "compare") : destination;
        string[] files = Directory.GetFiles(root, "selected", SearchOption.AllDirectories);
        Assert.Equal(operation == "compare" ? 2 : 1, files.Length);
        foreach (string file in files)
        {
            Assert.Equal("expected", await File.ReadAllTextAsync(file, Token));
        }
        Assert.Equal(operation == "compare", launcher.StartInfo is not null);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExplorerBaseVerificationDoesNotPrepareUnusedCandidateIndexes(bool gzip)
    {
        DockerArchiveFixture fixture = CreateFixture(gzip,
            [DockerArchiveFixture.BuildTar(("valid\nunix-name", [1]))]);
        await using DockerImageArchive target = await DockerImageArchive.OpenAsync(
            new MemoryStream(fixture.Export), fixture.Id, Token, Path.Combine(directory, "target"));
        int indexStores = 0;
        ImageSourceFactory factory = Factory(fixture, Mock.Of<IDockerRegistryClientFactory>(), () =>
        {
            indexStores++;
            return new LayerStore(Path.Combine(directory, "indexes"));
        });
        await using LayerStore store = new(Path.Combine(directory, "verification"));

        (IReadOnlyList<ExplorerBaseImage> bases, _) = await ExplorerSource.VerifySourceBasesAsync(
            factory, target.Configuration, target.Resolved, true,
            new PlatformOptionsBase { DockerHost = "http://localhost:2375" }, ["docker://base"], Token,
            targetContent: target, store: store);

        Assert.Equal(1, Assert.Single(bases).LayerCount);
        Assert.Equal(0, indexStores);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task LinuxContentAndFilesystemDemandsShareTheOriginalContentArchive(bool scoped, bool filesystemFirst)
    {
        DockerArchiveFixture fixture = CreateFixture(true, [DockerArchiveFixture.BuildTar(("file", [1]))]);
        List<ImageSourceFactoryTests.FakeEngine> engines = [];
        ImageSourceFactory factory = new(Mock.Of<IDockerRegistryClientFactory>(),
            new TestAppSettingsStore(new AppSettings(), settingsPath: ""),
            _ =>
            {
                ImageSourceFactoryTests.FakeEngine engine = new(fixture);
                engines.Add(engine);
                return engine;
            }, directory, indexStoreFactory: () => new LayerStore(Path.Combine(directory, "indexes")));
        await using ImageSourceFactory.ComparisonScope? scope = scoped ? factory.CreateComparisonScope() : null;
        PlatformOptionsBase options = new() { DockerHost = "http://localhost:2375" };
        ImageSourceRequirements firstDemand = filesystemFirst
            ? ImageSourceRequirements.LinuxFileSystem : ImageSourceRequirements.LinuxLayerContent;
        ImageSourceRequirements secondDemand = filesystemFirst
            ? ImageSourceRequirements.LinuxLayerContent : ImageSourceRequirements.LinuxFileSystem;
        await using ResolvedImage first = scope is null
            ? await factory.OpenAsync("docker://local", options, Token, firstDemand)
            : await scope.OpenAsync("docker://local", options, Token, firstDemand);
        await using ResolvedImage second = scope is null
            ? await factory.OpenAsync("docker://alias", options, Token, secondDemand)
            : await scope.OpenAsync("docker://alias", options, Token, secondDemand);

        Assert.Same(first.Content, second.Content);
        Assert.Equal(filesystemFirst, ((DockerImageArchive)second.Content).GetPreparedIndex(
            second.Resolved.Manifest.Layers[0].Digest!, fixture.StoredLayer.Length) is not null);
        await using LayerStore store = new(Path.Combine(directory, "consumer"));
        await using ImageFileSystem files = await ImageFileSystem.CreateAsync(second, Token, store);
        Assert.Single(files.List(null, recursive: false, showDeleted: false));
        Assert.Single(engines.SelectMany(engine => engine.Exports));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task LargerFilesystemDemandIndexesMissingLayersOfASharedPrefixArchive(bool scoped, bool gzip)
    {
        DockerArchiveFixture fixture = CreateFixture(gzip,
            [DockerArchiveFixture.BuildTar(("first", [1])), DockerArchiveFixture.BuildTar(("later", [2]))]);
        ImageSourceFactory factory = Factory(fixture, Mock.Of<IDockerRegistryClientFactory>());
        await using ImageSourceFactory.ComparisonScope? scope = scoped ? factory.CreateComparisonScope() : null;
        CatOptions prefixOptions = new() { Layer = 0, DockerHost = "http://localhost:2375" };
        PlatformOptionsBase fullOptions = new() { DockerHost = prefixOptions.DockerHost };
        await using ResolvedImage prefix = scope is null
            ? await factory.OpenAsync("docker://local", prefixOptions, Token, ImageSourceRequirements.LinuxFileSystem)
            : await scope.OpenAsync("docker://local", prefixOptions, Token, ImageSourceRequirements.LinuxFileSystem);
        await using ResolvedImage full = scope is null
            ? await factory.OpenAsync("docker://alias", fullOptions, Token, ImageSourceRequirements.LinuxFileSystem)
            : await scope.OpenAsync("docker://alias", fullOptions, Token, ImageSourceRequirements.LinuxFileSystem);
        DockerImageArchive archive = Assert.IsType<DockerImageArchive>(full.Content);

        Assert.Same(prefix.Content, full.Content);
        Assert.NotNull(archive.GetPreparedIndex(full.Resolved.Manifest.Layers[0].Digest!, null));
        Assert.Null(archive.GetPreparedIndex(full.Resolved.Manifest.Layers[1].Digest!, null));
        await using LayerStore store = new(Path.Combine(directory, "consumer"));
        await using ImageFileSystem files = await ImageFileSystem.CreateAsync(full, Token, store);
        Assert.Equal(2, files.List(null, recursive: false, showDeleted: false).Count);
        using MemoryStream output = new();
        await files.CopyFileToAsync("/later", output, Token);
        Assert.Equal(new byte[] { 2 }, output.ToArray());
    }

    [Fact]
    public async Task GzipSelectedProofsAreScopedToTheVerifiedPrefix()
    {
        DockerArchiveFixture fixture = DockerArchiveFixture.Create(oci: true, gzip: true);
        await using DockerImageArchive archive = await DockerImageArchive.OpenAsync(
            new MemoryStream(fixture.Export), fixture.Id, Token, directory, prepareFileSystem: true);
        string digest = archive.Resolved.Manifest.Layers[0].Digest!;
        ScannedEntry entry = Assert.Single(archive.GetPreparedIndex(digest, fixture.StoredLayer.Length)!.Changes.Entries);

        archive.RecordVerifiedEntries(digest, "gzip", [entry], prefixLength: 1024);

        Assert.Empty(archive.GetUnverifiedEntries(digest, "gzip", [entry], prefixLength: 1024));
        Assert.Single(archive.GetUnverifiedEntries(digest, "gzip", [entry], prefixLength: 2048));
        Assert.Single(archive.GetUnverifiedEntries(digest, "gzip", [entry]));
        archive.RecordVerifiedEntries(digest, "tar", [entry]);
        Assert.Empty(archive.GetUnverifiedEntries(digest, "tar", [entry], prefixLength: 2048));
    }

    private ImageSourceFactory Factory(DockerArchiveFixture fixture, IDockerRegistryClientFactory registry,
        Func<LayerStore>? indexStoreFactory = null) =>
        new(registry, new TestAppSettingsStore(new AppSettings(), settingsPath: ""),
            _ => new ImageSourceFactoryTests.FakeEngine(fixture), directory,
            indexStoreFactory: indexStoreFactory ?? (() => new LayerStore(Path.Combine(directory, "indexes"))));

    private static DockerArchiveFixture CreateFixture(bool gzip, byte[][] layers, bool wrongLastDiffId = false,
        bool wrongFirstDiffId = false, bool sha512 = false)
    {
        JsonObject config = JsonNode.Parse(DockerArchiveFixture.Create(scratch: true).Config)!.AsObject();
        JsonArray diffIds = new();
        JsonArray history = new();
        List<(string Path, byte[] Content)> members = [];
        List<string> names = [];
        byte[] firstStored = [];
        for (int i = 0; i < layers.Length; i++)
        {
            byte[] stored = layers[i];
            if (gzip)
            {
                using MemoryStream buffer = new();
                using (GZipStream stream = new(buffer, CompressionLevel.Fastest, leaveOpen: true))
                {
                    stream.Write(stored);
                }
                stored = buffer.ToArray();
            }
            if (i == 0)
            {
                firstStored = stored;
            }
            string name = $"layer-{i}.tar";
            names.Add(name);
            members.Add((name, stored));
            diffIds.Add((wrongLastDiffId && i == layers.Length - 1) || (wrongFirstDiffId && i == 0)
                ? "sha256:" + new string('0', 64)
                : sha512 ? "sha512:" + Convert.ToHexStringLower(SHA512.HashData(layers[i]))
                : DockerImageArchive.Hash(layers[i]));
            history.Add(new JsonObject { ["created_by"] = $"layer {i}" });
        }
        config["rootfs"]!["diff_ids"] = diffIds;
        config["history"] = history;
        byte[] json = JsonSerializer.SerializeToUtf8Bytes(config);
        members.Add(("config.json", json));
        members.Add(("manifest.json", JsonSerializer.SerializeToUtf8Bytes(
            new[] { new { Config = "config.json", Layers = names } })));
        return new(DockerArchiveFixture.BuildTar([.. members]), json, DockerImageArchive.Hash(json), firstStored);
    }

    public void Dispose()
    {
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
