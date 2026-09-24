using System.Collections.Concurrent;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Valleysoft.DockerRegistryClient;

namespace Valleysoft.Dredge;

internal sealed class LayerStore : IAsyncDisposable
{
    private const int FormatVersion = 1;
    private readonly string dataPath;
    private readonly string locksPath;
    private readonly long maxBytes;
    private readonly TextWriter diagnostics;
    private readonly ConcurrentDictionary<string, FileStream> pins = new(StringComparer.Ordinal);

    public LayerStore(string root, long maxBytes = CacheSettings.DefaultMaxBytes, TextWriter? diagnostics = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(maxBytes);
        this.maxBytes = maxBytes;
        this.diagnostics = diagnostics ?? Console.Error;
        root = Path.GetFullPath(root);
        CacheFileSystem.CreateDirectory(root);
        string ownedPath = Path.Combine(root, "layer-store");
        CacheFileSystem.CreateDirectory(ownedPath);
        dataPath = Path.Combine(ownedPath, "data");
        locksPath = Path.Combine(ownedPath, "locks");
        CacheFileSystem.CreateDirectory(dataPath);
        CacheFileSystem.CreateDirectory(locksPath);
    }

    public static LayerStore Create(IDredgePathProvider? paths = null) =>
        new((paths ?? new DredgePathProvider()).CachePath, AppSettings.Load().Cache.GetMaxBytes());

    internal string GetBlobPath(string digest) => Path.Combine(dataPath, $"{GetKey(digest)}.blob");

    public async Task<Stream> OpenBlobAsync(
        IDockerRegistryClient client, ImageName image, string digest, long? expectedSize,
        CancellationToken cancellationToken)
    {
        string key = GetKey(digest);
        using FileStream layerLock = await LockAsync(key, cancellationToken);
        Stream? cached = await OpenCachedBlobAsync(digest, expectedSize, cancellationToken);
        if (cached is not null)
        {
            return cached;
        }

        using Stream source = await client.Blobs.GetAsync(image.Repo, digest, cancellationToken);
        return await PublishBlobAsync(source, digest, expectedSize, cancellationToken);
    }

    public async Task<StoredLayerIndex> GetIndexAsync(
        IDockerRegistryClient client, ImageName image, ImageLayerReference layer, long? expectedSize,
        CancellationToken cancellationToken)
    {
        StoredLayerIndex? index = await ReadMetadataAsync<StoredLayerIndex>(
            layer.Digest, "index", cancellationToken);
        if (IsValid(index, layer.Digest, expectedSize))
        {
            return index!;
        }
        if (index is not null)
        {
            diagnostics.WriteLine($"Rebuilding invalid layer index for '{layer.Digest}'.");
        }

        using Stream blob = await OpenBlobAsync(
            client, image, layer.Digest, expectedSize, cancellationToken);
        using FileStream indexLock = await LockAsync($"index:{layer.Digest}", cancellationToken);
        index = await ReadMetadataAsync<StoredLayerIndex>(layer.Digest, "index", cancellationToken);
        if (IsValid(index, layer.Digest, expectedSize))
        {
            return index!;
        }

        LayerChanges changes = await ImageLayerScanner.ScanAsync(blob, layer, cancellationToken);
        index = new(layer.Digest, blob.Length, changes);
        await WriteMetadataAsync(layer.Digest, "index", index, cancellationToken);
        return index;
    }

    public async Task<Stream> OpenIndexedBlobAsync(
        IDockerRegistryClient client, ImageName image, StoredLayerIndex index,
        IEnumerable<int> entryOrdinals, CancellationToken cancellationToken)
    {
        using FileStream layerLock = await LockAsync(GetKey(index.Digest), cancellationToken);
        Stream? cached = await OpenCachedBlobAsync(index.Digest, index.BlobLength, cancellationToken);
        if (cached is not null)
        {
            return cached;
        }

        HashSet<int> ordinals = entryOrdinals.ToHashSet();
        ScannedEntry[] entries = index.Changes.Entries
            .Where(entry => ordinals.Contains(entry.EntryIndex)).ToArray();
        if (entries.Length != ordinals.Count || entries.Any(entry => entry.Type != ImageFileType.File))
        {
            throw new InvalidDataException("The layer index does not contain the requested content.");
        }
        long prefixLength = entries.Max(entry => entry.CompressedHighWaterMark);
        BlobDownloadResult? download = null;
        try
        {
            download = await client.Blobs.GetRangeAsync(
                image.Repo, index.Digest, 0, prefixLength, cancellationToken);
        }
        catch (RegistryException exception) when (exception.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
        {
            diagnostics.WriteLine($"Range unavailable for '{index.Digest}'; downloading the complete layer.");
        }

        if (download is not null)
        {
            using Stream source = download.Content;
            if (!download.IsRangeHonored)
            {
                return await PublishBlobAsync(source, index.Digest, index.BlobLength, cancellationToken);
            }

            if (download.RangeStart == 0 &&
                download.RangeEnd == prefixLength - 1 &&
                (download.TotalLength is null || download.TotalLength == index.BlobLength))
            {
                FileStream prefix = CreateScratchFile();
                try
                {
                    await CopyBoundedAsync(source, prefix, prefixLength, cancellationToken);
                    prefix.Position = 0;
                    await ValidatePrefixAsync(prefix, entries, cancellationToken);
                    prefix.Position = 0;
                    return prefix;
                }
                catch (InvalidDataException exception)
                {
                    diagnostics.WriteLine($"Invalid cached-index range for '{index.Digest}': {exception.Message} Downloading the complete layer.");
                    await prefix.DisposeAsync();
                }
                catch
                {
                    await prefix.DisposeAsync();
                    throw;
                }
            }
            else
            {
                diagnostics.WriteLine($"Unexpected range for '{index.Digest}'; downloading the complete layer.");
            }
        }

        using Stream full = await client.Blobs.GetAsync(image.Repo, index.Digest, cancellationToken);
        return await PublishBlobAsync(full, index.Digest, index.BlobLength, cancellationToken);
    }

    private static async Task ValidatePrefixAsync(
        Stream prefix, IReadOnlyList<ScannedEntry> entries, CancellationToken cancellationToken)
    {
        using GZipStream gzip = new(
            prefix, CompressionMode.Decompress, leaveOpen: true);
        long position = 0;
        byte[] buffer = new byte[81920];
        foreach (ScannedEntry entry in entries.OrderBy(entry => entry.UncompressedOffset))
        {
            await CopyBytesAsync(gzip, Stream.Null, entry.UncompressedOffset - position, buffer, cancellationToken);
            using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            long remaining = entry.Size;
            while (remaining > 0)
            {
                int read = await gzip.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)), cancellationToken);
                if (read == 0)
                {
                    throw new InvalidDataException("The compressed prefix ended before the selected file.");
                }
                hash.AppendData(buffer, 0, read);
                remaining -= read;
            }
            if (Convert.ToHexStringLower(hash.GetHashAndReset()) != entry.ContentHash)
            {
                throw new InvalidDataException("The compressed prefix did not match the indexed content hash.");
            }
            position = entry.UncompressedOffset + entry.Size;
        }
    }

    internal static async Task CopyBytesAsync(
        Stream source, Stream destination, long count, byte[] buffer, CancellationToken cancellationToken)
    {
        if (count < 0)
        {
            throw new InvalidDataException("Invalid indexed content offset.");
        }
        while (count > 0)
        {
            int read = await source.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, count)), cancellationToken);
            if (read == 0)
            {
                throw new InvalidDataException("Layer content ended prematurely.");
            }
            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            count -= read;
        }
    }

    private static async Task CopyBoundedAsync(
        Stream source, Stream destination, long length, CancellationToken cancellationToken)
    {
        byte[] buffer = new byte[81920];
        await CopyBytesAsync(source, destination, length, buffer, cancellationToken);
        if (await source.ReadAsync(buffer.AsMemory(0, 1), cancellationToken) != 0)
        {
            throw new InvalidDataException("The range response exceeded its advertised length.");
        }
    }

    public async Task<T?> ReadMetadataAsync<T>(
        string digest, string kind, CancellationToken cancellationToken) where T : class
    {
        string path = MetadataPath(digest, kind);
        using FileStream gate = await LockAsync("maintenance", cancellationToken);
        if (!File.Exists(path))
        {
            return null;
        }
        CacheFileSystem.ValidateNotLink(path);
        try
        {
            byte[] bytes = await File.ReadAllBytesAsync(path, cancellationToken);
            LayerCacheEnvelope? envelope = JsonHelper.Deserialize<LayerCacheEnvelope>(
                Encoding.UTF8.GetString(bytes),
                new JsonSerializerOptions(JsonHelper.Settings)
                {
                    PropertyNameCaseInsensitive = true
                });
            if (envelope is null || envelope.Version != FormatVersion || envelope.Digest != digest ||
                envelope.Payload is null || envelope.Checksum != Hash(Encoding.UTF8.GetBytes(envelope.Payload)))
            {
                throw new InvalidDataException("Incompatible or damaged cache envelope.");
            }
            T value = JsonHelper.Deserialize<T>(envelope.Payload, JsonHelper.CompactSettings) ??
                throw new InvalidDataException("The cached payload is empty.");
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow);
            return value;
        }
        catch (Exception exception) when (exception is JsonException or InvalidDataException)
        {
            diagnostics.WriteLine($"Rebuilding cached {kind} '{digest}': {exception.Message}");
            File.Delete(path);
            return null;
        }
    }

    public async Task WriteMetadataAsync<T>(
        string digest, string kind, T value, CancellationToken cancellationToken)
    {
        string payload = JsonHelper.Serialize(value, JsonHelper.CompactSettings);
        LayerCacheEnvelope envelope = new(
            FormatVersion, digest, Hash(Encoding.UTF8.GetBytes(payload)), payload);
        string staging = Path.Combine(dataPath, $"{Guid.NewGuid():N}.tmp");
        try
        {
            await using (FileStream output = CacheFileSystem.CreateFile(staging))
            {
                await JsonSerializer.SerializeAsync(
                    output, envelope, DredgeJsonContext.Default.LayerCacheEnvelope, cancellationToken);
                await output.FlushAsync(cancellationToken);
                output.Flush(flushToDisk: true);
                using FileStream gate = await LockAsync("maintenance", cancellationToken);
                // The gate bridges closing the staging handle and publishing its final name.
                output.Close();
                File.Move(staging, MetadataPath(digest, kind), overwrite: true);
            }
            await TrimAsync(cancellationToken);
        }
        finally
        {
            File.Delete(staging);
        }
    }

    private async Task<Stream?> OpenCachedBlobAsync(
        string digest, long? expectedSize, CancellationToken cancellationToken)
    {
        string path = GetBlobPath(digest);
        if (pins.ContainsKey(path))
        {
            return OpenRead(path);
        }
        FileStream blob;
        using (FileStream gate = await LockAsync("maintenance", cancellationToken))
        {
            if (!File.Exists(path))
            {
                return null;
            }
            CacheFileSystem.ValidateNotLink(path);
            blob = OpenRead(path);
        }
        bool valid;
        try
        {
            valid = (!expectedSize.HasValue || blob.Length == expectedSize) &&
                await VerifyDigestAsync(blob, digest, cancellationToken);
        }
        catch
        {
            await blob.DisposeAsync();
            throw;
        }
        if (!valid)
        {
            await blob.DisposeAsync();
            using FileStream gate = await LockAsync("maintenance", cancellationToken);
            File.Delete(path);
            diagnostics.WriteLine($"Rebuilding corrupt layer blob '{digest}'.");
            return null;
        }
        if (!pins.TryAdd(path, blob))
        {
            await blob.DisposeAsync();
        }
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow);
        return OpenRead(path);
    }

    private async Task<Stream> PublishBlobAsync(
        Stream source, string digest, long? expectedSize, CancellationToken cancellationToken)
    {
        string staging = Path.Combine(dataPath, $"{Guid.NewGuid():N}.tmp");
        try
        {
            await using FileStream output = CacheFileSystem.CreateFile(staging);
            await source.CopyToAsync(output, cancellationToken);
            output.Position = 0;
            if ((expectedSize.HasValue && output.Length != expectedSize) ||
                !await VerifyDigestAsync(output, digest, cancellationToken))
            {
                throw new InvalidDataException($"Downloaded layer '{digest}' failed digest or length verification.");
            }
            await output.FlushAsync(cancellationToken);
            output.Flush(flushToDisk: true);
            cancellationToken.ThrowIfCancellationRequested();
            string path = GetBlobPath(digest);
            using (FileStream gate = await LockAsync("maintenance", cancellationToken))
            {
                output.Close();
                File.Move(staging, path, overwrite: true);
                FileStream pin = OpenRead(path);
                if (!pins.TryAdd(path, pin))
                {
                    pin.Dispose();
                }
            }
            await TrimAsync(cancellationToken);
            return OpenRead(path);
        }
        finally
        {
            File.Delete(staging);
        }
    }

    internal FileStream CreateScratchFile()
    {
        string path = Path.Combine(dataPath, $"{Guid.NewGuid():N}.tmp");
        FileStreamOptions options = new()
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.ReadWrite,
            Share = FileShare.None,
            Options = FileOptions.Asynchronous | FileOptions.DeleteOnClose
        };
        if (!OperatingSystem.IsWindows())
        {
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        }
        return new FileStream(path, options);
    }

    internal async Task<string> CreateScratchDirectoryAsync(CancellationToken cancellationToken)
    {
        using FileStream gate = await LockAsync("maintenance", cancellationToken);
        string markerPath = Path.Combine(dataPath, $"{Guid.NewGuid():N}.scratch");
        string directoryPath = $"{markerPath}.dir";
        FileStream pin = CacheFileSystem.CreateFile(markerPath);
        try
        {
            CacheFileSystem.CreateDirectory(directoryPath);
            // Keep the marker leased until operation disposal so trimming cannot remove active scratch.
            pins[markerPath] = pin;
            return directoryPath;
        }
        catch
        {
            pin.Dispose();
            File.Delete(markerPath);
            throw;
        }
    }

    public async Task<long> ClearAsync(CancellationToken cancellationToken)
    {
        return await TrimAsync(cancellationToken, clear: true);
    }

    private async Task<long> TrimAsync(CancellationToken cancellationToken, bool clear = false)
    {
        using FileStream gate = await LockAsync("maintenance", cancellationToken);
        FileInfo[] files = new DirectoryInfo(dataPath).GetFiles();
        long total = 0;
        foreach (FileInfo file in files)
        {
            CacheFileSystem.ValidateNotLink(file.FullName);
            total = checked(total + file.Length);
        }
        long removed = 0;
        foreach (FileInfo file in files
            .OrderBy(file => file.Extension is ".tmp" or ".scratch" ? 0 : file.Extension == ".blob" ? 1 : 2)
            .ThenBy(file => file.LastWriteTimeUtc))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!clear && total <= maxBytes && file.Extension is not (".tmp" or ".scratch"))
            {
                continue;
            }
            if (file.Extension is not (".blob" or ".index" or ".view" or ".tmp" or ".scratch"))
            {
                continue;
            }
            try
            {
                using (new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.None))
                {
                    if (file.Extension == ".scratch")
                    {
                        string directoryPath = $"{file.FullName}.dir";
                        if (Directory.Exists(directoryPath))
                        {
                            CacheFileSystem.ValidateNotLink(directoryPath);
                            long scratchSize = 0;
                            foreach (FileInfo entry in new DirectoryInfo(directoryPath).EnumerateFiles("*",
                                new EnumerationOptions
                                {
                                    RecurseSubdirectories = true,
                                    AttributesToSkip = FileAttributes.ReparsePoint,
                                    IgnoreInaccessible = false
                                }))
                            {
                                cancellationToken.ThrowIfCancellationRequested();
                                scratchSize = checked(scratchSize + entry.Length);
                            }
                            cancellationToken.ThrowIfCancellationRequested();
                            Directory.Delete(directoryPath, recursive: true);
                            removed = checked(removed + scratchSize);
                        }
                    }
                }
                File.Delete(file.FullName);
                total -= file.Length;
                removed += file.Length;
            }
            catch (IOException exception) when (IsSharingViolation(exception))
            {
                // Leased bytes are temporary overflow, not a reason to discard reusable indexes.
                total -= file.Length;
                if (clear)
                {
                    diagnostics.WriteLine($"Cache entry is in use and was not removed: '{file.FullName}'.");
                }
            }
        }
        return removed;
    }

    private async Task<FileStream> LockAsync(string key, CancellationToken cancellationToken)
    {
        // A fixed stripe table bounds coordination files without unlinking live locks.
        string name = key == "maintenance" ? "maintenance" : Hash(Encoding.UTF8.GetBytes(key))[..2];
        string path = Path.Combine(locksPath, $"{name}.lock");
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (File.Exists(path))
            {
                CacheFileSystem.ValidateNotLink(path);
            }
            try
            {
                FileStreamOptions options = new()
                {
                    Mode = FileMode.OpenOrCreate,
                    Access = FileAccess.ReadWrite,
                    Share = FileShare.None
                };
                if (!OperatingSystem.IsWindows())
                {
                    options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
                }
                return new FileStream(path, options);
            }
            catch (IOException exception) when (IsSharingViolation(exception))
            {
                await Task.Delay(25, cancellationToken);
            }
        }
    }

    private string MetadataPath(string digest, string kind)
    {
        if (kind is not ("index" or "view"))
        {
            throw new ArgumentException("Unknown cache metadata kind.", nameof(kind));
        }
        return Path.Combine(dataPath, $"{GetKey(digest)}.{kind}");
    }

    private static bool IsSharingViolation(IOException exception)
    {
        int error = exception.HResult & 0xffff;
        if (OperatingSystem.IsWindows())
        {
            return error is 32 or 33;
        }
        // Unix FileStream exposes native EWOULDBLOCK: 35 on macOS, 11 on Linux.
        return error == (OperatingSystem.IsMacOS() ? 35 : 11);
    }

    private static FileStream OpenRead(string path) =>
        new(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous);

    private static string Hash(byte[] data) => Convert.ToHexStringLower(SHA256.HashData(data));

    private static string GetKey(string digest)
    {
        string[] parts = digest.Split(':');
        if (parts.Length != 2 ||
            parts[0] is not ("sha256" or "sha512") ||
            parts[1].Length != (parts[0] == "sha256" ? 64 : 128) ||
            parts[1].Any(character => !char.IsAsciiHexDigitLower(character)))
        {
            throw new InvalidDataException($"Unsupported or invalid layer/manifest digest '{digest}'.");
        }
        return $"{parts[0]}-{parts[1]}";
    }

    private static async Task<bool> VerifyDigestAsync(Stream stream, string digest, CancellationToken cancellationToken)
    {
        _ = GetKey(digest);
        byte[] hash = digest.StartsWith("sha256:", StringComparison.Ordinal)
            ? await SHA256.HashDataAsync(stream, cancellationToken)
            : await SHA512.HashDataAsync(stream, cancellationToken);
        return digest[(digest.IndexOf(':') + 1)..] == Convert.ToHexStringLower(hash);
    }

    private static bool IsValid(StoredLayerIndex? index, string digest, long? expectedSize)
    {
        if (index is null || index.Digest != digest || index.BlobLength <= 0 ||
            (expectedSize.HasValue && index.BlobLength != expectedSize) ||
            index.Changes?.Entries is null || index.Changes.Whiteouts is null ||
            index.Changes.OpaqueDirectories is null)
        {
            return false;
        }
        int previousOrdinal = -1;
        foreach (ScannedEntry entry in index.Changes.Entries)
        {
            if (entry is null || entry.EntryIndex <= previousOrdinal || entry.Size < 0 ||
                entry.UncompressedOffset < 0 || entry.CompressedHighWaterMark <= 0 ||
                entry.CompressedHighWaterMark > index.BlobLength ||
                (entry.Type == ImageFileType.File &&
                    (entry.ContentHash?.Length != 64 || entry.ContentHash.Any(c => !char.IsAsciiHexDigitLower(c)))))
            {
                return false;
            }
            previousOrdinal = entry.EntryIndex;
        }
        try
        {
            foreach (string path in index.Changes.Entries.Select(entry => entry.Path)
                .Concat(index.Changes.Whiteouts).Concat(index.Changes.OpaqueDirectories))
            {
                if (string.IsNullOrEmpty(path) || ImagePath.NormalizeArchive(path) != path)
                {
                    return false;
                }
            }
        }
        catch (InvalidDataException)
        {
            return false;
        }
        return true;
    }

    public async ValueTask DisposeAsync()
    {
        foreach (FileStream pin in pins.Values)
        {
            await pin.DisposeAsync();
        }
        pins.Clear();
        await TrimAsync(CancellationToken.None);
    }

}

internal sealed record StoredLayerIndex(string Digest, long BlobLength, LayerChanges Changes);
internal sealed record LayerCacheEnvelope(
    [property: JsonPropertyName("Version")] int Version,
    [property: JsonPropertyName("Digest")] string Digest,
    [property: JsonPropertyName("Checksum")] string Checksum,
    [property: JsonPropertyName("Payload")] string Payload);
