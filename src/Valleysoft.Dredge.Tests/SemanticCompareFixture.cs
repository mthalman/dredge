using System.Formats.Tar;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Valleysoft.DockerRegistryClient;
using Valleysoft.DockerRegistryClient.Models.Images;
using Valleysoft.DockerRegistryClient.Models.Manifests;
using Valleysoft.DockerRegistryClient.Models.Manifests.Oci;
using Valleysoft.Dredge.Commands;
using Valleysoft.Dredge.Commands.Image;

namespace Valleysoft.Dredge.Tests;

internal sealed class SemanticCompareFixture : IDisposable, IImageSourceFactory
{
    public string Root { get; } = Path.Combine(Directory.GetCurrentDirectory(), $".semantic-compare-{Guid.NewGuid():N}");
    public Dictionary<string, Snapshot> Images { get; } = new(StringComparer.Ordinal);
    public int Disposals { get; private set; }
    public int Reads { get; private set; }
    public int StreamDisposals { get; private set; }
    public List<ImageSourceRequirements> Requirements { get; } = [];
    public bool FailTarget { get; set; }
    public bool CancelContent { get; set; }
    public bool UseDaemonArchive { get; set; }
    public RecordingProcessTerminator? Terminator { get; private set; }
    public static DateTime Timestamp { get; } = new(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    public SemanticCompareCommand Command(TextWriter? output = null)
    {
        var command = new SemanticCompareCommand(Mock.Of<IDockerRegistryClientFactory>(), output ?? TextWriter.Null, new TestDredgePathProvider(Root))
        {
            ImageSourceFactory = this
        };
        ((IProcessTerminationAware)command).ProcessTerminator = Terminator = new RecordingProcessTerminator();
        return command;
    }

    public async Task<CompareResult> CompareAsync(params string[] arguments)
    {
        var command = Command();
        var parsed = command.Parse(arguments);
        Assert.Empty(parsed.Errors);
        command.Options.SetParseResult(parsed);
        return await command.GetResultAsync(TestContext.Current.CancellationToken);
    }

    public async Task<ResolvedImage> OpenAsync(string reference, PlatformOptionsBase options, CancellationToken cancellationToken,
        ImageSourceRequirements requirements = ImageSourceRequirements.LayerContent)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Requirements.Add(requirements);
        if (FailTarget && reference.Contains("target", StringComparison.Ordinal))
        {
            throw new InvalidDataException("fixture acquisition failure");
        }
        Snapshot snapshot = Images[reference];
        var parsed = ImageSourceReference.Parse(reference);
        Image configuration = JsonSerializer.Deserialize(snapshot.Config, DredgeJsonContext.Default.Image)!;
        string configId = Digest(snapshot.Config);
        if (parsed.IsDaemon && UseDaemonArchive)
        {
            DockerImageArchive archive = await DockerImageArchive.OpenAsync(
                new MemoryStream(Export(snapshot)), configId, cancellationToken, Path.Combine(Root, "daemon-export"));
            return new(parsed, parsed.GetImageName(), archive.Resolved, null, archive.Configuration, archive.ConfigurationJson,
                archive, configId, archive.Platform.ToString(), new ArchiveOwner(this, archive));
        }
        OciImageManifest manifest = new()
        {
            SchemaVersion = 2,
            MediaType = "application/vnd.oci.image.manifest.v1+json",
            Config = new OciDescriptor { Digest = configId, Size = snapshot.Config.Length, MediaType = "application/vnd.oci.image.config.v1+json" },
            Layers = snapshot.Layers.Select(static l => new OciDescriptor
            {
                Digest = Digest(l),
                Size = l.Length,
                MediaType = "application/vnd.oci.image.layer.v1.tar+gzip"
            }).ToArray()
        };
        var info = new ManifestInfo(manifest.MediaType, Digest(Encoding.UTF8.GetBytes(JsonHelper.Serialize(manifest))), manifest);
        return new ResolvedImage(parsed, parsed.GetImageName(), new(info, manifest), parsed.IsDaemon ? null : info,
            configuration, snapshot.Config, new Content(this, snapshot.Layers),
            parsed.IsDaemon ? configId : info.DockerContentDigest, configuration.Os + "/" + configuration.Architecture,
            new Owner(this));
    }

    public static Snapshot Image(params byte[][] layers) => new(layers, Configuration(layers));
    public static byte[] Configuration(byte[][] layers, string os = "linux", string architecture = "amd64",
        string extra = "", string execution = "{}")
    {
        string ids = string.Join(",", layers.Select(layer => "\"" + Digest(Uncompress(layer)) + "\""));
        return Encoding.UTF8.GetBytes($"{{\"os\":\"{os}\",\"architecture\":\"{architecture}\",\"config\":{execution},\"rootfs\":{{\"type\":\"layers\",\"diff_ids\":[{ids}]}}{extra}}}");
    }
    public static byte[] Layer(params TarItem[] items)
    {
        using MemoryStream raw = new();
        using (TarWriter writer = new(raw, TarEntryFormat.Ustar, leaveOpen: true))
        {
            foreach (var item in items)
            {
                using MemoryStream data = new(Encoding.UTF8.GetBytes(item.Content));
                UstarTarEntry entry = new(item.Type, item.Path)
                {
                    ModificationTime = item.Time ?? Timestamp,
                    Mode = (UnixFileMode)item.Mode,
                    Uid = item.Owner,
                    Gid = item.Group,
                    UserName = "",
                    GroupName = ""
                };
                if (item.Type == TarEntryType.RegularFile) { entry.DataStream = data; }
                if (item.Type is TarEntryType.SymbolicLink or TarEntryType.HardLink) { entry.LinkName = item.Content; }
                writer.WriteEntry(entry);
            }
        }
        using MemoryStream compressed = new();
        using (GZipStream gzip = new(compressed, CompressionLevel.SmallestSize, leaveOpen: true)) { raw.WriteTo(gzip); }
        return compressed.ToArray();
    }
    public static byte[] Uncompress(byte[] layer)
    {
        if (layer.Length < 2 || layer[0] != 0x1f || layer[1] != 0x8b) { return layer; }
        using GZipStream gzip = new(new MemoryStream(layer), CompressionMode.Decompress);
        using MemoryStream raw = new();
        gzip.CopyTo(raw);
        return raw.ToArray();
    }
    public static string Digest(byte[] bytes) => "sha256:" + Convert.ToHexStringLower(SHA256.HashData(bytes));

    public static byte[] Export(Snapshot snapshot, string tag = "fixture:fixed")
    {
        using MemoryStream output = new();
        using (TarWriter writer = new(output, TarEntryFormat.Ustar, leaveOpen: true))
        {
            string configName = Digest(snapshot.Config)["sha256:".Length..] + ".json";
            string[] names = snapshot.Layers.Select((_, i) => $"layer{i}/layer.tar").ToArray();
            string manifest = $"[{{\"Config\":\"{configName}\",\"RepoTags\":[\"{tag}\"],\"Layers\":[{string.Join(",", names.Select(static n => "\"" + n + "\""))}]}}]";
            Write("manifest.json", Encoding.UTF8.GetBytes(manifest));
            Write(configName, snapshot.Config);
            for (int i = 0; i < names.Length; i++) { Write(names[i], Uncompress(snapshot.Layers[i])); }
            void Write(string name, byte[] bytes)
            {
                using MemoryStream data = new(bytes);
                writer.WriteEntry(new UstarTarEntry(TarEntryType.RegularFile, name)
                {
                    ModificationTime = Timestamp,
                    DataStream = data,
                    UserName = "",
                    GroupName = ""
                });
            }
        }
        return output.ToArray();
    }

    public void Dispose()
    {
        if (Directory.Exists(Root)) { Directory.Delete(Root, recursive: true); }
    }
    public sealed record Snapshot(byte[][] Layers, byte[] Config);
    public sealed record TarItem(string Path, string Content = "", TarEntryType Type = TarEntryType.RegularFile,
        int Mode = 420, int Owner = 0, int Group = 0, DateTime? Time = null);
    private sealed class Owner(SemanticCompareFixture fixture) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() { fixture.Disposals++; return ValueTask.CompletedTask; }
    }
    private sealed class ArchiveOwner(SemanticCompareFixture fixture, DockerImageArchive archive) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync() { await archive.DisposeAsync(); fixture.Disposals++; }
    }
    private sealed class Content(SemanticCompareFixture fixture, byte[][] layers) : IImageContentSource
    {
        public Task<BlobDownloadResult> GetRangeAsync(string digest, long start, long? length, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            fixture.Reads++;
            if (fixture.CancelContent) { return Task.FromCanceled<BlobDownloadResult>(new CancellationToken(true)); }
            byte[] bytes = layers.Single(layer => Digest(layer) == digest);
            return Task.FromResult(new BlobDownloadResult(new TrackedStream(bytes, fixture), false, null, null, bytes.Length));
        }
    }
    private sealed class TrackedStream(byte[] bytes, SemanticCompareFixture fixture) : MemoryStream(bytes)
    {
        protected override void Dispose(bool disposing)
        {
            if (disposing) { fixture.StreamDisposals++; }
            base.Dispose(disposing);
        }
    }
}
