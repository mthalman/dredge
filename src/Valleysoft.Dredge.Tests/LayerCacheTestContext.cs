using System.Security.Cryptography;

namespace Valleysoft.Dredge.Tests;

internal sealed class LayerCacheTestContext : IAsyncDisposable
{
    public string Root { get; } = Path.Combine(Path.GetTempPath(), $"dredge-cache-test-{Guid.NewGuid():N}");
    public TestDredgePathProvider Paths { get; }
    public LayerStore Store { get; private set; }

    public LayerCacheTestContext(long maxBytes = CacheSettings.DefaultMaxBytes)
    {
        Paths = new(Root);
        Store = new(Paths.CachePath, maxBytes);
    }

    public async Task EvictBlobsAsync()
    {
        await Store.DisposeAsync();
        foreach (string blob in Directory.GetFiles(
            Path.Combine(Paths.CachePath, "layer-store", "data"), "*.blob"))
        {
            File.Delete(blob);
        }
        Store = new(Paths.CachePath);
    }

    public static string Digest(byte[] bytes) =>
        $"sha256:{Convert.ToHexStringLower(SHA256.HashData(bytes))}";

    public static byte[] ReadBytes(Stream stream)
    {
        using (stream)
        using (MemoryStream bytes = new())
        {
            stream.CopyTo(bytes);
            return bytes.ToArray();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await Store.DisposeAsync();
        Directory.Delete(Root, recursive: true);
    }
}
