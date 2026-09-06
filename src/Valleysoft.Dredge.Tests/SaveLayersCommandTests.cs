namespace Valleysoft.Dredge.Tests;

using System.Formats.Tar;
using System.IO.Compression;
using System.Text;
using Valleysoft.DockerRegistryClient.Models.Manifests;
using Valleysoft.DockerRegistryClient.Models.Manifests.Docker;
using Valleysoft.Dredge.Commands.Image;

public class SaveLayersCommandTests
{
    [Fact]
    public async Task ExecuteAsync_WhenOutputDirectoryIsNotEmpty_RejectsBeforeRegistryAccess()
    {
        string outputPath = CreateOutputPath();
        string existingPath = Path.Combine(outputPath, "existing.txt");
        Directory.CreateDirectory(outputPath);
        await File.WriteAllTextAsync(existingPath, "existing", TestContext.Current.CancellationToken);
        Mock<IDockerRegistryClientFactory> factory = new();
        using StringWriter error = new();
        TestSaveLayersCommand command = new(factory.Object, error)
        {
            Options = new SaveLayersOptions
            {
                Image = "image",
                OutputPath = outputPath
            }
        };

        try
        {
            CommandExitException exception =
                await Assert.ThrowsAsync<CommandExitException>(command.RunAsync);

            Assert.Equal(1, exception.ExitCode);
            Assert.Equal("existing", await File.ReadAllTextAsync(
                existingPath,
                TestContext.Current.CancellationToken));
            Assert.Contains("is not empty", error.ToString());
            Assert.Contains("--force", error.ToString());
            factory.Verify(
                item => item.GetClientAsync(It.IsAny<string?>()),
                Times.Never);
        }
        finally
        {
            Directory.Delete(outputPath, recursive: true);
        }
    }

    [Fact]
    public async Task ExecuteAsync_WithForce_OverwritesAndDeletesExistingContent()
    {
        string id = Guid.NewGuid().ToString("N");
        string digest = $"sha256:{id}";
        string outputPath = CreateOutputPath();
        string layerCachePath = Path.Combine(DredgeState.DredgeTempPath, "layers", id);
        Directory.CreateDirectory(outputPath);
        string overwritePath = Path.Combine(outputPath, "overwrite.txt");
        Directory.CreateDirectory(overwritePath);
        await File.WriteAllTextAsync(
            Path.Combine(overwritePath, "old.txt"),
            "old",
            TestContext.Current.CancellationToken);
        string directoryPath = Path.Combine(outputPath, "directory");
        await File.WriteAllTextAsync(
            directoryPath,
            "old",
            TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(
            Path.Combine(outputPath, "delete.txt"), "delete", TestContext.Current.CancellationToken);
        string linkTargetPath = Path.Combine(outputPath, "link-target.txt");
        string linkPath = Path.Combine(outputPath, "link.txt");
        await File.WriteAllTextAsync(
            linkTargetPath, "target", TestContext.Current.CancellationToken);
        File.CreateSymbolicLink(linkPath, linkTargetPath);
        Mock<IDockerRegistryClient> client = CreateClient(
            digest,
            () => CreateLayer(
                ("overwrite.txt", "new"),
                ("directory/new.txt", "new"),
                ("link.txt", "new"),
                (".wh.delete.txt", string.Empty)));
        Mock<IDockerRegistryClientFactory> factory = new();
        factory.Setup(item => item.GetClientAsync(null)).ReturnsAsync(client.Object);
        TestSaveLayersCommand command = new(factory.Object, TextWriter.Null)
        {
            Options = new SaveLayersOptions
            {
                Image = "image",
                OutputPath = outputPath,
                Force = true
            }
        };

        try
        {
            await command.RunAsync();

            Assert.Equal("new", await File.ReadAllTextAsync(
                overwritePath,
                TestContext.Current.CancellationToken));
            Assert.Equal("new", await File.ReadAllTextAsync(
                Path.Combine(directoryPath, "new.txt"),
                TestContext.Current.CancellationToken));
            Assert.Equal("new", await File.ReadAllTextAsync(
                linkPath,
                TestContext.Current.CancellationToken));
            Assert.Equal("target", await File.ReadAllTextAsync(
                linkTargetPath,
                TestContext.Current.CancellationToken));
            Assert.False(File.Exists(Path.Combine(outputPath, "delete.txt")));
        }
        finally
        {
            Directory.Delete(outputPath, recursive: true);
            if (Directory.Exists(layerCachePath))
            {
                Directory.Delete(layerCachePath, recursive: true);
            }
        }
    }

    [Fact]
    public async Task ExecuteAsync_WithForceAndNoSquash_ReplacesExistingLayerDirectory()
    {
        string id = Guid.NewGuid().ToString("N");
        string digest = $"sha256:{id}";
        string outputPath = CreateOutputPath();
        string layerOutputPath = Path.Combine(outputPath, $"layer0-{id}");
        string layerCachePath = Path.Combine(DredgeState.DredgeTempPath, "layers", id);
        Directory.CreateDirectory(layerOutputPath);
        await File.WriteAllTextAsync(
            Path.Combine(layerOutputPath, "existing.txt"),
            "old",
            TestContext.Current.CancellationToken);
        Mock<IDockerRegistryClient> client = CreateClient(
            digest,
            () => CreateLayer(("existing.txt", "new")));
        Mock<IDockerRegistryClientFactory> factory = new();
        factory.Setup(item => item.GetClientAsync(null)).ReturnsAsync(client.Object);
        TestSaveLayersCommand command = new(factory.Object, TextWriter.Null)
        {
            Options = new SaveLayersOptions
            {
                Image = "image",
                OutputPath = outputPath,
                NoSquash = true,
                Force = true
            }
        };

        try
        {
            await command.RunAsync();

            Assert.Equal("new", await File.ReadAllTextAsync(
                Path.Combine(layerOutputPath, "existing.txt"),
                TestContext.Current.CancellationToken));
        }
        finally
        {
            Directory.Delete(outputPath, recursive: true);
            if (Directory.Exists(layerCachePath))
            {
                Directory.Delete(layerCachePath, recursive: true);
            }
        }
    }

    [Fact]
    public async Task ExecuteAsync_WhenOutputPathIsFile_RejectsEvenWithForce()
    {
        string outputPath = Path.Combine(
            Path.GetTempPath(),
            $"dredge-output-{Guid.NewGuid():N}");
        await File.WriteAllTextAsync(
            outputPath,
            "existing",
            TestContext.Current.CancellationToken);
        Mock<IDockerRegistryClientFactory> factory = new();
        using StringWriter error = new();
        TestSaveLayersCommand command = new(factory.Object, error)
        {
            Options = new SaveLayersOptions
            {
                Image = "image",
                OutputPath = outputPath,
                Force = true
            }
        };

        try
        {
            CommandExitException exception =
                await Assert.ThrowsAsync<CommandExitException>(command.RunAsync);

            Assert.Equal(1, exception.ExitCode);
            Assert.Contains("is an existing file", error.ToString());
            factory.Verify(
                item => item.GetClientAsync(It.IsAny<string?>()),
                Times.Never);
        }
        finally
        {
            File.Delete(outputPath);
        }
    }

    [Fact]
    public async Task ExecuteAsync_WhenOutputPathIsSymbolicLink_RejectsBeforeRegistryAccess()
    {
        string rootPath = CreateOutputPath();
        string actualPath = Path.Combine(rootPath, "actual");
        string linkPath = Path.Combine(rootPath, "link");
        string sentinelPath = Path.Combine(actualPath, "collision", "sentinel.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(sentinelPath)!);
        await File.WriteAllTextAsync(
            sentinelPath,
            "sentinel",
            TestContext.Current.CancellationToken);
        Directory.CreateSymbolicLink(linkPath, actualPath);
        Mock<IDockerRegistryClientFactory> factory = new();
        using StringWriter error = new();
        TestSaveLayersCommand command = new(factory.Object, error)
        {
            Options = new SaveLayersOptions
            {
                Image = "image",
                OutputPath = linkPath,
                Force = true
            }
        };

        try
        {
            CommandExitException exception =
                await Assert.ThrowsAsync<CommandExitException>(command.RunAsync);

            Assert.Equal(1, exception.ExitCode);
            Assert.Contains("symbolic link", error.ToString());
            Assert.Equal("sentinel", await File.ReadAllTextAsync(
                sentinelPath,
                TestContext.Current.CancellationToken));
            factory.Verify(
                item => item.GetClientAsync(It.IsAny<string?>()),
                Times.Never);
        }
        finally
        {
            Directory.Delete(linkPath);
            Directory.Delete(rootPath, recursive: true);
        }
    }

    [Fact]
    public void ValidateDestinationPath_WhenAncestorIsSymbolicLink_AllowsDestination()
    {
        string rootPath = CreateOutputPath();
        string actualPath = Path.Combine(rootPath, "actual");
        string linkPath = Path.Combine(rootPath, "link");
        Directory.CreateDirectory(actualPath);
        Directory.CreateSymbolicLink(linkPath, actualPath);

        try
        {
            ImageHelper.ValidateDestinationPath(Path.Combine(linkPath, "output"));
        }
        finally
        {
            Directory.Delete(linkPath);
            Directory.Delete(rootPath, recursive: true);
        }
    }

    private static string CreateOutputPath() =>
        Path.Combine(Path.GetTempPath(), $"dredge-output-{Guid.NewGuid():N}");

    private static Mock<IDockerRegistryClient> CreateClient(
        string digest,
        Func<Stream> createLayer)
    {
        Mock<IDockerRegistryClient> client = new() { DefaultValue = DefaultValue.Mock };
        client
            .Setup(item => item.Manifests.GetAsync(
                "library/image",
                "latest",
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ManifestInfo(
                "application/test",
                "sha256:manifest",
                new DockerManifest
                {
                    Config = new ManifestConfig { Digest = "sha256:config" },
                    Layers = [new ManifestLayer { Digest = digest }]
                }));
        client
            .Setup(item => item.Blobs.GetAsync(
                "library/image",
                digest,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(createLayer);
        return client;
    }

    private static Stream CreateLayer(params (string Name, string Content)[] files)
    {
        MemoryStream compressed = new();
        using (GZipStream gzip = new(compressed, CompressionMode.Compress, leaveOpen: true))
        using (TarWriter writer = new(gzip, leaveOpen: true))
        {
            foreach ((string name, string content) in files)
            {
                PaxTarEntry entry = new(TarEntryType.RegularFile, name)
                {
                    DataStream = new MemoryStream(Encoding.UTF8.GetBytes(content))
                };
                writer.WriteEntry(entry);
            }
        }
        compressed.Position = 0;
        return compressed;
    }

    private sealed class TestSaveLayersCommand : SaveLayersCommand
    {
        private readonly TextWriter error;

        public TestSaveLayersCommand(
            IDockerRegistryClientFactory dockerRegistryClientFactory,
            TextWriter error)
            : base(dockerRegistryClientFactory)
        {
            this.error = error;
        }

        public Task RunAsync() => ExecuteAsync(TestContext.Current.CancellationToken);

        protected override TextWriter Error => error;

        protected override void Exit(int exitCode) => throw new CommandExitException(exitCode);
    }

    private sealed class CommandExitException : Exception
    {
        public CommandExitException(int exitCode)
        {
            ExitCode = exitCode;
        }

        public int ExitCode { get; }
    }
}
