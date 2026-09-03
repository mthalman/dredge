namespace Valleysoft.Dredge.Tests;

using Spectre.Console;
using System.CommandLine;
using System.CommandLine.Parsing;
using System.Formats.Tar;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Valleysoft.DockerRegistryClient.Models.Manifests;
using Valleysoft.DockerRegistryClient.Models.Manifests.Docker;
using Valleysoft.Dredge.Commands;
using Valleysoft.Dredge.Commands.Image;

public class ImageFileSystemTests
{
    private const string ConfigDigest = "sha256:config";
    private static readonly ImageName ImageName = ImageName.Parse("registry.test/repo:tag");

    [Fact]
    public async Task Index_AppliesLayersWhiteoutsAndProvenance()
    {
        byte[][] layers =
        [
            CreateLayer(
                Entry.File("etc/config", "old"),
                Entry.File("etc/deleted", "gone"),
                Entry.File("opaque/old", "gone"),
                Entry.File("recreated", "first")),
            CreateLayer(
                Entry.File("etc/config", "new"),
                Entry.File("etc/.wh.deleted", ""),
                Entry.File("opaque/.wh..wh..opq", ""),
                Entry.File("opaque/new", "new"),
                Entry.File(".wh.recreated", "")),
            CreateLayer(Entry.File("recreated", "again"))
        ];
        using IDockerRegistryClient client = CreateClient(layers).Object;

        ImageFileSystem fileSystem = await ImageFileSystem.CreateAsync(
            client,
            ImageName,
            new PlatformOptionsBase(),
            TestContext.Current.CancellationToken);

        Assert.Equal(
            ["etc", "opaque", "recreated"],
            fileSystem.List(null, recursive: false, showDeleted: false)
                .Select(entry => entry.Path));
        IReadOnlyList<ImageFileSystemEntry> all =
            fileSystem.List(null, recursive: true, showDeleted: true);
        ImageFileSystemEntry config = Assert.Single(all, entry => entry.Path == "etc/config");
        Assert.Equal(0, config.IntroducedLayer.Index);
        Assert.Equal(1, config.ModifiedLayer?.Index);
        ImageFileSystemEntry deleted = Assert.Single(all, entry => entry.Path == "etc/deleted");
        Assert.Equal(1, deleted.DeletedLayer?.Index);
        Assert.Same(
            deleted,
            Assert.Single(fileSystem.List("etc/deleted", recursive: false, showDeleted: true)));
        Assert.Contains(all, entry => entry.Path == "opaque/old" && entry.DeletedLayer?.Index == 1);
        ImageFileSystemEntry recreated = Assert.Single(all, entry => entry.Path == "recreated");
        Assert.Equal(2, recreated.IntroducedLayer.Index);
        Assert.Null(recreated.ModifiedLayer);
        Assert.Null(recreated.DeletedLayer);
        Assert.Equal(
            all.OrderBy(entry => entry.Path, StringComparer.Ordinal).Select(entry => entry.Path),
            all.Select(entry => entry.Path));
    }

    [Fact]
    public async Task CopyFile_FollowsLinksAndReturnsExactEffectiveBytes()
    {
        byte[] binary = [0, 1, 2, 10, 13, 255];
        const string unicodePath = "data/Főtanúsítvány.pem";
        byte[][] layers =
        [
            CreateLayer(
                Entry.File("data/value", "old"),
                Entry.File(unicodePath, "certificate"),
                Entry.File("duplicate", "first"),
                Entry.File("duplicate", "second"),
                Entry.SymbolicLink("relative", "data/value"),
                Entry.SymbolicLink("absolute", "/data/value"),
                Entry.SymbolicLink("unicode", unicodePath),
                Entry.HardLink("hard", "data/value"),
                Entry.HardLink("hard-symbolic", "relative"),
                Entry.File("usr/bin/tool", "tool"),
                Entry.SymbolicLink("bin", "/usr/bin"),
                Entry.HardLink("hard-symlinked-parent", "bin/tool")),
            CreateLayer(Entry.File("data/value", binary))
        ];
        using IDockerRegistryClient client = CreateClient(layers).Object;
        ImageFileSystem fileSystem = await ImageFileSystem.CreateAsync(
            client,
            ImageName,
            new PlatformOptionsBase(),
            TestContext.Current.CancellationToken);

        foreach ((string path, byte[] expected) in new[]
        {
            ("data/value", binary),
            ("relative", binary),
            ("absolute", binary),
            ("unicode", Encoding.UTF8.GetBytes("certificate")),
            ("duplicate", Encoding.UTF8.GetBytes("second")),
            ("hard", Encoding.UTF8.GetBytes("old")),
            ("hard-symbolic", binary),
            ("hard-symlinked-parent", Encoding.UTF8.GetBytes("tool"))
        })
        {
            using MemoryStream output = new();
            await fileSystem.CopyFileToAsync(
                path,
                output,
                TestContext.Current.CancellationToken);
            Assert.Equal(expected, output.ToArray());
        }
    }

    [Fact]
    public async Task ListAndExtract_ResolveIntermediateSymbolicLinks()
    {
        string output = Path.Combine(
            Path.GetTempPath(),
            $"dredge-filesystem-{Guid.NewGuid():N}");
        using IDockerRegistryClient client = CreateClient(
        [
            CreateLayer(
                Entry.File("usr/bin/tool", "tool"),
                Entry.SymbolicLink("bin", "/usr/bin"))
        ]).Object;
        ImageFileSystem fileSystem = await ImageFileSystem.CreateAsync(
            client,
            ImageName,
            new PlatformOptionsBase(),
            TestContext.Current.CancellationToken);

        try
        {
            ImageFileSystemEntry listed = Assert.Single(
                fileSystem.List("bin/tool", recursive: false, showDeleted: false));
            Assert.Equal("bin/tool", listed.Path);

            await fileSystem.ExtractAsync(
                "bin/tool",
                output,
                TestContext.Current.CancellationToken);

            Assert.Equal("tool", File.ReadAllText(output));
        }
        finally
        {
            File.Delete(output);
        }
    }

    [Fact]
    public async Task List_ShowDeletedResolvesIntermediateSymbolicLinks()
    {
        using IDockerRegistryClient client = CreateClient(
        [
            CreateLayer(
                Entry.File("usr/bin/deleted", "content"),
                Entry.SymbolicLink("bin", "/usr/bin")),
            CreateLayer(Entry.File("usr/bin/.wh.deleted", ""))
        ]).Object;
        ImageFileSystem fileSystem = await ImageFileSystem.CreateAsync(
            client,
            ImageName,
            new PlatformOptionsBase(),
            TestContext.Current.CancellationToken);

        ImageFileSystemEntry listed = Assert.Single(
            fileSystem.List("bin/deleted", recursive: false, showDeleted: true));

        Assert.Equal("bin/deleted", listed.Path);
        Assert.Equal(1, listed.DeletedLayer?.Index);
    }

    [Theory]
    [InlineData(TarEntryFormat.Pax)]
    [InlineData(TarEntryFormat.Gnu)]
    public async Task Index_ReadsSupportedTarFormats(TarEntryFormat format)
    {
        const string Target = "Főtanúsítvány.pem";
        using IDockerRegistryClient client = CreateClient(
            [CreateLayer(
                format,
                Entry.File(Target, "x"),
                Entry.SymbolicLink("link", Target))]).Object;

        ImageFileSystem fileSystem = await ImageFileSystem.CreateAsync(
            client,
            ImageName,
            new PlatformOptionsBase(),
            TestContext.Current.CancellationToken);
        using MemoryStream output = new();
        await fileSystem.CopyFileToAsync(
            "link",
            output,
            TestContext.Current.CancellationToken);

        Assert.Equal("x", Encoding.UTF8.GetString(output.ToArray()));
        Assert.Equal(
            Target,
            Assert.Single(fileSystem.List("link", recursive: false, showDeleted: false))
                .LinkTarget);
    }

    [Fact]
    public async Task CopyFile_RejectsDanglingAndLoopingLinksAndClampsAtRoot()
    {
        byte[][] layers =
        [
            CreateLayer(
                Entry.SymbolicLink("dangling", "missing"),
                Entry.SymbolicLink("a", "b"),
                Entry.SymbolicLink("b", "a"))
        ];
        using IDockerRegistryClient client = CreateClient(layers).Object;
        ImageFileSystem fileSystem = await ImageFileSystem.CreateAsync(
            client,
            ImageName,
            new PlatformOptionsBase(),
            TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<FileNotFoundException>(
            () => fileSystem.CopyFileToAsync(
                "dangling",
                new MemoryStream(),
                TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<InvalidDataException>(
            () => fileSystem.CopyFileToAsync(
                "a",
                new MemoryStream(),
                TestContext.Current.CancellationToken));

        using IDockerRegistryClient rootClient = CreateClient(
            [CreateLayer(
                Entry.File("outside", "root"),
                Entry.SymbolicLink("escape", "../../outside"))]).Object;
        ImageFileSystem rootFileSystem = await ImageFileSystem.CreateAsync(
            rootClient,
            ImageName,
            new PlatformOptionsBase(),
            TestContext.Current.CancellationToken);
        using MemoryStream rootOutput = new();
        await rootFileSystem.CopyFileToAsync(
            "escape",
            rootOutput,
            TestContext.Current.CancellationToken);
        Assert.Equal("root", Encoding.UTF8.GetString(rootOutput.ToArray()));
    }

    [Theory]
    [InlineData("../escape")]
    [InlineData("/absolute")]
    [InlineData(@"windows\path")]
    public async Task Index_RejectsUnsafeArchivePaths(string path)
    {
        using IDockerRegistryClient client =
            CreateClient([CreateLayer(Entry.File(path, "unsafe"))]).Object;

        await Assert.ThrowsAsync<InvalidDataException>(
            () => ImageFileSystem.CreateAsync(
                client,
                ImageName,
                new PlatformOptionsBase(),
                TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Index_WhenLayerArchiveIsInvalid_ProvidesLayerContext()
    {
        using IDockerRegistryClient client =
            CreateClient([Encoding.UTF8.GetBytes("not a gzip archive")]).Object;

        InvalidDataException exception = await Assert.ThrowsAsync<InvalidDataException>(
            () => ImageFileSystem.CreateAsync(
                client,
                ImageName,
                new PlatformOptionsBase(),
                TestContext.Current.CancellationToken));

        Assert.Contains("Layer 0 ('sha256:layer-0')", exception.Message);
        Assert.NotNull(exception.InnerException);
    }

    [Fact]
    public async Task Extract_AssemblesDirectoryAcrossLayersAndDoesNotOverwrite()
    {
        string output = Path.Combine(
            Path.GetTempPath(),
            $"dredge-filesystem-{Guid.NewGuid():N}");
        string rootOutput = $"{output}-root";
        byte[][] layers =
        [
            CreateLayer(
                Entry.Directory("tree/empty"),
                Entry.File("tree/one", "one"),
                Entry.HardLink("tree/hard", "tree/one"),
                Entry.File("tree/repeated", "old"),
                Entry.HardLink("tree/repeated-hard", "tree/repeated"),
                Entry.File("tree/repeated", "new"),
                Entry.File("tree/change", "old"),
                Entry.SymbolicLink("tree/dangling", "missing"),
                Entry.HardLink("tree/symlink-hard", "tree/dangling"),
                Entry.SymbolicLink("tree/outside", "../outside-target"),
                Entry.File("outside-target", "outside"),
                Entry.HardLink("tree/outside-hard", "outside-target")),
            CreateLayer(
                Entry.File("tree/change", "new"),
                Entry.File("tree/two", "two"))
        ];
        using IDockerRegistryClient client = CreateClient(layers).Object;
        ImageFileSystem fileSystem = await ImageFileSystem.CreateAsync(
            client,
            ImageName,
            new PlatformOptionsBase(),
            TestContext.Current.CancellationToken);

        try
        {
            await fileSystem.ExtractAsync(
                "tree",
                output,
                TestContext.Current.CancellationToken);

            Assert.Equal("one", File.ReadAllText(Path.Combine(output, "one")));
            Assert.Equal("one", File.ReadAllText(Path.Combine(output, "hard")));
            File.WriteAllText(Path.Combine(output, "one"), "linked");
            Assert.Equal("linked", File.ReadAllText(Path.Combine(output, "hard")));
            Assert.Equal("new", File.ReadAllText(Path.Combine(output, "repeated")));
            Assert.Equal("old", File.ReadAllText(Path.Combine(output, "repeated-hard")));
            File.WriteAllText(Path.Combine(output, "repeated"), "changed");
            Assert.Equal("old", File.ReadAllText(Path.Combine(output, "repeated-hard")));
            Assert.Equal("new", File.ReadAllText(Path.Combine(output, "change")));
            Assert.Equal("two", File.ReadAllText(Path.Combine(output, "two")));
            Assert.True(Directory.Exists(Path.Combine(output, "empty")));
            Assert.Equal("missing", new FileInfo(Path.Combine(output, "dangling")).LinkTarget);
            Assert.Equal(
                "missing",
                new FileInfo(Path.Combine(output, "symlink-hard")).LinkTarget);
            Assert.Equal(
                "../outside-target",
                new FileInfo(Path.Combine(output, "outside")).LinkTarget);
            Assert.Equal(
                "outside",
                File.ReadAllText(Path.Combine(output, "outside-hard")));
            await Assert.ThrowsAsync<IOException>(
                () => fileSystem.ExtractAsync(
                    "tree",
                    output,
                    TestContext.Current.CancellationToken));
            await fileSystem.ExtractAsync(
                "/",
                rootOutput,
                TestContext.Current.CancellationToken);
            Assert.Equal(
                "two",
                File.ReadAllText(Path.Combine(rootOutput, "tree", "two")));
        }

        finally
        {
            if (Directory.Exists(output))
            {
                Directory.Delete(output, recursive: true);
            }
            if (Directory.Exists(rootOutput))
            {
                Directory.Delete(rootOutput, recursive: true);
            }
        }
    }

    [Fact]
    public async Task Extract_HardLinkSurvivesWhiteoutedTargetParent()
    {
        string output = Path.Combine(
            Path.GetTempPath(),
            $"dredge-filesystem-{Guid.NewGuid():N}");
        using IDockerRegistryClient client = CreateClient(
        [
            CreateLayer(
                Entry.File("original/file", "content"),
                Entry.HardLink("survivor", "original/file")),
            CreateLayer(Entry.File(".wh.original", ""))
        ]).Object;
        ImageFileSystem fileSystem = await ImageFileSystem.CreateAsync(
            client,
            ImageName,
            new PlatformOptionsBase(),
            TestContext.Current.CancellationToken);

        try
        {
            ImageFileSystemEntry listed = Assert.Single(
                fileSystem.List("survivor", recursive: false, showDeleted: false));
            Assert.Equal(7, listed.Size);

            await fileSystem.ExtractAsync(
                "survivor",
                output,
                TestContext.Current.CancellationToken);

            Assert.Equal("content", File.ReadAllText(output));
        }
        finally
        {
            File.Delete(output);
        }
    }

    [Fact]
    public async Task Extract_DirectoryRejectsUnsupportedDescendant()
    {
        string output = Path.Combine(
            Path.GetTempPath(),
            $"dredge-filesystem-{Guid.NewGuid():N}");
        using IDockerRegistryClient client = CreateClient(
        [
            CreateLayer(
                Entry.File("tree/file", "content"),
                Entry.Other("tree/device"))
        ]).Object;
        ImageFileSystem fileSystem = await ImageFileSystem.CreateAsync(
            client,
            ImageName,
            new PlatformOptionsBase(),
            TestContext.Current.CancellationToken);

        NotSupportedException exception = await Assert.ThrowsAsync<NotSupportedException>(
            () => fileSystem.ExtractAsync(
                "tree",
                output,
                TestContext.Current.CancellationToken));

        Assert.Contains("/tree/device", exception.Message);
        Assert.False(File.Exists(output));
        Assert.False(Directory.Exists(output));
    }

    [Fact]
    public async Task Extract_HandlesLargeFileSet()
    {
        string output = Path.Combine(
            Path.GetTempPath(),
            $"dredge-filesystem-{Guid.NewGuid():N}");
        Entry[] files = Enumerable.Range(0, 1200)
            .Select(index => Entry.File($"tree/file-{index}", index.ToString()))
            .ToArray();
        using IDockerRegistryClient client =
            CreateClient([CreateLayer(files)]).Object;
        ImageFileSystem fileSystem = await ImageFileSystem.CreateAsync(
            client,
            ImageName,
            new PlatformOptionsBase(),
            TestContext.Current.CancellationToken);

        try
        {
            await fileSystem.ExtractAsync(
                "tree",
                output,
                TestContext.Current.CancellationToken);

            Assert.Equal(
                "1199",
                File.ReadAllText(Path.Combine(output, "file-1199")));
        }

        finally
        {
            if (Directory.Exists(output))
            {
                Directory.Delete(output, recursive: true);
            }
        }
    }

    [Fact]
    public async Task Extract_AllowsUserSelectedSymlinkedParent()
    {
        string root = Path.Combine(
            Path.GetTempPath(),
            $"dredge-filesystem-{Guid.NewGuid():N}");
        string actualParent = Path.Combine(root, "actual");
        string linkedParent = Path.Combine(root, "linked");
        string output = Path.Combine(linkedParent, "file");
        using IDockerRegistryClient client =
            CreateClient([CreateLayer(Entry.File("file", "value"))]).Object;
        ImageFileSystem fileSystem = await ImageFileSystem.CreateAsync(
            client,
            ImageName,
            new PlatformOptionsBase(),
            TestContext.Current.CancellationToken);

        try
        {
            Directory.CreateDirectory(actualParent);
            Directory.CreateSymbolicLink(linkedParent, actualParent);

            await fileSystem.ExtractAsync(
                "file",
                output,
                TestContext.Current.CancellationToken);

            Assert.Equal("value", File.ReadAllText(Path.Combine(actualParent, "file")));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public async Task Extract_WhenLaterLayerFails_RemovesPartialOutput()
    {
        string output = Path.Combine(
            Path.GetTempPath(),
            $"dredge-filesystem-{Guid.NewGuid():N}");
        byte[][] layers =
        [
            CreateLayer(Entry.File("tree/first", "first")),
            CreateLayer(Entry.File("tree/second", "second"))
        ];
        Mock<IDockerRegistryClient> client = CreateClient(layers);
        int secondLayerRequests = 0;
        client
            .Setup(item => item.Blobs.GetAsync(
                ImageName.Repo,
                "sha256:layer-1",
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                secondLayerRequests++;
                if (secondLayerRequests > 1)
                {
                    throw new IOException("Layer download failed.");
                }
                return new MemoryStream(layers[1]);
            });
        ImageFileSystem fileSystem = await ImageFileSystem.CreateAsync(
            client.Object,
            ImageName,
            new PlatformOptionsBase(),
            TestContext.Current.CancellationToken);

        IOException exception = await Assert.ThrowsAsync<IOException>(
            () => fileSystem.ExtractAsync(
                "tree",
                output,
                TestContext.Current.CancellationToken));

        Assert.Equal("Layer download failed.", exception.Message);
        Assert.False(Directory.Exists(output));
    }

    [Fact]
    public async Task Extract_WhenSingleFileFails_RemovesCreatedParentDirectories()
    {
        string outputRoot = Path.Combine(
            Path.GetTempPath(),
            $"dredge-filesystem-{Guid.NewGuid():N}");
        string output = Path.Combine(outputRoot, "nested", "file");
        byte[] layer = CreateLayer(Entry.File("file", "value"));
        Mock<IDockerRegistryClient> client = CreateClient([layer]);
        int layerRequests = 0;
        client
            .Setup(item => item.Blobs.GetAsync(
                ImageName.Repo,
                "sha256:layer-0",
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                layerRequests++;
                if (layerRequests > 1)
                {
                    throw new IOException("Layer download failed.");
                }
                return new MemoryStream(layer);
            });
        ImageFileSystem fileSystem = await ImageFileSystem.CreateAsync(
            client.Object,
            ImageName,
            new PlatformOptionsBase(),
            TestContext.Current.CancellationToken);

        IOException exception = await Assert.ThrowsAsync<IOException>(
            () => fileSystem.ExtractAsync(
                "file",
                output,
                TestContext.Current.CancellationToken));

        Assert.Equal("Layer download failed.", exception.Message);
        Assert.False(Directory.Exists(outputRoot));
    }

    [Fact]
    public async Task Extract_WhenDirectoryFails_RemovesCreatedParentDirectories()
    {
        string outputRoot = Path.Combine(
            Path.GetTempPath(),
            $"dredge-filesystem-{Guid.NewGuid():N}");
        string output = Path.Combine(outputRoot, "nested", "tree");
        byte[] layer = CreateLayer(Entry.File("tree/file", "value"));
        Mock<IDockerRegistryClient> client = CreateClient([layer]);
        int layerRequests = 0;
        client
            .Setup(item => item.Blobs.GetAsync(
                ImageName.Repo,
                "sha256:layer-0",
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                layerRequests++;
                if (layerRequests > 1)
                {
                    throw new IOException("Layer download failed.");
                }
                return new MemoryStream(layer);
            });
        ImageFileSystem fileSystem = await ImageFileSystem.CreateAsync(
            client.Object,
            ImageName,
            new PlatformOptionsBase(),
            TestContext.Current.CancellationToken);

        IOException exception = await Assert.ThrowsAsync<IOException>(
            () => fileSystem.ExtractAsync(
                "tree",
                output,
                TestContext.Current.CancellationToken));

        Assert.Equal("Layer download failed.", exception.Message);
        Assert.False(Directory.Exists(outputRoot));
    }

    [Fact]
    public async Task Operations_RejectTraversalAndHonorCancellation()
    {
        using IDockerRegistryClient client =
            CreateClient([CreateLayer(Entry.File("file", "value"))]).Object;
        ImageFileSystem fileSystem = await ImageFileSystem.CreateAsync(
            client,
            ImageName,
            new PlatformOptionsBase(),
            TestContext.Current.CancellationToken);

        Assert.Throws<InvalidDataException>(
            () => fileSystem.List("../outside", recursive: false, showDeleted: false));
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => fileSystem.CopyFileToAsync(
                "file",
                new MemoryStream(),
                cancellation.Token));
    }

    [Theory]
    [InlineData("tree/name:stream")]
    [InlineData("tree/name.")]
    [InlineData("tree/name ")]
    [InlineData("tree/NUL.txt")]
    [InlineData("tree/COM1")]
    [InlineData("tree/CONIN$")]
    [InlineData("tree/CONOUT$.txt")]
    public async Task Extract_OnWindowsRejectsInvalidDestinationNames(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        string output = Path.Combine(
            Path.GetTempPath(),
            $"dredge-filesystem-{Guid.NewGuid():N}");
        using IDockerRegistryClient client =
            CreateClient([CreateLayer(Entry.File(path, "value"))]).Object;
        ImageFileSystem fileSystem = await ImageFileSystem.CreateAsync(
            client,
            ImageName,
            new PlatformOptionsBase(),
            TestContext.Current.CancellationToken);

        InvalidDataException exception = await Assert.ThrowsAsync<InvalidDataException>(
            () => fileSystem.ExtractAsync(
                "tree",
                output,
                TestContext.Current.CancellationToken));

        Assert.Contains("valid Windows path", exception.Message);
        Assert.False(Directory.Exists(output));
    }

    [Fact]
    public async Task LsCommand_ProducesCamelCaseJsonAndForwardsPlatformOptions()
    {
        byte[][] layers = [CreateLayer(Entry.File("file", "value"))];
        Mock<IDockerRegistryClient> client = CreateClient(layers);
        Mock<IDockerRegistryClientFactory> factory = new();
        factory.Setup(item => item.GetClientAsync("registry.test")).ReturnsAsync(client.Object);
        StringWriter writer = new();
        IAnsiConsole console = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Ansi = AnsiSupport.No,
            ColorSystem = ColorSystemSupport.NoColors,
            Out = new AnsiConsoleOutput(writer)
        });
        LsCommand command = new TestLsCommand(factory.Object, console);

        int exitCode = await command
            .Parse([ImageName.ToString(), "--output", "json", "--os", "linux", "--arch", "amd64"])
            .InvokeAsync(
                new InvocationConfiguration(),
                TestContext.Current.CancellationToken);

        Assert.Equal(0, exitCode);
        using JsonDocument json = JsonDocument.Parse(writer.ToString());
        JsonElement item = Assert.Single(json.RootElement.EnumerateArray());
        Assert.Equal("file", item.GetProperty("path").GetString());
        Assert.Equal("File", item.GetProperty("type").GetString());
        Assert.EndsWith("Z", item.GetProperty("modifiedTime").GetString());
        Assert.Equal(0, item.GetProperty("introducedLayer").GetProperty("index").GetInt32());
        Assert.False(item.TryGetProperty("Path", out _));
        Assert.Equal("linux", command.Options.Os);
        Assert.Equal("amd64", command.Options.Architecture);
    }

    [Fact]
    public async Task LsCommand_HumanOutputUsesLsStyleDetailLevels()
    {
        Mock<IDockerRegistryClient> client =
            CreateClient([CreateLayer(
                Entry.File("dir/file", "value"),
                Entry.SymbolicLink("dir/link", "file"),
                Entry.File("dir/second", "value"))]);
        Mock<IDockerRegistryClientFactory> factory = new();
        factory.Setup(item => item.GetClientAsync("registry.test")).ReturnsAsync(client.Object);

        string defaultOutput = await InvokeLsCommandAsync(factory.Object, "/dir");
        string longOutput = await InvokeLsCommandAsync(factory.Object, "/dir", "-l");
        string provenanceOutput = await InvokeLsCommandAsync(
            factory.Object,
            "/dir",
            "--provenance");
        string combinedOutput = await InvokeLsCommandAsync(
            factory.Object,
            "/dir",
            "--long",
            "--provenance");

        Assert.Equal(
            ["file", "link", "second"],
            defaultOutput.Split(
                Environment.NewLine,
                StringSplitOptions.RemoveEmptyEntries));
        Assert.DoesNotContain("dir/", defaultOutput);
        Assert.DoesNotContain("rwx", defaultOutput);
        Assert.DoesNotContain("i=", defaultOutput);
        Assert.Contains("-rwxr-xr-x", longOutput);
        Assert.Contains("1970-01-01 00:00Z", longOutput);
        Assert.Contains("link -> file", longOutput);
        Assert.DoesNotContain("i=", longOutput);
        Assert.Contains("i=", provenanceOutput);
        Assert.Contains("0:layer-0", provenanceOutput);
        Assert.DoesNotContain("rwx", provenanceOutput);
        Assert.DoesNotContain("->", provenanceOutput);
        Assert.Contains("-rwxr-xr-x", combinedOutput);
        Assert.Contains("i=", combinedOutput);
        Assert.All(
            longOutput.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries),
            line => Assert.Equal(line.TrimEnd(), line));
        Assert.All(
            combinedOutput.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries),
            line => Assert.Equal(line.TrimEnd(), line));
    }

    [Fact]
    public async Task CatCommand_WritesOnlyBinaryContentToOutputStream()
    {
        byte[] content = [0, 10, 255, 42];
        Mock<IDockerRegistryClient> client =
            CreateClient([CreateLayer(Entry.File("file", content))]);
        Mock<IDockerRegistryClientFactory> factory = new();
        factory.Setup(item => item.GetClientAsync("registry.test")).ReturnsAsync(client.Object);
        using MemoryStream output = new();
        CatCommand command = new TestCatCommand(factory.Object, output);

        int exitCode = await command
            .Parse([ImageName.ToString(), "file"])
            .InvokeAsync(
                new InvocationConfiguration(),
                TestContext.Current.CancellationToken);

        Assert.Equal(0, exitCode);
        Assert.Equal(content, output.ToArray());
    }

    [Fact]
    public async Task Create_RejectsWindowsImagesBeforeReadingLayers()
    {
        Mock<IDockerRegistryClient> client =
            CreateClient([CreateLayer(Entry.File("file", "value"))], os: "windows");

        NotSupportedException exception = await Assert.ThrowsAsync<NotSupportedException>(
            () => ImageFileSystem.CreateAsync(
                client.Object,
                ImageName,
                new PlatformOptionsBase(),
                TestContext.Current.CancellationToken));

        Assert.Contains("Linux", exception.Message);
        Assert.Contains("Windows", exception.Message);
        client.Verify(
            item => item.Blobs.GetAsync(
                ImageName.Repo,
                "sha256:layer-0",
                It.IsAny<CancellationToken>()),
            Times.Never);
    }

    private static async Task<string> InvokeLsCommandAsync(
        IDockerRegistryClientFactory factory,
        params string[] options)
    {
        StringWriter writer = new();
        IAnsiConsole console = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Ansi = AnsiSupport.No,
            ColorSystem = ColorSystemSupport.NoColors,
            Out = new AnsiConsoleOutput(writer)
        });
        TestLsCommand command = new(factory, console);

        int exitCode = await command
            .Parse([ImageName.ToString(), .. options])
            .InvokeAsync(
                new InvocationConfiguration(),
                TestContext.Current.CancellationToken);

        Assert.Equal(0, exitCode);
        return writer.ToString();
    }

    private sealed class TestLsCommand : LsCommand
    {
        public TestLsCommand(
            IDockerRegistryClientFactory dockerRegistryClientFactory,
            IAnsiConsole console)
            : base(dockerRegistryClientFactory, console)
        {
        }

        protected override TextWriter Error => TextWriter.Null;

        protected override void Exit(int exitCode) =>
            throw new InvalidOperationException($"Command exited with code {exitCode}.");
    }

    private sealed class TestCatCommand : CatCommand
    {
        public TestCatCommand(
            IDockerRegistryClientFactory dockerRegistryClientFactory,
            Stream output)
            : base(dockerRegistryClientFactory, output)
        {
        }

        protected override TextWriter Error => TextWriter.Null;

        protected override void Exit(int exitCode) =>
            throw new InvalidOperationException($"Command exited with code {exitCode}.");
    }

    private static Mock<IDockerRegistryClient> CreateClient(
        byte[][] layers,
        string os = "linux")
    {
        Mock<IDockerRegistryClient> client = new() { DefaultValue = DefaultValue.Mock };
        DockerManifest manifest = new()
        {
            Config = new ManifestConfig { Digest = ConfigDigest },
            Layers = layers
                .Select((_, index) => new ManifestLayer { Digest = $"sha256:layer-{index}" })
                .ToArray()
        };
        client
            .Setup(item => item.Manifests.GetAsync(
                ImageName.Repo,
                ImageName.Tag!,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ManifestInfo(
                "application/vnd.oci.image.manifest.v1+json",
                "sha256:manifest",
                manifest));
        client
            .Setup(item => item.Blobs.GetAsync(
                ImageName.Repo,
                ConfigDigest,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new MemoryStream(
                Encoding.UTF8.GetBytes($"{{\"os\":\"{os}\"}}")));
        for (int index = 0; index < layers.Length; index++)
        {
            int captured = index;
            client
                .Setup(item => item.Blobs.GetAsync(
                    ImageName.Repo,
                    $"sha256:layer-{captured}",
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => new MemoryStream(layers[captured]));
        }
        return client;
    }

    private static byte[] CreateLayer(params Entry[] entries)
    {
        return CreateLayer(TarEntryFormat.Pax, entries);
    }

    private static byte[] CreateLayer(TarEntryFormat format, params Entry[] entries)
    {
        using MemoryStream result = new();
        using (GZipStream gzip = new(result, CompressionMode.Compress, leaveOpen: true))
        using (TarWriter writer = new(gzip, format, leaveOpen: true))
        {
            foreach (Entry definition in entries)
            {
                TarEntry entry = format == TarEntryFormat.Gnu
                    ? new GnuTarEntry(definition.Type, definition.Path)
                    : new PaxTarEntry(definition.Type, definition.Path);
                entry.Mode = (UnixFileMode)Convert.ToInt32("755", 8);
                entry.ModificationTime = DateTimeOffset.FromUnixTimeSeconds(1);
                if (definition.LinkTarget is not null)
                {
                    entry.LinkName = definition.LinkTarget;
                }
                if (definition.Content is not null)
                {
                    entry.DataStream = new MemoryStream(definition.Content);
                }
                writer.WriteEntry(entry);
            }
        }
        return result.ToArray();
    }

    private sealed record Entry(
        TarEntryType Type,
        string Path,
        byte[]? Content,
        string? LinkTarget)
    {
        public static Entry File(string path, string content) =>
            File(path, Encoding.UTF8.GetBytes(content));

        public static Entry File(string path, byte[] content) =>
            new(TarEntryType.RegularFile, path, content, null);

        public static Entry Directory(string path) =>
            new(TarEntryType.Directory, path, null, null);

        public static Entry SymbolicLink(string path, string target) =>
            new(TarEntryType.SymbolicLink, path, null, target);

        public static Entry HardLink(string path, string target) =>
            new(TarEntryType.HardLink, path, null, target);

        public static Entry Other(string path) =>
            new(TarEntryType.Fifo, path, null, null);
    }
}
