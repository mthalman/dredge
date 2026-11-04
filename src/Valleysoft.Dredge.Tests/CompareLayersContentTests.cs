using System.CommandLine;
using System.Formats.Tar;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Spectre.Console;
using Valleysoft.DockerRegistryClient;
using Valleysoft.DockerRegistryClient.Models.Images;
using Valleysoft.DockerRegistryClient.Models.Manifests;
using Valleysoft.DockerRegistryClient.Models.Manifests.Docker;
using Valleysoft.Dredge.Commands;
using Valleysoft.Dredge.Commands.Image;
using static Valleysoft.Dredge.Tests.LayerCacheTestContext;

namespace Valleysoft.Dredge.Tests;

public sealed class CompareLayersContentTests : IDisposable
{
    private readonly string root = Path.Combine(
        Directory.GetCurrentDirectory(), $".content-comparison-{Guid.NewGuid():N}");

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task MixedSources_VerifyRawAndGzipContentInBothDirections(bool registryFirst, bool gzip)
    {
        byte[] raw = Layer();
        Content registry = new(gzip ? Compress(raw) : raw);
        Content daemon = new(raw);
        Sources sources = new(registry, daemon, Digest(raw));
        using StringWriter output = new();
        TestCommand command = Command(sources, output);

        Assert.Equal(0, await InvokeAsync(command, registryFirst));

        JsonNode result = JsonNode.Parse(output.ToString())!;
        Assert.True(result["summary"]!["areEqual"]!.GetValue<bool>());
        JsonNode comparison = Assert.Single(result["layerComparisons"]!.AsArray())!;
        Assert.Equal(Digest(raw), comparison["base"]!["digest"]!.GetValue<string>());
        Assert.Equal(Digest(raw), comparison["target"]!["digest"]!.GetValue<string>());
        Assert.Equal(1, registry.Reads);
        Assert.Equal(0, daemon.Reads);
        Assert.True(registry.StreamDisposed);
        Assert.Equal(2, sources.Disposals);
        Assert.Empty(Directory.GetFiles(Path.Combine(root, "cache", "layer-store", "data"), "*.scratch"));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task MixedSources_RejectIncorrectAdvertisedDiffId(bool registryFirst, bool gzip)
    {
        byte[] raw = Layer();
        Content registry = new(gzip ? Compress(raw) : raw);
        Sources sources = new(registry, new(raw), "sha256:" + new string('a', 64));
        using StringWriter output = new();
        TestCommand command = Command(sources, output);

        Assert.Equal(1, await InvokeAsync(command, registryFirst));

        Assert.Equal(string.Empty, output.ToString());
        Assert.Contains("does not match the configuration rootfs diff ID", command.Diagnostics.ToString());
        Assert.Equal(1, registry.Reads);
        Assert.True(registry.StreamDisposed);
        Assert.Equal(2, sources.Disposals);
        Assert.Empty(Directory.GetFiles(Path.Combine(root, "cache", "layer-store", "data"), "*.scratch"));
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, false, false)]
    [InlineData(true, true, false)]
    [InlineData(false, false, true)]
    [InlineData(false, true, true)]
    [InlineData(true, false, true)]
    [InlineData(true, true, true)]
    public async Task MixedSources_NormalizeVerifiedDiffIdAlgorithms(bool registryFirst, bool gzip, bool daemonSha512)
    {
        byte[] raw = Layer();
        Content registry = new(gzip ? Compress(raw) : raw);
        string sha512 = "sha512:" + Convert.ToHexStringLower(SHA512.HashData(raw));
        Sources sources = new(registry, new(raw), daemonSha512 ? sha512 : Digest(raw))
        {
            RegistryDiffId = daemonSha512 ? Digest(raw) : sha512
        };
        using StringWriter output = new();
        TestCommand command = Command(sources, output);

        Assert.Equal(0, await InvokeAsync(command, registryFirst));

        JsonNode result = JsonNode.Parse(output.ToString())!;
        Assert.True(result["summary"]!["areEqual"]!.GetValue<bool>());
        JsonNode comparison = Assert.Single(result["layerComparisons"]!.AsArray())!;
        Assert.Equal(Digest(raw), comparison["base"]!["digest"]!.GetValue<string>());
        Assert.Equal(Digest(raw), comparison["target"]!["digest"]!.GetValue<string>());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NormalizationDoesNotTrustAnIncorrectSha512DiffId(bool gzip)
    {
        byte[] raw = Layer();
        Sources sources = new(new(gzip ? Compress(raw) : raw), new(raw), Digest(raw))
        {
            RegistryDiffId = "sha512:" + new string('a', 128)
        };
        using StringWriter output = new();
        TestCommand command = Command(sources, output);

        Assert.Equal(1, await InvokeAsync(command, registryFirst: true));

        Assert.Empty(output.ToString());
        Assert.Contains("does not match the configuration rootfs diff ID", command.Diagnostics.ToString());
    }

    [Fact]
    public async Task RegistryOnly_DoesNotReadLayerContent()
    {
        byte[] raw = Layer();
        Content registry = new(raw);
        Sources sources = new(registry, new(raw), "sha256:" + new string('a', 64));
        using StringWriter output = new();
        CompareLayersCommand command = Command(sources, output);

        Assert.Equal(0, await command.Parse(["registry.test/image", "registry.test/image", "--output", "json"])
            .InvokeAsync(new InvocationConfiguration(), TestContext.Current.CancellationToken));

        Assert.True(JsonNode.Parse(output.ToString())!["summary"]!["areEqual"]!.GetValue<bool>());
        Assert.Equal(0, registry.Reads);
        Assert.Equal(2, sources.Disposals);
        Assert.False(Directory.Exists(root));
    }

    [Fact]
    public async Task CanceledVerification_DisposesBothSourcesAndDoesNotProduceResult()
    {
        byte[] raw = Layer();
        Content registry = new(raw) { CancelRead = true };
        Sources sources = new(registry, new(raw), Digest(raw));
        using StringWriter output = new();
        CompareLayersCommand command = Command(sources, output);

        Assert.Equal(1, await InvokeAsync(command, registryFirst: true));

        Assert.Equal(string.Empty, output.ToString());
        Assert.Equal(2, sources.Disposals);
        Assert.Empty(Directory.GetFiles(Path.Combine(root, "cache", "layer-store", "data"), "*.scratch"));
    }

    private TestCommand Command(Sources sources, TextWriter output)
    {
        TestCommand command = new(
            Mock.Of<IDockerRegistryClientFactory>(),
            AnsiConsole.Create(new AnsiConsoleSettings { Out = new AnsiConsoleOutput(output) }),
            new TestDredgePathProvider(root))
        {
            ImageSourceFactory = sources
        };
        ((IProcessTerminationAware)command).ProcessTerminator = new TestProcessTerminator();
        return command;
    }

    private static Task<int> InvokeAsync(CompareLayersCommand command, bool registryFirst)
    {
        string[] operands = registryFirst
            ? ["registry.test/image", "docker://image:dev"]
            : ["docker://image:dev", "registry.test/image"];
        return command.Parse([.. operands, "--output", "json"])
            .InvokeAsync(new InvocationConfiguration(), TestContext.Current.CancellationToken);
    }

    private static byte[] Layer()
    {
        using MemoryStream result = new();
        using (TarWriter writer = new(result, leaveOpen: true))
        using (MemoryStream data = new(Encoding.UTF8.GetBytes("verified content")))
        {
            writer.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, "hello") { DataStream = data });
        }
        return result.ToArray();
    }

    private static byte[] Compress(byte[] raw)
    {
        using MemoryStream result = new();
        using (GZipStream gzip = new(result, CompressionMode.Compress, leaveOpen: true))
        {
            gzip.Write(raw);
        }
        return result.ToArray();
    }

    public void Dispose()
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private sealed class Sources(Content registry, Content daemon, string diffId) : IImageSourceFactory
    {
        public int Disposals { get; private set; }
        public string? RegistryDiffId { get; init; }

        public Task<ResolvedImage> OpenAsync(
            string reference, PlatformOptionsBase options, CancellationToken cancellationToken,
            ImageSourceRequirements requirements = ImageSourceRequirements.LayerContent)
        {
            ImageSourceReference parsed = ImageSourceReference.Parse(reference);
            Content content = parsed.IsDaemon ? daemon : registry;
            DockerManifest manifest = new()
            {
                Layers = [new ManifestLayer { Digest = Digest(content.Bytes), Size = content.Bytes.Length }]
            };
            ManifestInfo info = new("application/test", "sha256:manifest", manifest);
            Image configuration = new()
            {
                Os = "linux",
                Architecture = "amd64",
                RootFilesystem = new RootFilesystem
                {
                    Type = "layers", DiffIds = [parsed.IsDaemon ? diffId : RegistryDiffId ?? diffId]
                },
                History = [new LayerHistory { CreatedBy = "COPY hello /hello" }]
            };
            return Task.FromResult(new ResolvedImage(
                parsed, parsed.GetImageName(), new ResolvedManifest(info, manifest), info,
                configuration, [], content, "sha256:image", "linux/amd64",
                new Owner(() => Disposals++)));
        }
    }

    private sealed class Owner(Action dispose) : IAsyncDisposable
    {
        public ValueTask DisposeAsync()
        {
            dispose();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class Content(byte[] bytes) : IImageContentSource
    {
        public byte[] Bytes => bytes;
        public int Reads { get; private set; }
        public bool StreamDisposed { get; private set; }
        public bool CancelRead { get; init; }

        public Task<BlobDownloadResult> GetRangeAsync(
            string digest, long start, long? length, CancellationToken cancellationToken)
        {
            Reads++;
            cancellationToken.ThrowIfCancellationRequested();
            if (CancelRead)
            {
                return Task.FromCanceled<BlobDownloadResult>(new CancellationToken(canceled: true));
            }
            return Task.FromResult(new BlobDownloadResult(
                new TrackedStream(bytes, () => StreamDisposed = true), false, null, null, bytes.Length));
        }
    }

    private sealed class TrackedStream(byte[] bytes, Action onDispose) : MemoryStream(bytes)
    {
        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                onDispose();
            }
            base.Dispose(disposing);
        }
    }

    private sealed class TestCommand(
        IDockerRegistryClientFactory registry, IAnsiConsole console, IDredgePathProvider paths)
        : CompareLayersCommand(registry, console, paths)
    {
        public StringWriter Diagnostics { get; } = new();
        protected override TextWriter Error => Diagnostics;
    }
}
