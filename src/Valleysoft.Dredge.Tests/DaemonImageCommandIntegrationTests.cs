using System.CommandLine;
using System.Formats.Tar;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Docker.DotNet;
using Docker.DotNet.Models;
using Spectre.Console;
using Valleysoft.DockerRegistryClient.Models.Manifests;
using Valleysoft.DockerRegistryClient.Models.Manifests.Docker;
using Valleysoft.Dredge.Commands;
using Valleysoft.Dredge.Commands.Image;
using static Valleysoft.Dredge.Tests.LayerCacheTestContext;

namespace Valleysoft.Dredge.Tests;

[Trait("Category", "Integration")]
public class DaemonImageCommandIntegrationTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task UnpublishedImage_CommandsAndAllComparisonSourcesUseEquivalentContent(bool includeHistory)
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using DockerClient engine = new DockerClientBuilder().Build();
        string tag = $"dredge-405-tests-{Guid.NewGuid():N}:local";
        byte[] rawLayer = CreateTar(
            ("hello", Encoding.UTF8.GetBytes("unpublished image\n")),
            ("etc/os-release", Encoding.UTF8.GetBytes("ID=alpine\nVERSION_ID=3.21\n")));
        byte[] config = JsonSerializer.SerializeToUtf8Bytes(new
        {
            architecture = "amd64",
            os = "linux",
            config = new
            {
                Labels = new Dictionary<string, string> { ["dredge.test"] = tag },
                ExposedPorts = new Dictionary<string, object> { ["8080/tcp"] = new { } },
                Volumes = new Dictionary<string, object> { ["/Data/WithCase"] = new { } }
            },
            rootfs = new { type = "layers", diff_ids = new[] { Digest(rawLayer) } },
            history = new[] { new { created_by = "COPY hello /hello" } }
        });
        if (!includeHistory)
        {
            JsonObject configuration = JsonNode.Parse(config)!.AsObject();
            configuration.Remove("history");
            config = JsonSerializer.SerializeToUtf8Bytes(configuration);
        }
        string id = Digest(config);
        byte[] manifest = JsonSerializer.SerializeToUtf8Bytes(new[]
        {
            new { Config = "config.json", RepoTags = new[] { tag }, Layers = new[] { "layer.tar" } }
        });
        byte[] archive = CreateTar(("config.json", config), ("layer.tar", rawLayer), ("manifest.json", manifest));
        using MemoryStream input = new(archive);
        try
        {
            await engine.Images.LoadImageAsync(new ImageLoadParameters(), input, new Progress<JSONMessage>(), ct);
            await using LayerCacheTestContext cache = new();
            byte[] gzipLayer = Compress(rawLayer);
            Mock<IDockerRegistryClient> registry = new() { DefaultValue = DefaultValue.Mock };
            registry.Setup(client => client.Manifests.GetAsync("equivalent", "latest", It.IsAny<CancellationToken>()))
                .ReturnsAsync(new ManifestInfo(
                    "application/vnd.docker.distribution.manifest.v2+json", "sha256:registry-manifest",
                    new DockerManifest
                    {
                        Config = new ManifestConfig { Digest = id, Size = config.Length },
                        Layers = [new ManifestLayer { Digest = Digest(gzipLayer), Size = gzipLayer.Length }]
                    }));
            registry.Setup(client => client.Blobs.GetAsync("equivalent", id, It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => new MemoryStream(config));
            registry.Setup(client => client.Blobs.GetRangeAsync(
                    "equivalent", Digest(gzipLayer), 0, null, It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => new Valleysoft.DockerRegistryClient.BlobDownloadResult(
                    new MemoryStream(gzipLayer), false, null, null, gzipLayer.Length));
            Mock<IDockerRegistryClientFactory> factory = new(MockBehavior.Strict);
            factory.Setup(value => value.GetClientAsync("registry.example", It.IsAny<CancellationToken>()))
                .ReturnsAsync(registry.Object);
            IImageSourceFactory sources = new ImageSourceFactory(
                factory.Object, new TestAppSettingsStore(new AppSettings()));
            string reference = $"docker://{tag}";

            using StringWriter inspect = new();
            Assert.Equal(0, await InvokeAsync(new InspectCommand(factory.Object, inspect), sources, reference));
            Assert.Equal("linux", JsonNode.Parse(inspect.ToString())!["os"]!.GetValue<string>());
            Assert.True(JsonNode.Parse(inspect.ToString())!["config"]!["ExposedPorts"]!
                .AsObject().ContainsKey("8080/tcp"));

            using MemoryStream cat = new();
            Assert.Equal(0, await InvokeAsync(new CatCommand(factory.Object, cat, cache.Paths), sources, reference, "/hello"));
            Assert.Equal("unpublished image\n", Encoding.UTF8.GetString(cat.ToArray()));

            using StringWriter listing = new();
            Assert.Equal(0, await InvokeAsync(new LsCommand(factory.Object, ConsoleFor(listing), cache.Paths),
                sources, reference, "--recursive", "--output", "json"));
            Assert.Contains(JsonNode.Parse(listing.ToString())!.AsArray(),
                static entry => entry!["path"]!.GetValue<string>() == "hello");

            string extracted = Path.Combine(cache.Root, "extracted");
            Assert.Equal(0, await InvokeAsync(new ExtractCommand(factory.Object, cache.Paths),
                sources, reference, "/hello", extracted));
            Assert.Equal("unpublished image\n", await File.ReadAllTextAsync(extracted, ct));

            string saved = Path.Combine(cache.Root, "saved");
            Assert.Equal(0, await InvokeAsync(new SaveLayersCommand(factory.Object, cache.Paths),
                sources, reference, saved));
            Assert.Equal("unpublished image\n", await File.ReadAllTextAsync(Path.Combine(saved, "hello"), ct));

            using StringWriter os = new();
            Assert.Equal(0, await InvokeAsync(new OsCommand(factory.Object, os, cache.Paths), sources, reference));
            Assert.Equal("alpine", JsonNode.Parse(os.ToString())!["ID"]!.GetValue<string>());

            using StringWriter dockerfile = new();
            Assert.Equal(0, await InvokeAsync(new DockerfileCommand(factory.Object, ConsoleFor(dockerfile)),
                sources, reference, "--no-color"));
            Assert.Contains("FROM scratch", dockerfile.ToString());
            if (includeHistory)
            {
                Assert.Contains("COPY hello /hello", dockerfile.ToString());
            }
            factory.Verify(value => value.GetClientAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);

            foreach (string before in new[] { reference, "registry.example/equivalent:latest" })
            {
                foreach (string after in new[] { reference, "registry.example/equivalent:latest" })
                {
                    using StringWriter layers = new();
                    Assert.Equal(0, await InvokeAsync(new CompareLayersCommand(factory.Object, ConsoleFor(layers)),
                        sources, before, after, "--output", "json", "--compressed-size"));
                    JsonNode layerResult = JsonNode.Parse(layers.ToString())!;
                    Assert.True(layerResult["summary"]!["areEqual"]!.GetValue<bool>());
                    bool contentComparison = before.StartsWith("docker://", StringComparison.Ordinal) ||
                        after.StartsWith("docker://", StringComparison.Ordinal);
                    if (includeHistory || contentComparison)
                    {
                        JsonNode comparison = Assert.Single(layerResult["layerComparisons"]!.AsArray())!;
                        foreach ((string operand, string image) in new[] { ("base", before), ("target", after) })
                        {
                            Assert.Equal(contentComparison ? Digest(rawLayer) : Digest(gzipLayer),
                                comparison[operand]!["digest"]!.GetValue<string>());
                            if (image.StartsWith("docker://", StringComparison.Ordinal))
                            {
                                Assert.Null(comparison[operand]!["compressedSize"]);
                            }
                            else
                            {
                                Assert.Equal(gzipLayer.Length,
                                    comparison[operand]!["compressedSize"]!.GetValue<long>());
                            }
                        }
                    }

                    using StringWriter metadata = new();
                    Assert.Equal(0, await InvokeAsync(new CompareMetadataCommand(factory.Object, ConsoleFor(metadata)),
                        sources, before, after, "--output", "json"));
                    Assert.True(JsonNode.Parse(metadata.ToString())!["summary"]!["areEqual"]!.GetValue<bool>());

                    AppSettings comparisonSettings = new();
                    comparisonSettings.FileCompareTool.ExePath = "test-compare";
                    comparisonSettings.FileCompareTool.Args = "\"{0}\" \"{1}\"";
                    TestProcessLauncher launcher = new();
                    CompareFilesCommand files = new(factory.Object,
                        new TestAppSettingsStore(comparisonSettings), cache.Paths, launcher);
                    Assert.Equal(0, await InvokeAsync(files, sources, before, after));
                    Assert.Equal("test-compare", launcher.StartInfo?.FileName);
                    foreach (string side in new[] { "base", "target" })
                    {
                        Assert.Equal("unpublished image\n", await File.ReadAllTextAsync(
                            Path.Combine(cache.Paths.TempPath, "compare", side, "hello"), ct));
                    }
                }
            }
        }
        finally
        {
            try
            {
                await engine.Images.DeleteImageAsync(tag, new ImageDeleteParameters { Force = true }, CancellationToken.None);
            }
            catch (DockerApiException exception) when (exception.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
            }
        }
    }

    private static Task<int> InvokeAsync<TOptions>(
        RegistryCommandBase<TOptions> command, IImageSourceFactory sources, params string[] arguments)
        where TOptions : OptionsBase, new()
    {
        command.ImageSourceFactory = sources;
        ((IProcessTerminationAware)command).ProcessTerminator = new TestProcessTerminator();
        return command.Parse(arguments).InvokeAsync(new InvocationConfiguration(), TestContext.Current.CancellationToken);
    }

    private static IAnsiConsole ConsoleFor(TextWriter output) =>
        AnsiConsole.Create(new AnsiConsoleSettings { Out = new AnsiConsoleOutput(output) });

    private static byte[] Compress(byte[] bytes)
    {
        using MemoryStream result = new();
        using (GZipStream gzip = new(result, CompressionMode.Compress, leaveOpen: true))
        {
            gzip.Write(bytes);
        }
        return result.ToArray();
    }

    private static byte[] CreateTar(params (string Path, byte[] Bytes)[] files)
    {
        using MemoryStream result = new();
        using (TarWriter writer = new(result, leaveOpen: true))
        {
            foreach ((string path, byte[] bytes) in files)
            {
                using MemoryStream contents = new(bytes);
                writer.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, path)
                {
                    DataStream = contents,
                    Mode = UnixFileMode.UserRead | UnixFileMode.UserWrite |
                        UnixFileMode.GroupRead | UnixFileMode.OtherRead
                });
            }
        }
        return result.ToArray();
    }
}
