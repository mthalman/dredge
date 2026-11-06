using System.CommandLine;
using System.IO.Compression;
using System.Text;
using System.Text.Json.Nodes;
using Docker.DotNet.Models;
using Valleysoft.Dredge.Commands;
using Valleysoft.Dredge.Commands.Image;
using static Valleysoft.Dredge.Tests.DockerArchiveFixture;

namespace Valleysoft.Dredge.Tests;

public class DaemonLinuxOsIntegrityTests
{
    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, true, false)]
    [InlineData(false, false, true)]
    [InlineData(true, false, true)]
    [InlineData(false, true, true)]
    [InlineData(true, true, true)]
    [InlineData(false, true, false, true)]
    [InlineData(true, true, true, true)]
    public async Task LinuxOs_VerifiesEveryPinnedDiffIdBeforeReportingWithoutCaching(
        bool gzip, bool tamper, bool sha512, bool tamperUpper = false)
    {
        await using LayerCacheTestContext cache = new();
        byte[] expectedLayer = BuildTar(("etc/os-release", Encoding.UTF8.GetBytes("ID=trusted\nNAME=\"Trusted Linux\"\n")));
        byte[] actualLayer = tamper && !tamperUpper
            ? BuildTar(("etc/os-release", Encoding.UTF8.GetBytes("ID=forged\nNAME=\"Forged Linux\"\n")))
            : expectedLayer;
        if (gzip)
        {
            using MemoryStream compressed = new();
            using (GZipStream encoder = new(compressed, CompressionLevel.Fastest, leaveOpen: true))
            {
                encoder.Write(actualLayer);
            }
            actualLayer = compressed.ToArray();
        }
        string expectedDiffId = sha512
            ? $"sha512:{Convert.ToHexStringLower(System.Security.Cryptography.SHA512.HashData(expectedLayer))}"
            : LayerCacheTestContext.Digest(expectedLayer);
        byte[] later = BuildTar(("etc/os-release", Encoding.UTF8.GetBytes("ID=updated\nNAME=\"Updated Linux\"\n")));
        string laterDiffId = LayerCacheTestContext.Digest(later);
        byte[] actualLater = tamperUpper
            ? BuildTar(("etc/os-release", Encoding.UTF8.GetBytes("ID=forged\nNAME=\"Forged Linux\"\n")))
            : later;
        byte[] config = Encoding.UTF8.GetBytes(
            $$$"""{"os":"linux","architecture":"amd64","rootfs":{"type":"layers","diff_ids":["{{{expectedDiffId}}}","{{{laterDiffId}}}"]}}""");
        string id = LayerCacheTestContext.Digest(config);
        byte[] export = BuildTar(
            ("manifest.json", Encoding.UTF8.GetBytes(
                """[{"Config":"config.json","RepoTags":["local:dev"],"Layers":["layer.tar","later.tar"]}]""")),
            ("config.json", config),
            ("layer.tar", actualLayer),
            ("later.tar", actualLater));
        MetadataEngine engine = new(id, export);
        ImageSourceFactory factory = new(Mock.Of<IDockerRegistryClientFactory>(),
            new AppSettingsStore(Path.Combine(cache.Root, "settings.json")), _ => engine,
            Path.Combine(cache.Root, "snapshots"));
        using StringWriter output = new();
        using StringWriter errors = new();
        CapturedOsCommand command = new(output, errors, cache.Paths) { ImageSourceFactory = factory };
        ((IProcessTerminationAware)command).ProcessTerminator = new TestProcessTerminator();

        int exitCode = await command.Parse(
            ["docker://local:dev", "--docker-host", "http://localhost:2375", "--os", "linux", "--arch", "amd64"])
            .InvokeAsync(new InvocationConfiguration(), TestContext.Current.CancellationToken);

        Assert.True(engine.Disposed);
        Assert.Empty(Directory.GetFiles(Path.Combine(cache.Paths.CachePath, "layer-store", "data"), "*.blob"));
        if (tamper)
        {
            Assert.Equal(1, exitCode);
            Assert.Equal(string.Empty, output.ToString());
            Assert.Contains("diff ID", errors.ToString(), StringComparison.OrdinalIgnoreCase);
        }
        else
        {
            Assert.Equal(0, exitCode);
            Assert.Equal("updated", JsonNode.Parse(output.ToString())!["ID"]!.GetValue<string>());
        }
    }

    private sealed class CapturedOsCommand : OsCommand
    {
        private readonly TextWriter errors;

        public CapturedOsCommand(TextWriter output, TextWriter errors, IDredgePathProvider paths)
            : base(Mock.Of<IDockerRegistryClientFactory>(), output, paths)
        {
            this.errors = errors;
        }

        protected override TextWriter Error => errors;
    }

    private sealed class MetadataEngine(string id, byte[] export) : IDockerImageEngine
    {
        public bool Disposed { get; private set; }

        public Task<string> GetDaemonIdentityAsync(CancellationToken cancellationToken) =>
            Task.FromResult("os-integrity-test");

        public Task<ImageInspectResponse> InspectAsync(string reference, CancellationToken cancellationToken) =>
            Task.FromResult(new ImageInspectResponse { ID = id, Os = "linux", Architecture = "amd64" });

        public Task<Stream> SaveAsync(string imageId, CancellationToken cancellationToken) =>
            Task.FromResult<Stream>(new MemoryStream(export));

        public void Dispose() => Disposed = true;
    }
}
