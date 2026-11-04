using System.Buffers;
using System.Formats.Tar;
using System.Security.Cryptography;
using System.Text.Json;
using Valleysoft.DockerRegistryClient;
using Valleysoft.DockerRegistryClient.Models.Manifests;
using Valleysoft.DockerRegistryClient.Models.Manifests.Oci;
using Image = Valleysoft.DockerRegistryClient.Models.Images.Image;

namespace Valleysoft.Dredge;

internal sealed class DockerImageArchive : IImageContentSource, IAsyncDisposable
{
    internal const string RawLayerMediaType = "application/vnd.oci.image.layer.v1.tar";
    private readonly string directory;
    private readonly Dictionary<string, ArchiveMember> blobs = new(StringComparer.Ordinal);
    private readonly HashSet<VerifiedEntry> verifiedEntries = [];
    private readonly object entryLock = new();
    private SnapshotFile? snapshot;
    private bool disposed;

    private DockerImageArchive(string directory)
    {
        this.directory = directory;
    }

    public byte[] ConfigurationJson { get; private set; } = [];
    public Image Configuration { get; private set; } = null!;
    public ResolvedManifest Resolved { get; private set; } = null!;
    public ExplorerPlatform Platform { get; private set; } = null!;
    internal ImageFileSystem.ValidatedFileSystem? CachedFileSystem { get; private set; }

    internal static async Task<DockerImageArchive> OpenAsync(
        Stream export,
        string imageId,
        CancellationToken cancellationToken,
        string? scratchRoot = null,
        string? os = null,
        string? architecture = null,
        string? osVersion = null,
        string? variant = null,
        bool verifyLayerContent = true,
        bool prepareFileSystem = false,
        Func<string, long, CancellationToken, Task<StoredLayerIndex?>>? hasLayerIndex = null,
        int? maxLayerIndex = null,
        Func<string, int, CancellationToken, Task<ImageFileSystem.ValidatedFileSystem?>>? getFileSystemView = null,
        bool prepareCachedContent = false)
    {
        if (!IsSha256(imageId))
        {
            throw new InvalidDataException("Docker inspect did not return a full sha256 Docker image ID.");
        }
        scratchRoot ??= GetDefaultSnapshotRoot();
        CacheFileSystem.CreateDirectory(scratchRoot);
        string directory = Path.Combine(scratchRoot, Guid.NewGuid().ToString("N"));
        CacheFileSystem.CreateDirectory(directory);
        DockerImageArchive archive = new(directory);
        try
        {
            string spool = Path.Combine(directory, "export.tar");
            await using (FileStream file = CacheFileSystem.CreateFile(spool))
            {
                using CancellationTokenRegistration registration = cancellationToken.Register(export.Dispose);
                await export.CopyToAsync(file, cancellationToken);
            }
            cancellationToken.ThrowIfCancellationRequested();
            archive.snapshot = new SnapshotFile(spool);
            Dictionary<string, ArchiveMember> entries = new(StringComparer.Ordinal);
            await using (Stream file = new ArchiveRangeStream(archive.snapshot, 0, archive.snapshot.Length))
            await using (TarReader reader = new(file))
            {
                if (file.Length < 1024 || file.Length % 512 != 0)
                {
                    throw new InvalidDataException("Docker export is not a complete tar archive.");
                }
                file.Position = file.Length - 1024;
                byte[] terminator = new byte[1024];
                await file.ReadExactlyAsync(terminator, cancellationToken);
                if (terminator.Any(value => value != 0))
                {
                    throw new InvalidDataException("Docker export is missing its tar end-of-archive records.");
                }
                file.Position = 0;
                TarEntry? entry;
                HashSet<string> names = new(StringComparer.Ordinal);
                while ((entry = await reader.GetNextEntryAsync(cancellationToken: cancellationToken)) is not null)
                {
                    if (entry.EntryType == TarEntryType.Directory && entry.Name is "." or "./")
                    {
                        continue;
                    }
                    string name = ValidatePath(entry.Name);
                    if (!names.Add(name))
                    {
                        throw new InvalidDataException($"Docker export contains duplicate archive entry '{name}'.");
                    }
                    if (entry.EntryType == TarEntryType.Directory)
                    {
                        continue;
                    }
                    if (entry.EntryType is not (TarEntryType.RegularFile or TarEntryType.V7RegularFile))
                    {
                        throw new InvalidDataException($"Docker export entry '{name}' is not a regular file.");
                    }
                    long offset = entry.DataOffset;
                    if (offset < 0 || entry.Length < 0 || offset > file.Length - entry.Length)
                    {
                        throw new InvalidDataException($"Docker export entry '{name}' is truncated.");
                    }
                    entries.Add(name, new ArchiveMember(archive.snapshot, offset, entry.Length));
                }
            }
            await archive.ResolveAsync(entries, imageId, os, architecture, osVersion, variant,
                verifyLayerContent, prepareFileSystem, hasLayerIndex, maxLayerIndex, getFileSystemView,
                prepareCachedContent, cancellationToken);
            return archive;
        }
        catch
        {
            await archive.DisposeAsync();
            cancellationToken.ThrowIfCancellationRequested();
            throw;
        }
    }

    internal static string GetDefaultSnapshotRoot()
    {
        string applicationData = Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.DoNotVerify);
        if (!Path.IsPathFullyQualified(applicationData))
        {
            throw new InvalidOperationException("The local application data directory must be an absolute path.");
        }
        return Path.Combine(applicationData, "Valleysoft.Dredge", "snapshots");
    }

    private async Task ResolveAsync(Dictionary<string, ArchiveMember> entries, string imageId, string? os,
        string? architecture, string? osVersion, string? variant, bool verifyLayerContent, bool prepareFileSystem,
        Func<string, long, CancellationToken, Task<StoredLayerIndex?>>? hasLayerIndex,
        int? maxLayerIndex,
        Func<string, int, CancellationToken, Task<ImageFileSystem.ValidatedFileSystem?>>? getFileSystemView,
        bool prepareCachedContent,
        CancellationToken ct)
    {
        ArchiveMember configPath;
        string configurationId = imageId;
        ExplorerPlatform inspectedPlatform = new(os ?? "", architecture ?? "", variant, osVersion);
        ExplorerPlatform? selectedPlatform = null;
        List<(ArchiveMember Path, string? MediaType, string? Digest)> layers = [];
        if (!entries.ContainsKey("oci-layout") && entries.TryGetValue("manifest.json", out ArchiveMember? manifestPath))
        {
            using JsonDocument document = await ReadJsonAsync(manifestPath, ct);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                throw new InvalidDataException("Docker export manifest.json must be an array.");
            }
            JsonElement? selected = null;
            List<string> exportedIds = [];
            foreach (JsonElement candidate in document.RootElement.EnumerateArray())
            {
                ArchiveMember candidateConfig = FindEntry(entries, RequiredString(candidate, "Config"));
                string digest = await HashFileAsync(candidateConfig, ct);
                exportedIds.Add(digest);
                if (digest == imageId)
                {
                    if (selected is not null)
                    {
                        throw new InvalidDataException("Docker export contains ambiguous duplicate image records.");
                    }
                    selected = candidate;
                }
            }
            JsonElement record = selected ?? throw new InvalidDataException(
                $"Docker export does not contain pinned Docker image ID '{imageId}' (exported configurations: {string.Join(", ", exportedIds)}).");
            configPath = FindEntry(entries, RequiredString(record, "Config"));
            JsonElement layerList = record.GetProperty("Layers");
            if (layerList.ValueKind != JsonValueKind.Null)
            {
                foreach (JsonElement layer in layerList.EnumerateArray())
                {
                    layers.Add((FindEntry(entries, layer.GetString() ?? throw new InvalidDataException("Null layer path.")), null, null));
                }
            }
        }
        else
        {
            if (!entries.TryGetValue("oci-layout", out ArchiveMember? layout))
            {
                throw new InvalidDataException("Docker export contains neither a Docker-save manifest nor an OCI layout.");
            }
            using (JsonDocument layoutDocument = await ReadJsonAsync(layout, ct))
            {
                if (RequiredString(layoutDocument.RootElement, "imageLayoutVersion") != "1.0.0")
                {
                    throw new InvalidDataException("Unsupported OCI image layout version.");
                }
            }
            using JsonDocument index = await ReadJsonAsync(FindEntry(entries, "index.json"), ct);
            ValidateSchema(index.RootElement);
            List<(string Digest, JsonElement Manifest, ExplorerPlatform? Platform)> manifests = [];
            bool pinnedGraph = false;
            ArchiveMember pinnedPath = FindEntry(entries, "blobs/sha256/" + imageId[7..]);
            if (await HashFileAsync(pinnedPath, ct) != imageId)
            {
                throw new InvalidDataException("Docker export pinned identity blob does not match the inspected image ID.");
            }
            using (JsonDocument pinned = await ReadJsonAsync(pinnedPath, ct))
            {
                bool isConfiguration = pinned.RootElement.TryGetProperty("rootfs", out _) &&
                    pinned.RootElement.TryGetProperty("os", out _) &&
                    pinned.RootElement.TryGetProperty("architecture", out _);
                if (!isConfiguration && pinned.RootElement.TryGetProperty("manifests", out _))
                {
                    ValidateSchema(pinned.RootElement);
                    await FindOciManifestsAsync(pinned.RootElement, entries, manifests, [], [], [], 0,
                        os, architecture, osVersion, variant, ct);
                    pinnedGraph = true;
                }
                else if (!isConfiguration && pinned.RootElement.TryGetProperty("config", out _) &&
                    pinned.RootElement.TryGetProperty("layers", out _))
                {
                    ValidateSchema(pinned.RootElement);
                    manifests.Add((imageId, pinned.RootElement.Clone(), null));
                    pinnedGraph = true;
                }
                else
                {
                    await FindOciManifestsAsync(index.RootElement, entries, manifests, [], [], [], 0,
                        os ?? OptionalString(pinned.RootElement, "os"),
                        architecture ?? OptionalString(pinned.RootElement, "architecture"),
                        osVersion ?? OptionalString(pinned.RootElement, "os.version"),
                        variant ?? OptionalString(pinned.RootElement, "variant"), ct);
                }
            }
            JsonElement? selected = null;
            string? selectedDigest = null;
            foreach ((string manifestDigest, JsonElement manifest, ExplorerPlatform? descriptorPlatform) in manifests)
            {
                ExplorerPlatform? candidatePlatform = null;
                JsonElement configDescriptor = manifest.GetProperty("config");
                string configMediaType = RequiredString(configDescriptor, "mediaType");
                if (configMediaType is not ("application/vnd.oci.image.config.v1+json" or
                    "application/vnd.docker.container.image.v1+json"))
                {
                    continue;
                }
                bool matches = RequiredString(configDescriptor, "digest") == imageId;
                if (pinnedGraph || matches)
                {
                    if (pinnedGraph && (string.IsNullOrEmpty(os) || string.IsNullOrEmpty(architecture)))
                    {
                        throw new InvalidDataException("Docker index/manifest image IDs require an inspected concrete platform before export acquisition.");
                    }
                    ArchiveMember candidatePath = await VerifyDescriptorAsync(configDescriptor, entries, ct);
                    using JsonDocument candidate = await ReadJsonAsync(candidatePath, ct);
                    JsonElement candidateConfig = candidate.RootElement;
                    ExplorerPlatform configuredPlatform = new(
                        RequiredString(candidateConfig, "os"), RequiredString(candidateConfig, "architecture"),
                        OptionalString(candidateConfig, "variant"), OptionalString(candidateConfig, "os.version"));
                    matches = (descriptorPlatform is null || PlatformsMatch(configuredPlatform, descriptorPlatform)) &&
                        PlatformsMatch(configuredPlatform, inspectedPlatform);
                    if (matches)
                    {
                        candidatePlatform = MergePlatform(
                            MergePlatform(configuredPlatform, descriptorPlatform), inspectedPlatform);
                    }
                }
                if (matches)
                {
                    if (selected is not null)
                    {
                        if (selectedDigest == manifestDigest &&
                            selectedPlatform is not null && candidatePlatform is not null &&
                            selectedPlatform.IsEquivalentTo(candidatePlatform))
                        {
                            continue;
                        }
                        throw new InvalidDataException("OCI export contains ambiguous manifests for the pinned image ID.");
                    }
                    selected = manifest;
                    selectedDigest = manifestDigest;
                    selectedPlatform = candidatePlatform;
                }
            }
            JsonElement chosen = selected ?? throw new InvalidDataException(
                $"OCI export has no unique image configuration matching the platform of pinned Docker image ID '{imageId}' (exported configurations: {string.Join(", ", manifests.Select(manifest => RequiredString(manifest.Manifest.GetProperty("config"), "digest")))}).");
            ValidateSchema(chosen);
            configurationId = RequiredString(chosen.GetProperty("config"), "digest");
            configPath = await VerifyDescriptorAsync(chosen.GetProperty("config"), entries, ct);
            foreach (JsonElement layer in chosen.GetProperty("layers").EnumerateArray())
            {
                layers.Add((await VerifyDescriptorAsync(layer, entries, ct, verifyHash: false),
                    RequiredString(layer, "mediaType"), RequiredString(layer, "digest")));
            }
        }

        if (configPath.Length > 16 * 1024 * 1024)
        {
            throw new InvalidDataException("Docker export configuration exceeds the supported 16 MiB limit.");
        }
        await using (Stream input = configPath.Open())
        {
            ConfigurationJson = new byte[(int)configPath.Length];
            await input.ReadExactlyAsync(ConfigurationJson, ct);
        }
        if (Hash(ConfigurationJson) != configurationId)
        {
            throw new InvalidDataException("Docker export configuration hash does not match its verified configuration digest.");
        }
        string configurationText = ImageSourceFactory.DecodeConfiguration(ConfigurationJson);
        using JsonDocument configuration = JsonDocument.Parse(configurationText);
        JsonElement root = configuration.RootElement;
        if (!root.TryGetProperty("rootfs", out JsonElement rootfs) ||
            RequiredString(rootfs, "type") != "layers")
        {
            throw new InvalidDataException("Docker export configuration has no supported rootfs layer list.");
        }
        string[] diffIds = [];
        if (rootfs.TryGetProperty("diff_ids", out JsonElement diffIdList))
        {
            if (diffIdList.ValueKind != JsonValueKind.Array ||
                diffIdList.EnumerateArray().Any(value => value.ValueKind != JsonValueKind.String))
            {
                throw new InvalidDataException("Docker export rootfs diff IDs must be an array of strings.");
            }
            diffIds = diffIdList.EnumerateArray().Select(value => value.GetString()!).ToArray();
        }
        if (diffIds.Length != layers.Count)
        {
            throw new InvalidDataException("Docker export layer count/diff IDs do not match the image configuration.");
        }
        foreach (string diffId in diffIds)
        {
            _ = LayerStore.GetKey(diffId);
        }
        if (root.TryGetProperty("history", out JsonElement history) && history.GetArrayLength() != 0 &&
            history.EnumerateArray().Count(value =>
                !value.TryGetProperty("empty_layer", out JsonElement empty) || !empty.GetBoolean()) != layers.Count)
        {
            throw new InvalidDataException("Docker export history does not match its filesystem layers.");
        }
        Configuration = JsonHelper.Deserialize<Image>(configurationText) ??
            throw new InvalidDataException("Docker export configuration is invalid.");
        ExplorerPlatform configured = ExplorerPlatform.FromConfig(Configuration);
        if (!PlatformsMatch(configured, inspectedPlatform))
        {
            throw new InvalidDataException("Docker exported configuration platform does not match the pinned image inspection.");
        }
        Platform = selectedPlatform ?? MergePlatform(configured, inspectedPlatform);
        ImageFileSystemSemantics semantics = ImageFileSystemSemantics.ForOs(Configuration.Os);
        foreach (var layer in layers)
        {
            semantics.ValidateLayerMediaType(layer.MediaType);
        }
        if (prepareFileSystem && maxLayerIndex is null && getFileSystemView is not null)
        {
            CachedFileSystem = await getFileSystemView(configurationId, layers.Count, ct);
        }
        HashSet<int> contentLayers = CachedFileSystem is not null && prepareCachedContent
            ? CachedFileSystem.Entries.Where(entry => entry.Value.Type == ImageFileType.File ||
                (entry.Value.Type == ImageFileType.HardLink && entry.ContentLinkTarget is null))
                .Select(entry => entry.ContentLayerIndex).ToHashSet()
            : [];
        ILookup<ArchiveMember, (ArchiveMember Path, string? MediaType, string? Digest, string DiffId, string? CachedDigest)> memberLayers =
            layers.Select((layer, index) => (layer.Path, layer.MediaType, layer.Digest, DiffId: diffIds[index],
                CachedDigest: CachedFileSystem?.Layers[index]))
                .ToLookup(layer => layer.Path);
        HashSet<ArchiveMember> sha512Members = layers.Where((_, index) =>
            diffIds[index].StartsWith("sha512:", StringComparison.Ordinal)).Select(layer => layer.Path).ToHashSet();
        HashSet<ArchiveMember> fileSystemMembers = prepareFileSystem
            ? layers.Where((_, index) => (maxLayerIndex is null || index <= maxLayerIndex) &&
                (CachedFileSystem is null || contentLayers.Contains(index)))
                .Select(layer => layer.Path).ToHashSet()
            : [];
        await VerifyMembersAsync(memberLayers.Select(group => group.Key), async (path, token) =>
        {
            await using (Stream input = path.Open())
            {
                path.IsGzip = ImageTarReader.DetectEncoding(input) == "gzip";
                foreach (var layer in memberLayers[path])
                {
                    string? mediaType = layer.MediaType;
                    if (!verifyLayerContent || mediaType is null)
                    {
                        continue;
                    }
                    bool declaredGzip = mediaType is "application/vnd.oci.image.layer.v1.tar+gzip" or
                        "application/vnd.docker.image.rootfs.diff.tar.gzip" or
                        "application/vnd.docker.image.rootfs.foreign.diff.tar.gzip" or
                        "application/vnd.oci.image.layer.nondistributable.v1.tar+gzip";
                    bool declaredRaw = mediaType is RawLayerMediaType or
                        "application/vnd.docker.image.rootfs.diff.tar" or
                        "application/vnd.oci.image.layer.nondistributable.v1.tar";
                    if ((!declaredGzip && !declaredRaw) || declaredGzip != path.IsGzip)
                    {
                        throw new InvalidDataException($"Unsupported or mismatched Docker export layer encoding '{mediaType}'.");
                    }
                }
                if (verifyLayerContent && path.DiffId is null)
                {
                    string? knownDigest = path.Digest ?? memberLayers[path].First().Digest;
                    if (!path.IsGzip)
                    {
                        knownDigest ??= memberLayers[path].Select(layer => layer.DiffId).FirstOrDefault(IsSha256);
                    }
                    knownDigest ??= memberLayers[path].First().CachedDigest;
                    bool prepare = fileSystemMembers.Contains(path);
                    StoredLayerIndex? retained = prepare && knownDigest is not null && hasLayerIndex is not null
                        ? await hasLayerIndex(knownDigest, path.Length, token) : null;
                    if (prepare && retained is null)
                    {
                        using SHA256? encodedHash = path.IsGzip ? SHA256.Create() : null;
                        using SHA256 decodedHash = SHA256.Create();
                        using SHA512? sha512 = sha512Members.Contains(path) ? SHA512.Create() : null;
                        using CryptoStream? encoded = encodedHash is null ? null :
                            new(input, encodedHash, CryptoStreamMode.Read, leaveOpen: true);
                        using BufferedStream? sequential = path.IsGzip ? null : new(input, 81920);
                        string encoding = path.IsGzip ? "gzip" : "tar";
                        LayerChanges changes = await ImageLayerScanner.ScanAsync(
                            encoded ?? (Stream?)sequential ?? input, new(0, knownDigest ?? imageId),
                            token, semantics, decodedHash, sha512, encoding);
                        if (encoded is not null)
                        {
                            await encoded.CopyToAsync(Stream.Null, token);
                        }
                        token.ThrowIfCancellationRequested();
                        path.DiffId = "sha256:" + Convert.ToHexStringLower(decodedHash.Hash!);
                        path.Digest = encodedHash is null ? path.DiffId :
                            "sha256:" + Convert.ToHexStringLower(encodedHash.Hash!);
                        path.Sha512DiffId = sha512 is null ? null : "sha512:" + Convert.ToHexStringLower(sha512.Hash!);
                        path.Index = new(path.Digest, path.Length, changes, encoding);
                    }
                    else if (path.IsGzip)
                    {
                        (path.Digest, path.DiffId, path.Sha512DiffId) =
                            await HashGzipLayerAsync(input, sha512Members.Contains(path), token);
                    }
                    else
                    {
                        (path.DiffId, path.Sha512DiffId) =
                            await HashContentAsync(input, sha512Members.Contains(path), token);
                        path.Digest = path.DiffId;
                    }
                    path.RetainedIndex = retained is not null && retained.Digest == path.Digest &&
                        retained.Encoding == (path.IsGzip ? "gzip" : "tar") ? retained : null;
                }
            }
            await HashFileAsync(path, token);
        }, ct);
        OciDescriptor[] descriptors = new OciDescriptor[layers.Count];
        for (int i = 0; i < layers.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            (ArchiveMember path, string? mediaType, string? expectedDigest) = layers[i];
            string? actualDiffId = diffIds[i].StartsWith("sha512:", StringComparison.Ordinal)
                ? path.Sha512DiffId : path.DiffId;
            if (verifyLayerContent && actualDiffId != diffIds[i])
            {
                throw new InvalidDataException($"Docker export layer {i + 1} does not match its rootfs diff ID.");
            }
            string digest = path.Digest!;
            if (expectedDigest is not null && digest != expectedDigest)
            {
                throw new InvalidDataException($"OCI export blob '{expectedDigest}' does not match its descriptor.");
            }
            blobs.TryAdd(digest, path);
            descriptors[i] = new OciDescriptor
            {
                Digest = digest,
                Size = path.Length,
                MediaType = !verifyLayerContent && mediaType is not null ? mediaType :
                    path.IsGzip ? "application/vnd.oci.image.layer.v1.tar+gzip" : RawLayerMediaType
            };
        }
        blobs.TryAdd(configurationId, configPath);
        OciImageManifest internalManifest = new()
        {
            SchemaVersion = 2,
            MediaType = "application/vnd.oci.image.manifest.v1+json",
            Config = new OciDescriptor { Digest = configurationId, Size = ConfigurationJson.Length, MediaType = "application/vnd.oci.image.config.v1+json" },
            Layers = descriptors
        };
        byte[] manifestJson = System.Text.Encoding.UTF8.GetBytes(JsonHelper.Serialize(internalManifest));
        Resolved = new ResolvedManifest(
            new ManifestInfo(internalManifest.MediaType, Hash(manifestJson), internalManifest, manifestJson), internalManifest);
    }

    internal bool TryGetVerifiedContentDigest(
        string digest, long? expectedSize, string expectedDiffId, out string identity)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        identity = "";
        if (!blobs.TryGetValue(digest, out ArchiveMember? member) || member.DiffId is null ||
            (expectedDiffId != member.DiffId && expectedDiffId != member.Sha512DiffId))
        {
            return false;
        }
        if (expectedSize.HasValue && expectedSize != member.Length)
        {
            throw new InvalidDataException($"Layer '{digest}' does not match its advertised length.");
        }
        identity = member.DiffId;
        return true;
    }

    internal async Task<string> VerifyContentAsync(
        string digest, long? expectedSize, string expectedDiffId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (TryGetVerifiedContentDigest(digest, expectedSize, expectedDiffId, out string identity))
        {
            return identity;
        }
        ArchiveMember member = RequireMember(digest, expectedSize);
        using Stream input = member.Open();
        return await LayerStore.VerifyPinnedContentAsync(input, digest, expectedDiffId, cancellationToken);
    }

    internal ScannedEntry[] GetUnverifiedEntries(string digest, string encoding, IReadOnlyList<ScannedEntry> entries,
        long? prefixLength = null)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        lock (entryLock)
        {
            bool sharedPrefix = encoding == "gzip" && prefixLength.HasValue &&
                entries.Any(entry => entry.CompressedHighWaterMark == prefixLength &&
                    HasEntryProof(digest, encoding, entry, prefixLength));
            return [.. entries.Where(entry => !HasEntryProof(digest, encoding, entry, prefixLength) &&
                !(sharedPrefix && entry.CompressedHighWaterMark <= prefixLength &&
                    HasEntryProof(digest, encoding, entry, entry.CompressedHighWaterMark)))];
        }
    }

    private bool HasEntryProof(string digest, string encoding, ScannedEntry entry, long? prefixLength)
    {
        VerifiedEntry proof = GetEntryProof(digest, encoding, entry, prefixLength);
        if (verifiedEntries.Contains(proof))
        {
            return true;
        }
        if (!blobs.TryGetValue(digest, out ArchiveMember? member) ||
            member.Index is not StoredLayerIndex index || index.Encoding != encoding)
        {
            return false;
        }
        IReadOnlyList<ScannedEntry> scanned = index.Changes.Entries;
        int low = 0;
        int high = scanned.Count - 1;
        while (low <= high)
        {
            int middle = low + (high - low) / 2;
            ScannedEntry candidate = scanned[middle];
            if (candidate.EntryIndex == entry.EntryIndex)
            {
                return candidate.Type == ImageFileType.File &&
                    GetEntryProof(digest, encoding, candidate, candidate.CompressedHighWaterMark) == proof;
            }
            if (candidate.EntryIndex < entry.EntryIndex)
            {
                low = middle + 1;
            }
            else
            {
                high = middle - 1;
            }
        }
        return false;
    }

    internal void RetainScannedIndex(StoredLayerIndex index)
    {
        lock (entryLock)
        {
            if (!disposed)
            {
                RequireMember(index.Digest, index.BlobLength).Index = index;
            }
        }
    }

    internal void RecordVerifiedEntries(string digest, string encoding, IReadOnlyList<ScannedEntry> entries,
        long? prefixLength = null)
    {
        lock (entryLock)
        {
            if (disposed)
            {
                return;
            }
            foreach (ScannedEntry entry in entries)
            {
                if (entry.Type == ImageFileType.File &&
                    !HasEntryProof(digest, encoding, entry, prefixLength ?? entry.CompressedHighWaterMark))
                {
                    verifiedEntries.Add(GetEntryProof(digest, encoding, entry, prefixLength ?? entry.CompressedHighWaterMark));
                }
            }
        }
    }

    private static VerifiedEntry GetEntryProof(string digest, string encoding, ScannedEntry entry, long? prefixLength) =>
        new(digest, encoding, entry.UncompressedOffset, entry.Size, entry.ContentHash,
            encoding == "gzip" ? prefixLength : null);

    private readonly record struct VerifiedEntry(string Digest, string Encoding, long Offset, long Size, string? Hash,
        long? PrefixLength);

    internal StoredLayerIndex? GetRetainedIndex(string digest, long? expectedSize)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        return RequireMember(digest, expectedSize).RetainedIndex;
    }

    internal StoredLayerIndex? GetPreparedIndex(string digest, long? expectedSize)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        return RequireMember(digest, expectedSize).Index;
    }

    private ArchiveMember RequireMember(string digest, long? expectedSize)
    {
        if (!blobs.TryGetValue(digest, out ArchiveMember? member))
        {
            throw new InvalidDataException($"Blob '{digest}' is not part of this Docker snapshot.");
        }
        if (expectedSize.HasValue && expectedSize != member.Length)
        {
            throw new InvalidDataException($"Layer '{digest}' does not match its advertised length.");
        }
        return member;
    }

    private static async Task<int> FindOciManifestsAsync(JsonElement index, Dictionary<string, ArchiveMember> entries,
        List<(string Digest, JsonElement Manifest, ExplorerPlatform? Platform)> manifests,
        HashSet<string> active, Dictionary<string, (JsonElement Document, bool IsIndex)> documents,
        Dictionary<(string Digest, ExplorerPlatform? Platform), int> completed, int depth,
        string? os, string? architecture, string? osVersion, string? variant, CancellationToken ct,
        ExplorerPlatform? inheritedPlatform = null)
    {
        if (depth > 8)
        {
            throw new InvalidDataException("OCI export index nesting is too deep.");
        }
        int height = 0;
        foreach (JsonElement descriptor in index.GetProperty("manifests").EnumerateArray())
        {
            ct.ThrowIfCancellationRequested();
            ExplorerPlatform? descriptorPlatform = inheritedPlatform;
            if (descriptor.TryGetProperty("platform", out JsonElement platform))
            {
                ExplorerPlatform current = new(OptionalString(platform, "os") ?? "",
                    OptionalString(platform, "architecture") ?? "", OptionalString(platform, "variant"),
                    OptionalString(platform, "os.version"));
                if (inheritedPlatform is not null && !PlatformsMatch(current, inheritedPlatform))
                {
                    throw new InvalidDataException("OCI export contains conflicting nested descriptor platforms.");
                }
                descriptorPlatform = MergePlatform(current, inheritedPlatform);
            }
            if (descriptorPlatform is not null &&
                !PlatformsMatch(descriptorPlatform, new(os ?? "", architecture ?? "", variant, osVersion)))
            {
                continue;
            }
            string digest = RequiredString(descriptor, "digest");
            if (!active.Add(digest))
            {
                throw new InvalidDataException("OCI export contains cyclic descriptors.");
            }
            ArchiveMember path = await VerifyDescriptorAsync(descriptor, entries, ct);
            string mediaType = RequiredString(descriptor, "mediaType");
            if (mediaType is not ("application/vnd.oci.image.index.v1+json" or
                "application/vnd.docker.distribution.manifest.list.v2+json" or
                "application/vnd.oci.image.manifest.v1+json" or
                "application/vnd.docker.distribution.manifest.v2+json"))
            {
                throw new InvalidDataException($"Unsupported OCI export descriptor media type '{mediaType}'.");
            }
            bool isIndex = mediaType is "application/vnd.oci.image.index.v1+json" or
                "application/vnd.docker.distribution.manifest.list.v2+json";
            bool hasDocument = documents.TryGetValue(digest, out var cached);
            if (hasDocument && cached.IsIndex != isIndex)
            {
                throw new InvalidDataException("OCI export contains inconsistent descriptor media types.");
            }
            var context = (digest, NormalizePlatform(descriptorPlatform));
            if (completed.TryGetValue(context, out int cachedHeight))
            {
                if (depth + cachedHeight > 8)
                {
                    throw new InvalidDataException("OCI export index nesting is too deep.");
                }
                height = Math.Max(height, cachedHeight);
                active.Remove(digest);
                continue;
            }
            JsonElement document;
            if (hasDocument)
            {
                document = cached.Document;
            }
            else
            {
                using JsonDocument parsed = await ReadJsonAsync(path, ct);
                ValidateSchema(parsed.RootElement);
                document = parsed.RootElement.Clone();
                documents.Add(digest, (document, isIndex));
            }
            int descriptorHeight = 0;
            if (isIndex)
            {
                descriptorHeight = 1 + await FindOciManifestsAsync(document, entries, manifests, active, documents, completed, depth + 1,
                    os, architecture, osVersion, variant, ct, descriptorPlatform);
            }
            else
            {
                manifests.Add((digest, document, descriptorPlatform));
            }
            active.Remove(digest);
            completed.Add(context, descriptorHeight);
            height = Math.Max(height, descriptorHeight);
        }
        return height;
    }

    private static ExplorerPlatform? NormalizePlatform(ExplorerPlatform? platform) => platform is null ? null :
        new(platform.Os.ToLowerInvariant(), platform.Architecture.ToLowerInvariant(),
            string.IsNullOrEmpty(platform.Variant) ? null : platform.Variant.ToLowerInvariant(),
            string.IsNullOrEmpty(platform.OsVersion) ? null : platform.OsVersion.ToLowerInvariant());

    private static bool PlatformsMatch(ExplorerPlatform left, ExplorerPlatform right) =>
        Matches(left.Os, right.Os) && Matches(left.Architecture, right.Architecture) &&
        Matches(left.Variant, right.Variant) && Matches(left.OsVersion, right.OsVersion);

    private static bool Matches(string? left, string? right) =>
        string.IsNullOrEmpty(left) || string.IsNullOrEmpty(right) ||
        string.Equals(left, right, StringComparison.OrdinalIgnoreCase);

    private static ExplorerPlatform MergePlatform(ExplorerPlatform configured, ExplorerPlatform? fallback) =>
        new(string.IsNullOrEmpty(configured.Os) ? fallback?.Os ?? "" : configured.Os,
            string.IsNullOrEmpty(configured.Architecture) ? fallback?.Architecture ?? "" : configured.Architecture,
            string.IsNullOrEmpty(configured.Variant) ? fallback?.Variant : configured.Variant,
            string.IsNullOrEmpty(configured.OsVersion) ? fallback?.OsVersion : configured.OsVersion);

    private static async Task<ArchiveMember> VerifyDescriptorAsync(JsonElement descriptor, Dictionary<string, ArchiveMember> entries,
        CancellationToken ct, bool verifyHash = true)
    {
        string digest = RequiredString(descriptor, "digest");
        if (!IsSha256(digest))
        {
            throw new InvalidDataException("OCI export descriptors must contain full sha256 digests.");
        }
        ArchiveMember path = FindEntry(entries, "blobs/sha256/" + digest[7..]);
        if (path.Length != descriptor.GetProperty("size").GetInt64() ||
            (verifyHash && await HashFileAsync(path, ct) != digest))
        {
            throw new InvalidDataException($"OCI export blob '{digest}' does not match its descriptor.");
        }
        return path;
    }

    public Task<BlobDownloadResult> GetRangeAsync(string digest, long start, long? length, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        if (!blobs.TryGetValue(digest, out ArchiveMember? path))
        {
            throw new InvalidDataException($"Blob '{digest}' is not part of this Docker snapshot.");
        }
        if (start < 0 || length < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(start));
        }
        if (start > path.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(start));
        }
        long count = Math.Min(length ?? path.Length - start, path.Length - start);
        Stream bounded = path.Open(start, count);
        return Task.FromResult(new BlobDownloadResult(bounded, true, start,
            count == 0 ? null : start + count - 1, path.Length));
    }

    public ValueTask DisposeAsync()
    {
        if (!disposed)
        {
            disposed = true;
            CachedFileSystem = null;
            lock (entryLock)
            {
                verifiedEntries.Clear();
            }
            snapshot?.Release();
            Directory.Delete(directory, recursive: true);
        }
        return ValueTask.CompletedTask;
    }

    internal static bool IsSha256(string value) => value.Length == 71 && value.StartsWith("sha256:", StringComparison.Ordinal) &&
        value.AsSpan(7).ContainsAnyExcept("0123456789abcdef") == false;

    internal static string Hash(ReadOnlySpan<byte> bytes) => "sha256:" + Convert.ToHexStringLower(SHA256.HashData(bytes));

    internal static async Task<(string Digest, string DiffId)> HashGzipLayerAsync(Stream input, CancellationToken ct)
    {
        var result = await HashGzipLayerAsync(input, false, ct);
        return (result.Digest, result.DiffId);
    }

    internal static async Task<(string DiffId, string? Sha512DiffId)> HashContentAsync(
        Stream input, bool includeSha512, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        using IncrementalHash sha256 = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using IncrementalHash? sha512 = includeSha512 ? IncrementalHash.CreateHash(HashAlgorithmName.SHA512) : null;
        byte[] buffer = ArrayPool<byte>.Shared.Rent(81920);
        try
        {
            int read;
            while ((read = await input.ReadAsync(buffer.AsMemory(0, 81920), ct)) > 0)
            {
                sha256.AppendData(buffer, 0, read);
                sha512?.AppendData(buffer, 0, read);
            }
            ct.ThrowIfCancellationRequested();
            return ("sha256:" + Convert.ToHexStringLower(sha256.GetHashAndReset()),
                sha512 is null ? null : "sha512:" + Convert.ToHexStringLower(sha512.GetHashAndReset()));
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static async Task<(string Digest, string DiffId, string? Sha512DiffId)> HashGzipLayerAsync(
        Stream input, bool includeSha512, CancellationToken ct)
    {
        using SHA256 encodedHash = SHA256.Create();
        using CryptoStream encoded = new(input, encodedHash, CryptoStreamMode.Read, leaveOpen: true);
        using Stream decoded = ImageTarReader.OpenContent(encoded, "gzip");
        (string diffId, string? sha512DiffId) = await HashContentAsync(decoded, includeSha512, ct);
        await encoded.CopyToAsync(Stream.Null, ct);
        return ("sha256:" + Convert.ToHexStringLower(encodedHash.Hash!), diffId, sha512DiffId);
    }

    internal static async Task VerifyMembersAsync<T>(
        IEnumerable<T> members, Func<T, CancellationToken, Task> verify, CancellationToken ct)
        where T : notnull
    {
        ct.ThrowIfCancellationRequested();
        T[] uniqueMembers = members.Distinct().ToArray();
        try
        {
            await Parallel.ForAsync(0, uniqueMembers.Length, new ParallelOptions
            {
                MaxDegreeOfParallelism = 4,
                TaskScheduler = TaskScheduler.Default,
                CancellationToken = ct
            }, (index, token) => new ValueTask(verify(uniqueMembers[index], token)));
        }
        finally
        {
            ct.ThrowIfCancellationRequested();
        }
    }

    private static async Task<string> HashFileAsync(ArchiveMember path, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (path.Digest is null)
        {
            await using Stream stream = path.Open();
            path.Digest = (await HashContentAsync(stream, includeSha512: false, ct)).DiffId;
        }
        return path.Digest;
    }

    private static async Task<JsonDocument> ReadJsonAsync(ArchiveMember path, CancellationToken ct)
    {
        if (path.Length > 16 * 1024 * 1024)
        {
            throw new InvalidDataException("Docker export JSON metadata exceeds the supported 16 MiB limit.");
        }
        await using Stream stream = path.Open();
        return await JsonDocument.ParseAsync(stream, cancellationToken: ct);
    }

    private static string RequiredString(JsonElement element, string property) =>
        element.GetProperty(property).GetString() ?? throw new InvalidDataException($"Missing Docker export property '{property}'.");

    private static string? OptionalString(JsonElement element, string property) =>
        element.TryGetProperty(property, out JsonElement value) ? value.GetString() : null;

    private static void ValidateSchema(JsonElement element)
    {
        if (!element.TryGetProperty("schemaVersion", out JsonElement version) || version.GetInt32() != 2)
        {
            throw new InvalidDataException("Docker OCI export must use manifest/index schema version 2.");
        }
    }

    private static ArchiveMember FindEntry(Dictionary<string, ArchiveMember> entries, string name) =>
        entries.TryGetValue(ValidatePath(name), out ArchiveMember? path) ? path :
            throw new InvalidDataException($"Docker export references missing entry '{name}'.");

    private static string ValidatePath(string name)
    {
        while (name.StartsWith("./", StringComparison.Ordinal))
        {
            name = name[2..];
        }
        name = name.TrimEnd('/');
        if (name.Length == 0 || name.StartsWith('/') || name.Contains('\\') || name.Contains(':') ||
            name.Any(char.IsControl) || name.Split('/').Any(part => part is "" or "." or ".."))
        {
            throw new InvalidDataException("Docker export contains an unsafe archive path.");
        }
        return name;
    }

    private sealed class SnapshotFile
    {
        private readonly FileStream file;
        private readonly object sync = new();
        private int owners = 1;

        public SnapshotFile(string path)
        {
            file = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.Read | FileShare.Delete, 1, FileOptions.Asynchronous | FileOptions.RandomAccess);
            try
            {
                if (!OperatingSystem.IsWindows())
                {
                    File.SetUnixFileMode(file.SafeFileHandle, UnixFileMode.UserRead);
                }
            }
            catch
            {
                file.Dispose();
                throw;
            }
        }

        public Microsoft.Win32.SafeHandles.SafeFileHandle Handle => file.SafeFileHandle;
        public long Length => file.Length;

        public void Acquire()
        {
            lock (sync)
            {
                ObjectDisposedException.ThrowIf(owners == 0, this);
                owners++;
            }
        }

        public void Release()
        {
            lock (sync)
            {
                if (--owners == 0)
                {
                    file.Dispose();
                }
            }
        }
    }

    private sealed class ArchiveMember
    {
        private readonly SnapshotFile snapshot;
        private readonly long offset;

        public ArchiveMember(SnapshotFile snapshot, long offset, long length)
        {
            this.snapshot = snapshot;
            this.offset = offset;
            Length = length;
        }

        public long Length { get; }
        public string? Digest { get; set; }
        public string? DiffId { get; set; }
        public string? Sha512DiffId { get; set; }
        public bool IsGzip { get; set; }
        public StoredLayerIndex? Index { get; set; }
        public StoredLayerIndex? RetainedIndex { get; set; }
        public Stream Open(long start = 0, long? count = null) =>
            new ArchiveRangeStream(snapshot, offset + start, count ?? Length - start);
    }

    private sealed class ArchiveRangeStream : Stream
    {
        private readonly SnapshotFile snapshot;
        private readonly long offset;
        private readonly long length;
        private long position;
        private bool disposed;

        public ArchiveRangeStream(SnapshotFile snapshot, long offset, long length)
        {
            snapshot.Acquire();
            this.snapshot = snapshot;
            this.offset = offset;
            this.length = length;
        }

        public override bool CanRead => !disposed;
        public override bool CanSeek => !disposed;
        public override bool CanWrite => false;
        public override long Length => length;
        public override long Position { get => position; set => Seek(value, SeekOrigin.Begin); }
        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
        public override int Read(Span<byte> buffer)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            int count = RandomAccess.Read(snapshot.Handle,
                buffer[..(int)Math.Min(buffer.Length, length - position)], offset + position);
            if (count == 0 && position != length && buffer.Length != 0)
            {
                throw new EndOfStreamException("Docker export archive member is truncated.");
            }
            position += count;
            return count;
        }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            int count = await RandomAccess.ReadAsync(snapshot.Handle,
                buffer[..(int)Math.Min(buffer.Length, length - position)], offset + position, cancellationToken);
            if (count == 0 && position != length && buffer.Length != 0)
            {
                throw new EndOfStreamException("Docker export archive member is truncated.");
            }
            position += count;
            return count;
        }
        protected override void Dispose(bool disposing)
        {
            if (disposing && !disposed)
            {
                disposed = true;
                snapshot.Release();
            }
            base.Dispose(disposing);
        }
        public override ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            long target = origin switch
            {
                SeekOrigin.Begin => offset,
                SeekOrigin.Current => checked(position + offset),
                SeekOrigin.End => checked(length + offset),
                _ => throw new ArgumentOutOfRangeException(nameof(origin))
            };
            if (target < 0 || target > length)
            {
                throw new IOException("Cannot seek outside a Docker export archive member.");
            }
            position = target;
            return position;
        }
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
