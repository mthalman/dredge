using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Net;
using System.Runtime.ExceptionServices;
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
    private readonly VerificationState verifications = new();
    private object verificationLock => verifications.Sync;
    private Dictionary<(string Digest, string DiffId), TaskCompletionSource<string>> contentVerifications => verifications.Tasks;
    private readonly TaskCompletionSource operationsDrained = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int operations;
    private Task? disposal;
    private bool verificationDisposed;

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

    private LayerStore(LayerStore parent)
    {
        dataPath = parent.dataPath;
        locksPath = parent.locksPath;
        maxBytes = parent.maxBytes;
        diagnostics = parent.diagnostics;
        verifications = parent.verifications;
    }

    internal LayerStore CreateScope()
    {
        lock (verificationLock)
        {
            ObjectDisposedException.ThrowIf(verificationDisposed, this);
            return new(this);
        }
    }

    internal string GetBlobPath(string digest) => Path.Combine(dataPath, $"{GetKey(digest)}.blob");

    internal async Task<string> VerifyContentAsync(
        IImageContentSource source, string digest, long? expectedSize, string expectedDiffId,
        CancellationToken cancellationToken)
    {
        using IDisposable operation = EnterOperation();
        cancellationToken.ThrowIfCancellationRequested();
        _ = GetKey(expectedDiffId);
        string path = GetBlobPath(digest);
        string identity;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            TaskCompletionSource<string> verification;
            bool start;
            lock (verificationLock)
            {
                start = !contentVerifications.TryGetValue((digest, expectedDiffId), out verification!);
                if (start)
                {
                    verification = new(TaskCreationOptions.RunContinuationsAsynchronously);
                    contentVerifications.Add((digest, expectedDiffId), verification);
                }
            }
            if (start)
            {
                await CompleteContentVerificationAsync(
                    source, digest, expectedSize, expectedDiffId, verification, cancellationToken);
            }
            try
            {
                identity = await verification.Task.WaitAsync(cancellationToken);
                break;
            }
            catch (OperationCanceledException) when (
                !start && !cancellationToken.IsCancellationRequested && verification.Task.IsCanceled)
            {
                continue;
            }
        }
        cancellationToken.ThrowIfCancellationRequested();
        if (!pins.ContainsKey(path))
        {
            using (await OpenBlobCoreAsync(source, digest, expectedSize, cancellationToken)) { }
        }
        lock (verificationLock)
        {
            ObjectDisposedException.ThrowIf(verificationDisposed, this);
            if (expectedSize.HasValue && pins[path].Length != expectedSize)
            {
                throw new InvalidDataException($"Layer '{digest}' does not match its advertised length.");
            }
        }
        return identity;
    }

    private async Task CompleteContentVerificationAsync(
        IImageContentSource source, string digest, long? expectedSize, string expectedDiffId,
        TaskCompletionSource<string> verification,
        CancellationToken cancellationToken)
    {
        try
        {
            string? identity = null;
            ExceptionDispatchInfo? contentFailure = null;
            await using (Stream blob = await OpenBlobCoreAsync(source, digest, expectedSize, cancellationToken,
                verifyCached: async (cached, token) =>
                {
                    var result = await VerifyCachedContentAsync(cached, digest, expectedDiffId, token);
                    if (result.Valid)
                    {
                        identity = result.Identity;
                        contentFailure = result.Failure;
                    }
                    return result.Valid;
                }))
            {
                contentFailure?.Throw();
                identity ??= await VerifyPinnedContentAsync(blob, digest, expectedDiffId, cancellationToken);
            }
            verification.SetResult(identity);
        }
        catch (Exception exception)
        {
            lock (verificationLock)
            {
                contentVerifications.Remove((digest, expectedDiffId));
            }
            if (exception is OperationCanceledException)
            {
                verification.SetCanceled(cancellationToken);
            }
            else
            {
                verification.SetException(exception);
            }
        }
    }

    internal static async Task<string> VerifyPinnedContentAsync(
        Stream blob, string digest, string expectedDiffId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _ = GetKey(expectedDiffId);
        string encoding = ImageTarReader.DetectEncoding(blob);
        // A pinned raw blob's verified digest already covers every byte, including tar padding.
        if (encoding == "tar" && DockerImageArchive.IsSha256(digest) &&
            string.Equals(digest, expectedDiffId, StringComparison.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            return digest;
        }
        using Stream content = ImageTarReader.OpenContent(blob, encoding);
        bool sha512 = expectedDiffId.StartsWith("sha512:", StringComparison.Ordinal);
        (string identity, string? sha512DiffId) =
            await DockerImageArchive.HashContentAsync(content, sha512, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if ((sha512 ? sha512DiffId : identity) != expectedDiffId)
        {
            throw new InvalidDataException(
                $"Layer '{digest}' content does not match the configuration rootfs diff ID '{expectedDiffId}'.");
        }
        return identity;
    }

    internal static async Task<(bool Valid, string? Identity, ExceptionDispatchInfo? Failure)> VerifyCachedContentAsync(
        Stream blob, string digest, string expectedDiffId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _ = GetKey(digest);
        _ = GetKey(expectedDiffId);
        string encoding = ImageTarReader.DetectEncoding(blob);
        string? identity = null;
        string? sha512DiffId = null;
        ExceptionDispatchInfo? failure = null;
        bool sha512 = expectedDiffId.StartsWith("sha512:", StringComparison.Ordinal);
        bool sha512Blob = digest.StartsWith("sha512:", StringComparison.Ordinal);
        string? actualDigest;
        if (encoding == "tar")
        {
            (identity, sha512DiffId) = await DockerImageArchive.HashContentAsync(blob, sha512 || sha512Blob, cancellationToken);
            actualDigest = sha512Blob ? sha512DiffId : identity;
        }
        else
        {
            using HashAlgorithm encodedHash = sha512Blob ? SHA512.Create() : SHA256.Create();
            using CryptoStream encoded = new(blob, encodedHash, CryptoStreamMode.Read, leaveOpen: true);
            try
            {
                using Stream content = ImageTarReader.OpenContent(encoded, encoding);
                (identity, sha512DiffId) = await DockerImageArchive.HashContentAsync(content, sha512, cancellationToken);
            }
            catch (InvalidDataException exception)
            {
                failure = ExceptionDispatchInfo.Capture(exception);
            }
            await encoded.CopyToAsync(Stream.Null, cancellationToken);
            actualDigest = $"{digest[..digest.IndexOf(':')]}:{Convert.ToHexStringLower(encodedHash.Hash!)}";
        }
        cancellationToken.ThrowIfCancellationRequested();
        if (actualDigest != digest)
        {
            return (false, null, null);
        }
        if (failure is null && (sha512 ? sha512DiffId : identity) != expectedDiffId)
        {
            failure = ExceptionDispatchInfo.Capture(new InvalidDataException(
                $"Layer '{digest}' content does not match the configuration rootfs diff ID '{expectedDiffId}'."));
        }
        return (true, identity, failure);
    }

    public Task<Stream> OpenBlobAsync(
        IDockerRegistryClient client, ImageName image, string digest, long? expectedSize,
        CancellationToken cancellationToken, IProgress<long>? progress = null) =>
        OpenBlobAsync(new RegistryImageContentSource(client, image), digest, expectedSize, cancellationToken, progress);

    public async Task<Stream> OpenBlobAsync(
        IImageContentSource source, string digest, long? expectedSize,
        CancellationToken cancellationToken, IProgress<long>? progress = null)
    {
        using IDisposable operation = EnterOperation();
        return await OpenBlobCoreAsync(source, digest, expectedSize, cancellationToken, progress);
    }

    private async Task<Stream> OpenBlobCoreAsync(
        IImageContentSource source, string digest, long? expectedSize,
        CancellationToken cancellationToken, IProgress<long>? progress = null,
        Func<Stream, CancellationToken, Task<bool>>? verifyCached = null)
    {
        string key = GetKey(digest);
        using FileStream layerLock = await LockAsync(key, cancellationToken);
        Stream? cached = await OpenCachedBlobAsync(digest, expectedSize, cancellationToken, verifyCached);
        if (cached is not null)
        {
            progress?.Report(cached.Length);
            return cached;
        }

        BlobDownloadResult download = await DownloadFullBlobAsync(source, digest, cancellationToken);
        using Stream content = download.Content;
        return await PublishBlobAsync(content, digest, expectedSize, cancellationToken, progress);
    }

    public Task<StoredLayerIndex> GetIndexAsync(
        IDockerRegistryClient client, ImageName image, ImageLayerReference layer, long? expectedSize,
        CancellationToken cancellationToken, IProgress<long>? progress = null,
        ImageFileSystemSemantics? semantics = null) =>
        GetIndexAsync(new RegistryImageContentSource(client, image), layer, expectedSize,
            cancellationToken, progress, semantics: semantics);

    public async Task<StoredLayerIndex> GetIndexAsync(
        IImageContentSource source, ImageLayerReference layer, long? expectedSize,
        CancellationToken cancellationToken, IProgress<long>? progress = null,
        string? expectedDiffId = null,
        ImageFileSystemSemantics? semantics = null)
    {
        semantics ??= ImageFileSystemSemantics.Linux;
        using IDisposable operation = EnterOperation();
        if (source is DockerImageArchive retained &&
            retained.GetRetainedIndex(layer.Digest, expectedSize) is StoredLayerIndex warm &&
            IsValid(warm, layer.Digest, expectedSize))
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report(warm.BlobLength);
            return warm;
        }
        StoredLayerIndex? index = await ReadMetadataAsync<StoredLayerIndex>(
            layer.Digest, semantics.IndexKind, cancellationToken);
        if (IsValid(index, layer.Digest, expectedSize))
        {
            progress?.Report(index!.BlobLength);
            return index!;
        }
        if (index is not null)
        {
            diagnostics.WriteLine($"Rebuilding invalid layer index for '{layer.Digest}'.");
        }

        using Stream blob = await OpenIndexBlobAsync(
            source, layer.Digest, expectedSize, cancellationToken, progress);
        using FileStream indexLock = await LockAsync($"{semantics.IndexKind}:{layer.Digest}", cancellationToken);
        index = await ReadMetadataAsync<StoredLayerIndex>(layer.Digest, semantics.IndexKind, cancellationToken);
        if (IsValid(index, layer.Digest, expectedSize))
        {
            return index!;
        }

        if (source is DockerImageArchive prepared &&
            prepared.GetPreparedIndex(layer.Digest, expectedSize) is StoredLayerIndex ready)
        {
            await WriteMetadataAsync(layer.Digest, semantics.IndexKind, ready, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            return ready;
        }
        string encoding = ImageTarReader.DetectEncoding(blob);
        string algorithm = "sha256";
        if (expectedDiffId is not null)
        {
            try
            {
                _ = GetKey(expectedDiffId);
                algorithm = expectedDiffId[..expectedDiffId.IndexOf(':')];
            }
            catch (InvalidDataException)
            {
                // Registry-only indexing does not require valid configuration diff IDs.
            }
        }
        bool hasArchiveProof = source is DockerImageArchive archive && expectedDiffId is not null &&
            archive.TryGetVerifiedContentDigest(layer.Digest, expectedSize, expectedDiffId, out _);
        using SHA256? contentHash = hasArchiveProof ? null : SHA256.Create();
        using SHA512? sha512 = !hasArchiveProof && algorithm == "sha512" ? SHA512.Create() : null;
        using BufferedStream? sequential = source is DockerImageArchive && encoding == "tar"
            ? new(blob, 81920) : null;
        LayerChanges changes = await ImageLayerScanner.ScanAsync(
            sequential ?? blob, layer, cancellationToken, semantics, contentHash, sha512, encoding);
        index = new(layer.Digest, blob.Length, changes, encoding);
        await WriteMetadataAsync(layer.Digest, semantics.IndexKind, index, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        // Archive members have no cache pin; their proofs must not outlive the archive lease.
        if (source is DockerImageArchive scanned)
        {
            scanned.RetainScannedIndex(index);
        }
        else if (contentHash is not null)
        {
            string identity = $"sha256:{Convert.ToHexStringLower(contentHash.Hash!)}";
            string actualDiffId = sha512 is null ? identity : $"sha512:{Convert.ToHexStringLower(sha512.Hash!)}";
            lock (verificationLock)
            {
                if (!contentVerifications.ContainsKey((layer.Digest, actualDiffId)))
                {
                    TaskCompletionSource<string> verification = new(TaskCreationOptions.RunContinuationsAsynchronously);
                    verification.SetResult(identity);
                    contentVerifications.Add((layer.Digest, actualDiffId), verification);
                }
            }
        }
        return index;
    }

    internal async Task<StoredLayerIndex?> GetValidIndexAsync(
        string digest, long expectedSize, CancellationToken cancellationToken,
        ImageFileSystemSemantics? semantics = null)
    {
        semantics ??= ImageFileSystemSemantics.Linux;
        using IDisposable operation = EnterOperation();
        StoredLayerIndex? index = await ReadMetadataAsync<StoredLayerIndex>(
            digest, semantics.IndexKind, cancellationToken);
        bool valid = IsValid(index, digest, expectedSize);
        cancellationToken.ThrowIfCancellationRequested();
        return valid ? index : null;
    }

    internal async Task<Stream> OpenExtractionBlobAsync(
        IImageContentSource source, string digest, long? expectedSize, CancellationToken cancellationToken)
    {
        using IDisposable operation = EnterOperation();
        Stream blob = await OpenIndexBlobAsync(source, digest, expectedSize, cancellationToken, null);
        try
        {
            return source is DockerImageArchive && ImageTarReader.DetectEncoding(blob) == "tar"
                ? new BufferedStream(blob, 81920) : blob;
        }
        catch
        {
            blob.Dispose();
            throw;
        }
    }

    private async Task<Stream> OpenIndexBlobAsync(
        IImageContentSource source, string digest, long? expectedSize,
        CancellationToken cancellationToken, IProgress<long>? progress)
    {
        if (source is not DockerImageArchive archive)
        {
            return await OpenBlobCoreAsync(source, digest, expectedSize, cancellationToken, progress);
        }
        BlobDownloadResult member = await archive.GetRangeAsync(digest, 0, null, cancellationToken);
        try
        {
            if (expectedSize.HasValue && member.Content.Length != expectedSize)
            {
                throw new InvalidDataException($"Layer '{digest}' does not match its advertised length.");
            }
            progress?.Report(member.Content.Length);
            return member.Content;
        }
        catch
        {
            member.Content.Dispose();
            throw;
        }
    }

    public Task<Stream> OpenIndexedBlobAsync(
        IDockerRegistryClient client, ImageName image, StoredLayerIndex index,
        IEnumerable<int> entryOrdinals, CancellationToken cancellationToken) =>
        OpenIndexedBlobAsync(new RegistryImageContentSource(client, image), index, entryOrdinals, cancellationToken);

    public async Task<Stream> OpenIndexedBlobAsync(
        IImageContentSource source, StoredLayerIndex index,
        IEnumerable<int> entryOrdinals, CancellationToken cancellationToken)
    {
        using IDisposable operation = EnterOperation();
        if (!IsValid(index, index.Digest, index.BlobLength))
        {
            throw new InvalidDataException("The layer index contains invalid content metadata.");
        }
        using FileStream? layerLock = source is DockerImageArchive ? null :
            await LockAsync(GetKey(index.Digest), cancellationToken);
        Stream? cached = source is DockerImageArchive ? null :
            await OpenCachedBlobAsync(index.Digest, index.BlobLength, cancellationToken);
        if (cached is not null)
        {
            return cached;
        }

        HashSet<int> ordinals = [.. entryOrdinals];
        ScannedEntry[] entries = [.. index.Changes.Entries.Where(entry => ordinals.Contains(entry.EntryIndex))];
        if (entries.Length == 0 || entries.Length != ordinals.Count ||
            entries.Any(entry => entry.Type != ImageFileType.File))
        {
            throw new InvalidDataException("The layer index does not contain the requested content.");
        }
        if (index.Encoding == "tar")
        {
            Stream? raw = source is DockerImageArchive archive
                ? await OpenRawArchiveIndexedBlobAsync(archive, index, entries, cancellationToken)
                : await OpenRawIndexedBlobAsync(source, index, entries, cancellationToken);
            if (raw is not null)
            {
                return raw;
            }
        }
        else
        {
            long prefixLength = entries.Max(entry => entry.CompressedHighWaterMark);
            BlobDownloadResult? download = null;
            try
            {
                download = await source.GetRangeAsync(
                    index.Digest, 0, prefixLength, cancellationToken);
            }
            catch (RegistryException exception) when (exception.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
            {
                diagnostics.WriteLine($"Range unavailable for '{index.Digest}'; downloading the complete layer.");
            }

            if (download is not null)
            {
                Stream content = download.Content;
                bool transferred = false;
                try
                {
                    if (!download.IsRangeHonored)
                    {
                        return await PublishBlobAsync(content, index.Digest, index.BlobLength, cancellationToken);
                    }

                    if (download.RangeStart == 0 &&
                        download.RangeEnd == prefixLength - 1 &&
                        (download.TotalLength is null || download.TotalLength == index.BlobLength))
                    {
                        FileStream? staged = null;
                        try
                        {
                            Stream prefix = content;
                            DockerImageArchive? archive = source as DockerImageArchive;
                            if (archive is not null)
                            {
                                if (content.Length != prefixLength || ImageTarReader.DetectEncoding(content) != index.Encoding)
                                {
                                    throw new InvalidDataException("The layer prefix does not match the cached index.");
                                }
                            }
                            else
                            {
                                staged = CreateScratchFile();
                                await CopyBoundedAsync(content, staged, prefixLength, cancellationToken);
                                staged.Position = 0;
                                prefix = staged;
                            }
                            ScannedEntry[] unverified = archive is null ? entries :
                                archive.GetUnverifiedEntries(index.Digest, index.Encoding, entries, prefixLength);
                            await ValidatePrefixAsync(prefix, index.Encoding, unverified, cancellationToken);
                            cancellationToken.ThrowIfCancellationRequested();
                            prefix.Position = 0;
                            archive?.RecordVerifiedEntries(index.Digest, index.Encoding, unverified, prefixLength);
                            transferred = ReferenceEquals(prefix, content);
                            staged = null;
                            return prefix;
                        }
                        catch (InvalidDataException exception)
                        {
                            diagnostics.WriteLine($"Invalid cached-index range for '{index.Digest}': {exception.Message} Downloading the complete layer.");
                        }
                        finally
                        {
                            if (staged is not null)
                            {
                                await staged.DisposeAsync();
                            }
                        }
                    }
                    else
                    {
                        diagnostics.WriteLine($"Unexpected range for '{index.Digest}'; downloading the complete layer.");
                    }
                }
                finally
                {
                    if (!transferred)
                    {
                        content.Dispose();
                    }
                }
            }
        }

        using FileStream? fallbackLock = source is DockerImageArchive
            ? await LockAsync(GetKey(index.Digest), cancellationToken) : null;
        if (source is DockerImageArchive)
        {
            Stream? fallback = await OpenCachedBlobAsync(index.Digest, index.BlobLength, cancellationToken);
            if (fallback is not null)
            {
                return fallback;
            }
        }
        BlobDownloadResult fullDownload = await DownloadFullBlobAsync(source, index.Digest, cancellationToken);
        using Stream full = fullDownload.Content;
        return await PublishBlobAsync(full, index.Digest, index.BlobLength, cancellationToken);
    }

    private async Task<Stream?> OpenRawArchiveIndexedBlobAsync(
        DockerImageArchive archive, StoredLayerIndex index, IReadOnlyList<ScannedEntry> entries,
        CancellationToken cancellationToken)
    {
        Stream member = await OpenIndexBlobAsync(archive, index.Digest, index.BlobLength, cancellationToken, null);
        try
        {
            if (ImageTarReader.DetectEncoding(member) != "tar")
            {
                throw new InvalidDataException("The layer encoding does not match the cached raw index.");
            }
            ScannedEntry[] unverified = archive.GetUnverifiedEntries(index.Digest, "tar", entries);
            await ValidatePrefixAsync(member, "tar", unverified, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            archive.RecordVerifiedEntries(index.Digest, "tar", unverified);
            return new IndexedReadStream(member, [.. entries.OrderBy(static entry => entry.UncompressedOffset)
                .Select(static entry => new RawExtent(entry.UncompressedOffset, entry.Size, entry.UncompressedOffset))]);
        }
        catch (InvalidDataException exception)
        {
            member.Dispose();
            diagnostics.WriteLine($"Invalid cached-index range for '{index.Digest}': {exception.Message} Downloading the complete layer.");
            return null;
        }
        catch
        {
            member.Dispose();
            throw;
        }
    }

    private async Task<Stream?> OpenRawIndexedBlobAsync(
        IImageContentSource source, StoredLayerIndex index, IReadOnlyList<ScannedEntry> entries,
        CancellationToken cancellationToken)
    {
        FileStream selected = CreateScratchFile();
        bool transferred = false;
        try
        {
            List<RawExtent> extents = [];
            string emptyHash = Convert.ToHexStringLower(SHA256.HashData(Array.Empty<byte>()));
            foreach (ScannedEntry entry in entries.Where(static entry => entry.Size == 0))
            {
                if (entry.ContentHash != emptyHash)
                {
                    diagnostics.WriteLine($"Invalid cached-index range for '{index.Digest}': The empty file did not match the indexed content hash. Downloading the complete layer.");
                    return null;
                }
                extents.Add(new(entry.UncompressedOffset, 0, 0));
            }
            ScannedEntry[] ordered = [.. entries.Where(static entry => entry.Size > 0)
                .OrderBy(static entry => entry.UncompressedOffset)];
            List<(int First, int Last)> ranges = [];
            long unrequestedBytes = index.BlobLength;
            for (int first = 0; first < ordered.Length;)
            {
                cancellationToken.ThrowIfCancellationRequested();
                const long maxRangeBytes = 1024 * 1024;
                const long maxGapBytes = 4096;
                long start = ordered[first].UncompressedOffset;
                long end = start + ordered[first].Size;
                int last = first + 1;
                while (last < ordered.Length &&
                    ordered[last].UncompressedOffset >= end &&
                    ordered[last].UncompressedOffset - end <= maxGapBytes &&
                    ordered[last].UncompressedOffset + ordered[last].Size - start <= maxRangeBytes)
                {
                    end = ordered[last].UncompressedOffset + ordered[last].Size;
                    last++;
                }
                ranges.Add((first, last));
                unrequestedBytes = Math.Max(0, unrequestedBytes - (end - start));
                first = last;
            }
            const int maxSparseRequests = 128;
            const long requestCostBytes = 64 * 1024;
            if (ranges.Count > maxSparseRequests &&
                unrequestedBytes <= (long)(ranges.Count - 1) * requestCostBytes)
            {
                diagnostics.WriteLine($"Fragmented selection for '{index.Digest}'; downloading the complete layer to avoid {ranges.Count} range requests.");
                return null;
            }
            long[] storedOffsets = new long[ordered.Length];
            long selectedLength = 0;
            for (int i = 0; i < ordered.Length; i++)
            {
                storedOffsets[i] = selectedLength;
                extents.Add(new(ordered[i].UncompressedOffset, ordered[i].Size, selectedLength));
                selectedLength = checked(selectedLength + ordered[i].Size);
            }
            selected.SetLength(selectedLength);
            var selectedHandle = selected.SafeFileHandle;
            const int maxConcurrentRanges = 4;
            CancellationTokenSource[] workers = [.. Enumerable.Range(0, Math.Min(maxConcurrentRanges, ranges.Count))
                .Select(_ => CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))];
            int nextRange = -1;
            object fallbackLock = new();
            bool fallback = false;
            BlobDownloadResult? ignoredRange = null;
            try
            {
                await Task.WhenAll(workers.Select((worker, workerIndex) => ReadRangesAsync(workerIndex)));
                cancellationToken.ThrowIfCancellationRequested();
                if (ignoredRange is not null)
                {
                    return await PublishBlobAsync(
                        ignoredRange.Content, index.Digest, index.BlobLength, cancellationToken);
                }
                if (fallback)
                {
                    return null;
                }
            }
            finally
            {
                ignoredRange?.Content.Dispose();
                foreach (CancellationTokenSource worker in workers)
                {
                    worker.Dispose();
                }
            }

            void StopRanges(int workerIndex, BlobDownloadResult? full = null)
            {
                lock (fallbackLock)
                {
                    if (fallback)
                    {
                        full?.Content.Dispose();
                        return;
                    }
                    fallback = true;
                    ignoredRange = full;
                    // Keep the winning full response alive; cancel and drain its siblings before publishing it.
                    for (int i = 0; i < workers.Length; i++)
                    {
                        if (full is null || i != workerIndex)
                        {
                            workers[i].Cancel();
                        }
                    }
                }
            }

            async Task ReadRangesAsync(int workerIndex)
            {
                CancellationToken token = workers[workerIndex].Token;
                byte[] buffer = new byte[81920];
                byte[] outputBuffer = new byte[81920];
                try
                {
                    while (true)
                    {
                        token.ThrowIfCancellationRequested();
                        int rangeIndex = Interlocked.Increment(ref nextRange);
                        if (rangeIndex >= ranges.Count)
                        {
                            return;
                        }
                        (int first, int last) = ranges[rangeIndex];
                        long start = ordered[first].UncompressedOffset;
                        long end = ordered[last - 1].UncompressedOffset + ordered[last - 1].Size;
                        BlobDownloadResult download = await source.GetRangeAsync(index.Digest, start, end - start, token);
                        if (!download.IsRangeHonored)
                        {
                            StopRanges(workerIndex, download);
                            return;
                        }
                        using Stream content = download.Content;
                        if (download.RangeStart != start || download.RangeEnd != end - 1 ||
                            (download.TotalLength.HasValue && download.TotalLength != index.BlobLength))
                        {
                            diagnostics.WriteLine($"Unexpected range for '{index.Digest}'; downloading the complete layer.");
                            StopRanges(workerIndex);
                            return;
                        }
                        long position = start;
                        long storedOffset = storedOffsets[first];
                        int buffered = 0;
                        for (int i = first; i < last; i++)
                        {
                            ScannedEntry entry = ordered[i];
                            await CopyBytesAsync(content, Stream.Null, entry.UncompressedOffset - position, buffer, token);
                            using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                            long remaining = entry.Size;
                            while (remaining > 0)
                            {
                                int read = await content.ReadAsync(
                                    outputBuffer.AsMemory(buffered, (int)Math.Min(outputBuffer.Length - buffered, remaining)), token);
                                if (read == 0)
                                {
                                    throw new InvalidDataException("The layer range ended before the selected file.");
                                }
                                hash.AppendData(outputBuffer, buffered, read);
                                buffered += read;
                                if (buffered == outputBuffer.Length)
                                {
                                    await RandomAccess.WriteAsync(selectedHandle, outputBuffer, storedOffset, token);
                                    storedOffset += buffered;
                                    buffered = 0;
                                }
                                remaining -= read;
                            }
                            if (Convert.ToHexStringLower(hash.GetHashAndReset()) != entry.ContentHash)
                            {
                                throw new InvalidDataException("The layer range did not match the indexed content hash or length.");
                            }
                            position = entry.UncompressedOffset + entry.Size;
                        }
                        if (await content.ReadAsync(buffer.AsMemory(0, 1), token) != 0)
                        {
                            throw new InvalidDataException("The layer range did not match the indexed content hash or length.");
                        }
                        if (buffered > 0)
                        {
                            await RandomAccess.WriteAsync(
                                selectedHandle, outputBuffer.AsMemory(0, buffered), storedOffset, token);
                        }
                    }
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
                {
                }
                catch (RegistryException exception) when (exception.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
                {
                    diagnostics.WriteLine($"Range unavailable for '{index.Digest}'; downloading the complete layer.");
                    StopRanges(workerIndex);
                }
                catch (InvalidDataException exception)
                {
                    diagnostics.WriteLine($"Invalid cached-index range for '{index.Digest}': {exception.Message} Downloading the complete layer.");
                    StopRanges(workerIndex);
                }
                catch
                {
                    StopRanges(workerIndex);
                    throw;
                }
            }
            cancellationToken.ThrowIfCancellationRequested();
            await selected.FlushAsync(cancellationToken);
            selected.Position = 0;
            Stream result = new IndexedReadStream(selected, [.. extents.OrderBy(static extent => extent.Offset)]);
            transferred = true;
            return result;
        }
        finally
        {
            if (!transferred)
            {
                await selected.DisposeAsync();
            }
        }
    }

    private static async Task<BlobDownloadResult> DownloadFullBlobAsync(
        IImageContentSource source, string digest, CancellationToken cancellationToken)
    {
        // The registry client's ordinary GetAsync buffers the entire response (at most 2 GB).
        BlobDownloadResult download = await source.GetRangeAsync(
            digest, 0, null, cancellationToken);
        if (download.IsRangeHonored &&
            (download.RangeStart != 0 ||
                (download.TotalLength is long total && download.RangeEnd != total - 1)))
        {
            download.Content.Dispose();
            throw new InvalidDataException($"The registry returned an incomplete layer '{digest}'.");
        }
        return download;
    }

    private static async Task ValidatePrefixAsync(
        Stream prefix, string encoding, IReadOnlyList<ScannedEntry> entries, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (entries.Count == 0)
        {
            return;
        }
        using Stream content = ImageTarReader.OpenContent(prefix, encoding);
        long position = 0;
        byte[] buffer = new byte[81920];
        foreach (ScannedEntry entry in entries.OrderBy(static entry => entry.UncompressedOffset))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (encoding == "tar" && prefix.CanSeek)
            {
                prefix.Seek(entry.UncompressedOffset, SeekOrigin.Begin);
            }
            else
            {
                await CopyBytesAsync(content, Stream.Null, entry.UncompressedOffset - position, buffer, cancellationToken);
            }
            if (await CopyAndHashAsync(content, Stream.Null, entry.Size, buffer, cancellationToken) != entry.ContentHash)
            {
                throw new InvalidDataException("The layer range did not match the indexed content hash.");
            }
            position = checked(entry.UncompressedOffset + entry.Size);
        }
    }

    private static async Task<string> CopyAndHashAsync(
        Stream source, Stream destination, long count, byte[] buffer, CancellationToken cancellationToken)
    {
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        while (count > 0)
        {
            int read = await source.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, count)), cancellationToken);
            if (read == 0)
            {
                throw new InvalidDataException("The layer range ended before the selected file.");
            }
            hash.AppendData(buffer, 0, read);
            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            count -= read;
        }
        return Convert.ToHexStringLower(hash.GetHashAndReset());
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
        string digest, long? expectedSize, CancellationToken cancellationToken,
        Func<Stream, CancellationToken, Task<bool>>? verify = null)
    {
        string path = GetBlobPath(digest);
        if (pins.ContainsKey(path))
        {
            FileStream pinned = OpenRead(path);
            if (expectedSize.HasValue && pinned.Length != expectedSize)
            {
                pinned.Dispose();
                throw new InvalidDataException($"Layer '{digest}' does not match its advertised length.");
            }
            return pinned;
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
            lock (verificationLock)
            {
                if (verifications.Owners.ContainsKey(path))
                {
                    if (expectedSize.HasValue && blob.Length != expectedSize)
                    {
                        blob.Dispose();
                        throw new InvalidDataException($"Layer '{digest}' does not match its advertised length.");
                    }
                    if (!TryAddPin(path, blob))
                    {
                        blob.Dispose();
                    }
                    File.SetLastWriteTimeUtc(path, DateTime.UtcNow);
                    return OpenRead(path);
                }
            }
        }
        bool valid;
        try
        {
            valid = (!expectedSize.HasValue || blob.Length == expectedSize) &&
                await (verify is null ? VerifyDigestAsync(blob, digest, cancellationToken) : verify(blob, cancellationToken));
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
        if (!TryAddPin(path, blob))
        {
            await blob.DisposeAsync();
        }
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow);
        return OpenRead(path);
    }

    private async Task<Stream> PublishBlobAsync(
        Stream source, string digest, long? expectedSize, CancellationToken cancellationToken,
        IProgress<long>? progress = null)
    {
        string staging = Path.Combine(dataPath, $"{Guid.NewGuid():N}.tmp");
        try
        {
            await using FileStream output = CacheFileSystem.CreateFile(staging);
            if (progress is null)
            {
                await source.CopyToAsync(output, cancellationToken);
            }
            else
            {
                byte[] buffer = new byte[81_920];
                long downloaded = 0;
                int count;
                while ((count = await source.ReadAsync(buffer, cancellationToken)) != 0)
                {
                    await output.WriteAsync(buffer.AsMemory(0, count), cancellationToken);
                    downloaded += count;
                    progress.Report(downloaded);
                }
            }
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
                if (!TryAddPin(path, pin))
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
        using IDisposable operation = EnterOperation();
        using FileStream gate = await LockAsync("maintenance", cancellationToken);
        string markerPath = Path.Combine(dataPath, $"{Guid.NewGuid():N}.scratch");
        string directoryPath = $"{markerPath}.dir";
        FileStream pin = CacheFileSystem.CreateFile(markerPath);
        try
        {
            CacheFileSystem.CreateDirectory(directoryPath);
            // Keep the marker leased until operation disposal so trimming cannot remove active scratch.
            TryAddPin(markerPath, pin);
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
            .OrderBy(static file => file.Extension is ".tmp" or ".scratch" ? 0 : file.Extension == ".blob" ? 1 : 2)
            .ThenBy(static file => file.LastWriteTimeUtc))
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
        string suffix = kind switch
        {
            "index" or "view" => kind,
            ImageFileSystemSemantics.WindowsIndexKind => ImageFileSystemSemantics.WindowsInterpretation + ".index",
            ImageFileSystemSemantics.WindowsViewKind => ImageFileSystemSemantics.WindowsInterpretation + ".view",
            _ => throw new ArgumentException("Unknown cache metadata kind.", nameof(kind))
        };
        return Path.Combine(dataPath, $"{GetKey(digest)}.{suffix}");
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

    internal static string GetKey(string digest)
    {
        string[] parts = digest.Split(':');
        if (parts.Length != 2 ||
            parts[0] is not ("sha256" or "sha512") ||
            parts[1].Length != (parts[0] == "sha256" ? 64 : 128) ||
            parts[1].Any(static character => !char.IsAsciiHexDigitLower(character)))
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
        => index is not null && index.Digest == digest &&
            (!expectedSize.HasValue || index.BlobLength == expectedSize) && index.HasValidMetadata;

    internal static bool ValidateIndex(StoredLayerIndex index)
    {
        if (index.Encoding is not ("gzip" or "tar") || index.BlobLength <= 0 ||
            index.Changes?.Entries is null || index.Changes.Whiteouts is null ||
            index.Changes.OpaqueDirectories is null)
        {
            return false;
        }
        int previousOrdinal = -1;
        foreach (ScannedEntry entry in index.Changes.Entries)
        {
            if (entry is null || entry.EntryIndex <= previousOrdinal || entry.Size < 0 ||
                entry.UncompressedOffset < 0 || entry.Size > long.MaxValue - entry.UncompressedOffset ||
                (index.Encoding == "tar" && entry.UncompressedOffset + entry.Size > index.BlobLength) ||
                entry.CompressedHighWaterMark <= 0 ||
                entry.CompressedHighWaterMark > index.BlobLength ||
                (entry.Type == ImageFileType.File &&
                    (entry.ContentHash?.Length != 64 || entry.ContentHash.Any(static c => !char.IsAsciiHexDigitLower(c)))))
            {
                return false;
            }
            previousOrdinal = entry.EntryIndex;
        }
        try
        {
            foreach (string path in index.Changes.Entries.Select(static entry => entry.Path)
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

    private bool TryAddPin(string path, FileStream pin)
    {
        lock (verificationLock)
        {
            if (!pins.TryAdd(path, pin))
            {
                return false;
            }
            verifications.Owners[path] = verifications.Owners.GetValueOrDefault(path) + 1;
            return true;
        }
    }

    private IDisposable EnterOperation()
    {
        lock (verificationLock)
        {
            ObjectDisposedException.ThrowIf(verificationDisposed, this);
            operations++;
            return new StoreOperation(this);
        }
    }

    public ValueTask DisposeAsync()
    {
        lock (verificationLock)
        {
            if (disposal is null)
            {
                verificationDisposed = true;
                if (operations == 0)
                {
                    operationsDrained.TrySetResult();
                }
                disposal = DisposeCoreAsync();
            }
            return new(disposal);
        }
    }

    private async Task DisposeCoreAsync()
    {
        await operationsDrained.Task;
        lock (verificationLock)
        {
            HashSet<string> released = new(StringComparer.Ordinal);
            try
            {
                foreach ((string path, FileStream pin) in pins)
                {
                    pin.Dispose();
                    if (--verifications.Owners[path] == 0)
                    {
                        verifications.Owners.Remove(path);
                        released.Add(path);
                    }
                }
                pins.Clear();
            }
            finally
            {
                if (released.Count != 0)
                {
                    foreach (var key in contentVerifications.Keys
                        .Where(key => contentVerifications[key].Task.IsCompleted &&
                            released.Contains(GetBlobPath(key.Digest))).ToArray())
                    {
                        contentVerifications.Remove(key);
                    }
                }
            }
        }
        await TrimAsync(CancellationToken.None);
    }

    private sealed class VerificationState
    {
        public object Sync { get; } = new();
        public Dictionary<(string Digest, string DiffId), TaskCompletionSource<string>> Tasks { get; } = [];
        public Dictionary<string, int> Owners { get; } = new(StringComparer.Ordinal);
    }

    private sealed class StoreOperation(LayerStore store) : IDisposable
    {
        public void Dispose()
        {
            lock (store.verificationLock)
            {
                if (--store.operations == 0 && store.verificationDisposed)
                {
                    store.operationsDrained.TrySetResult();
                }
            }
        }
    }

    private readonly record struct RawExtent(long Offset, long Size, long StoredOffset);

    private sealed class IndexedReadStream(Stream content, IReadOnlyList<RawExtent> extents) : Stream
    {
        private long position = extents[0].Offset;
        private readonly long length = extents.Max(static extent => extent.Offset + extent.Size);
        public override bool CanRead => content.CanRead;
        public override bool CanSeek => content.CanSeek;
        public override bool CanWrite => false;
        public override long Length => length;
        public override long Position
        {
            get => position;
            set => Seek(value, SeekOrigin.Begin);
        }

        public override long Seek(long value, SeekOrigin origin)
        {
            long position = origin switch
            {
                SeekOrigin.Begin => value,
                SeekOrigin.Current => checked(Position + value),
                SeekOrigin.End => checked(Length + value),
                _ => throw new ArgumentOutOfRangeException(nameof(origin))
            };
            int index = FindExtent(position);
            if (index < 0 || position > extents[index].Offset + extents[index].Size)
            {
                throw new InvalidDataException("The requested content is outside the verified layer range.");
            }
            this.position = position;
            return position;
        }

        private int PrepareRead(int count)
        {
            int index = FindExtent(position);
            if (index >= 0)
            {
                RawExtent extent = extents[index];
                if (position < extent.Offset + extent.Size)
                {
                    content.Position = checked(extent.StoredOffset + position - extent.Offset);
                    return (int)Math.Min(count, extent.Offset + extent.Size - position);
                }
            }
            return 0;
        }

        private int FindExtent(long value)
        {
            int low = 0;
            int high = extents.Count - 1;
            while (low <= high)
            {
                int middle = low + (high - low) / 2;
                if (extents[middle].Offset <= value)
                {
                    low = middle + 1;
                }
                else
                {
                    high = middle - 1;
                }
            }
            return high;
        }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
        public override int Read(Span<byte> buffer)
        {
            int read = content.Read(buffer[..PrepareRead(buffer.Length)]);
            position += read;
            return read;
        }
        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            int read = await content.ReadAsync(buffer[..PrepareRead(buffer.Length)], cancellationToken);
            position += read;
            return read;
        }
        public override Task<int> ReadAsync(
            byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
        public override void Flush() => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                content.Dispose();
            }
            base.Dispose(disposing);
        }
        public override ValueTask DisposeAsync() => content.DisposeAsync();
    }

}

internal sealed record StoredLayerIndex
{
    private readonly Lazy<bool> validMetadata;

    private StoredLayerIndex(StoredLayerIndex original)
    {
        Digest = original.Digest;
        BlobLength = original.BlobLength;
        Encoding = original.Encoding;
        Changes = original.Changes;
        validMetadata = new(() => LayerStore.ValidateIndex(this));
    }

    public StoredLayerIndex(string digest, long blobLength, LayerChanges changes, string encoding = "gzip")
    {
        Digest = digest;
        BlobLength = blobLength;
        Encoding = encoding;
        Changes = changes;
        if (changes is { Entries: not null, Whiteouts: not null, OpaqueDirectories: not null })
        {
            Changes = new(changes.Entries.ToImmutableArray(), changes.Whiteouts.ToImmutableArray(),
                changes.OpaqueDirectories.ToImmutableArray())
            {
                Diagnostics = changes.Diagnostics.ToImmutableArray()
            };
        }
        validMetadata = new(() => LayerStore.ValidateIndex(this));
    }

    public string Digest { get; init; }
    public long BlobLength { get; init; }
    public LayerChanges Changes { get; init; }
    public string Encoding { get; init; }
    internal bool HasValidMetadata => validMetadata.Value;
}
internal sealed record LayerCacheEnvelope(
    [property: JsonPropertyName("Version")] int Version,
    [property: JsonPropertyName("Digest")] string Digest,
    [property: JsonPropertyName("Checksum")] string Checksum,
    [property: JsonPropertyName("Payload")] string Payload);
