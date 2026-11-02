using System.Formats.Tar;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using static Valleysoft.Dredge.Tests.WindowsImageTestData;

namespace Valleysoft.Dredge.Tests;

public sealed class WindowsScannerPerformanceTests
{
    private CancellationToken Token => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("")]
    [InlineData("plain")]
    [InlineData("unicode")]
    [InlineData("surrogates")]
    public void MetadataFingerprintPreservesPersistedEncoding(string content)
    {
        string value = content switch
        {
            "plain" => new('A', 4096),
            "unicode" => new string('A', 1023) + "\uD83D\uDE00\u20AC" + new string('Z', 5000),
            "surrogates" => new string('A', 1023) + "\uD83D" + new string('Z', 5000) + "\uDE00",
            _ => ""
        };
        Dictionary<string, string> global = new()
        {
            ["comment"] = new('X', 4096),
            ["MSWINDOWS.fileattr"] = "33",
            ["MSWINDOWS.rawsd"] = "replaced",
            ["MSWINDOWS.xattr.removed"] = "remove"
        };
        Dictionary<string, string> local = new()
        {
            ["MSWINDOWS.rawsd"] = value,
            ["MSWINDOWS.fileattr"] = "32",
            ["MSWINDOWS.xattr.removed"] = "",
            ["comment"] = "ignored"
        };
        PaxTarEntry entry = new(TarEntryType.RegularFile, "Files/value", local);
        Dictionary<string, string> effective = new(global);
        foreach (var pair in local)
        {
            if (pair.Value.Length == 0) effective.Remove(pair.Key);
            else effective[pair.Key] = pair.Value;
        }
        string expected = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n',
            effective.Where(static pair => pair.Key.StartsWith("MSWINDOWS.", StringComparison.Ordinal))
                .OrderBy(static pair => pair.Key, StringComparer.Ordinal)
                .Select(static pair => $"{pair.Key}={pair.Value}")))));
        WindowsFileMetadata metadata = ReadMetadata(entry, "value", global);
        Assert.Equal(expected, metadata.Fingerprint);
        Assert.Equal(32u, metadata.Attributes);
        Assert.Equal(value.Length > 0, metadata.HasSecurityDescriptor);
        Assert.Null(metadata.UnsupportedReason);
    }

    [Fact]
    public void FingerprintingLargeDescriptorsUsesBoundedAllocation()
    {
        PaxTarEntry entry = new(TarEntryType.RegularFile, "Files/value", new Dictionary<string, string>
        {
            ["MSWINDOWS.fileattr"] = "32",
            ["MSWINDOWS.rawsd"] = new('A', 64 * 1024)
        });
        Dictionary<string, string> global = [];
        ReadMetadata(entry, "value", global);
        long start = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 10; i++) ReadMetadata(entry, "value", global);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - start;
        Assert.True(allocated < 50_000, $"Ten metadata reads allocated {allocated:N0} bytes.");
    }

    private static readonly Func<TarEntry, string, IReadOnlyDictionary<string, string>, WindowsFileMetadata> ReadMetadata =
        typeof(ImageLayerScanner).GetMethod("ReadWindowsMetadata", BindingFlags.Static | BindingFlags.NonPublic)!
            .CreateDelegate<Func<TarEntry, string, IReadOnlyDictionary<string, string>, WindowsFileMetadata>>();

    [Theory]
    [InlineData("Files/App/name.")]
    [InlineData("Files/App /value")]
    [InlineData("Files/App/.wh.value.")]
    public void ArchiveMappingValidatesWindowsSegmentsWithoutRenormalizing(string path)
    {
        Assert.Throws<NotSupportedException>(() => ImageFileSystemSemantics.Windows.MapArchivePath(path));
        Assert.Equal(path, ImageFileSystemSemantics.Linux.MapArchivePath(path));
    }

    [Theory]
    [InlineData("Files/App/name.")]
    [InlineData("Files/App /value")]
    [InlineData("Files/App/.wh.value.")]
    public async Task InvalidPathsAreRejectedBeforePayloadReads(string path)
    {
        byte[] content = new byte[128 * 1024];
        new Random(17).NextBytes(content);
        byte[] layer = Layer(new PaxTarEntry(TarEntryType.RegularFile, path)
        {
            DataStream = new MemoryStream(content)
        });
        using PayloadProbeStream blob = new(layer);
        await Assert.ThrowsAsync<NotSupportedException>(() => ImageLayerScanner.ScanAsync(
            blob, new(0, "layer"), Token, ImageFileSystemSemantics.Windows));
        Assert.True(blob.Position < 64 * 1024);
    }

    private sealed class PayloadProbeStream(byte[] content) : MemoryStream(content, false)
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (Position >= 64 * 1024) throw new IOException("Payload was read before header validation.");
            return base.ReadAsync(buffer, cancellationToken);
        }
        public override int Read(byte[] buffer, int offset, int count)
        {
            if (Position >= 64 * 1024) throw new IOException("Payload was read before header validation.");
            return base.Read(buffer, offset, count);
        }
    }
}
