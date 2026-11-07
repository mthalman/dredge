using System.Formats.Tar;
using System.IO.Compression;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Valleysoft.Dredge;

internal static class ImageLayerScanner
{
    private const string WhiteoutPrefix = ".wh.";
    private const string OpaqueWhiteout = ".wh..wh..opq";

    public static Task<LayerChanges> ScanAsync(
        Stream blob,
        ImageLayerReference layer,
        CancellationToken cancellationToken,
        HashAlgorithm? decodedHash,
        HashAlgorithm? additionalDecodedHash = null,
        string? encoding = null) =>
        ScanAsync(blob, layer, cancellationToken, semantics: null, decodedHash: decodedHash,
            additionalDecodedHash: additionalDecodedHash, encoding: encoding);

    public static async Task<LayerChanges> ScanAsync(
        Stream blob,
        ImageLayerReference layer,
        CancellationToken cancellationToken,
        ImageFileSystemSemantics? semantics = null,
        HashAlgorithm? decodedHash = null,
        HashAlgorithm? additionalDecodedHash = null,
        string? encoding = null)
    {
        semantics ??= ImageFileSystemSemantics.Linux;
        List<ScannedEntry> entries = [];
        List<string> whiteouts = [];
        List<string> opaqueDirectories = [];
        Dictionary<string, LayerDiagnostic> diagnostics = new(StringComparer.Ordinal);
        HashSet<string> spellings = new(semantics.Comparer);
        Dictionary<string, string> globalAttributes = new(StringComparer.Ordinal);
        WindowsMetadataCache? metadataCache = semantics.IsWindows ? new() : null;
        void Report(string key, LayerDiagnostic diagnostic)
        {
            if (diagnostics.Count < 64)
            {
                diagnostics.TryAdd(key, diagnostic);
            }
            else
            {
                diagnostics.TryAdd("limit", new("", "Additional Windows layer warnings were omitted; unsupported entries retain their individual limitations."));
            }
        }

        encoding ??= ImageTarReader.DetectEncoding(blob);
        using CountingReadStream compressed = new(blob);
        using Stream content = ImageTarReader.OpenContent(compressed, encoding);
        using CryptoStream? additionalHashing = additionalDecodedHash is null
            ? null
            : new(content, additionalDecodedHash, CryptoStreamMode.Read, leaveOpen: true);
        using CryptoStream? hashing = decodedHash is null
            ? null
            : new(additionalHashing ?? content, decodedHash, CryptoStreamMode.Read, leaveOpen: true);
        using CountingReadStream uncompressed = new(hashing ?? additionalHashing ?? content);
        using TarReader tar = new(uncompressed, leaveOpen: true);
        int entryIndex = 0;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            TarEntry? tarEntry = await ImageTarReader.GetNextEntryAsync(
                tar,
                layer,
                cancellationToken);
            if (tarEntry is null)
            {
                break;
            }
            int currentEntryIndex = entryIndex++;
            if (semantics.IsWindows && tarEntry is PaxGlobalExtendedAttributesTarEntry global)
            {
                if (UpdateGlobalWindowsAttributes(globalAttributes, global.GlobalExtendedAttributes))
                {
                    metadataCache!.Clear();
                }
                await ImageTarReader.DrainEntryAsync(tarEntry, layer, cancellationToken);
                continue;
            }
            string? mapped = semantics.MapArchivePath(tarEntry.Name);
            long offset = uncompressed.BytesRead;
            ImageFileType type = GetFileType(tarEntry);

            if (mapped is null)
            {
                await ReadContentAsync(tarEntry, layer, uncompressed, offset, false, cancellationToken);
                Report("runtime", new(tarEntry.Name,
                    "Windows Hives/UtilityVM runtime payloads are excluded from the container filesystem."));
                continue;
            }
            string path = mapped;
            if (path.Length == 0)
            {
                if (semantics.IsWindows)
                {
                    WindowsFileMetadata root = ReadWindowsMetadataCore(tarEntry, path, globalAttributes, metadataCache);
                    if (type != ImageFileType.Directory || root.UnsupportedReason is not null)
                    {
                        throw new NotSupportedException(
                            $"Windows filesystem root has unsupported semantics: {root.UnsupportedReason ?? "The root entry must be a directory."}");
                    }
                }
                await ReadContentAsync(tarEntry, layer, uncompressed, offset, false, cancellationToken);
                continue;
            }

            string fileName = ImagePath.GetFileName(path);
            string parent = ImagePath.GetDirectoryName(path);
            if (string.Equals(fileName, OpaqueWhiteout, StringComparison.Ordinal))
            {
                ValidateRootOpaqueWhiteout(tarEntry, parent);
                await ReadContentAsync(tarEntry, layer, uncompressed, offset, false, cancellationToken);
                opaqueDirectories.Add(parent);
                continue;
            }

            if (fileName.StartsWith(WhiteoutPrefix, StringComparison.Ordinal))
            {
                string targetName = fileName[WhiteoutPrefix.Length..];
                ImagePath.ValidateSegment(targetName, "whiteout target");
                string targetPath = ImagePath.Join(parent, targetName);
                string target = semantics.NormalizeArchivePath(targetPath);
                await ReadContentAsync(tarEntry, layer, uncompressed, offset, false, cancellationToken);
                whiteouts.Add(target);
                continue;
            }

            if (semantics.IsWindows)
            {
                if (spellings.TryGetValue(path, out string? spelling) && spelling != path)
                {
                    throw new NotSupportedException($"Windows layer contains ambiguous case-colliding paths '{spelling}' and '{path}'.");
                }
                spellings.Add(path);
            }
            WindowsFileMetadata? windows = semantics.IsWindows
                ? ReadWindowsMetadataCore(tarEntry, path, globalAttributes, metadataCache) : null;
            if (windows?.UnsupportedReason is string unsupported)
            {
                if (tarEntry.EntryType == TarEntryType.Directory)
                {
                    throw new NotSupportedException($"Windows directory '{path}' has unsupported filesystem semantics: {unsupported}");
                }
                Report(unsupported, new(path, unsupported));
                type = ImageFileType.Other;
            }
            if (windows?.Attributes is uint fileAttributes && (fileAttributes & 0x400) != 0 &&
                type is ImageFileType.File or ImageFileType.Directory or ImageFileType.HardLink)
            {
                Report("reparse-attributes", new(path,
                    "Windows reparse attributes on non-link tar entries are retained as metadata; content-only analysis follows the archive entry type and does not recreate host reparse points."));
            }
            string? linkTarget = type is ImageFileType.SymbolicLink or ImageFileType.HardLink
                ? tarEntry.LinkName
                : null;
            if (semantics.IsWindows && type == ImageFileType.HardLink)
            {
                linkTarget = semantics.MapArchivePath(linkTarget!) ??
                    throw new NotSupportedException($"Hard link '{path}' targets an excluded Windows runtime payload.");
            }
            if (linkTarget is not null && ImagePath.ContainsControlCharacter(linkTarget))
            {
                throw new InvalidDataException(
                    $"Link '/{path}' has an invalid target.");
            }
            if (type == ImageFileType.SymbolicLink)
            {
                string basePath = semantics.IsAbsolute(linkTarget!)
                    ? string.Empty
                    : parent;
                try
                {
                    _ = semantics.ResolveLinkTarget(basePath, linkTarget!, string.Empty, path);
                }
                catch (Exception exception) when (semantics.IsWindows &&
                    exception is InvalidDataException or NotSupportedException)
                {
                    type = ImageFileType.Other;
                    windows = windows! with { UnsupportedReason = exception.Message };
                    Report("link", new(path, exception.Message));
                }
            }

            string? contentHash = await ReadContentAsync(tarEntry, layer, uncompressed, offset,
                GetFileType(tarEntry) == ImageFileType.File, cancellationToken);
            entries.Add(new ScannedEntry(
                path,
                type,
                (int)tarEntry.Mode,
                tarEntry.Uid,
                tarEntry.Gid,
                type == ImageFileType.File ? tarEntry.Length : 0,
                tarEntry.ModificationTime.UtcDateTime,
                linkTarget,
                currentEntryIndex,
                offset,
                encoding == "tar" ? uncompressed.BytesRead : compressed.BytesRead,
                contentHash) { Windows = windows });
        }
        if (semantics.IsWindows)
        {
            Report("metadata", new("", "Windows content-only analysis: ACLs, ownership, extended attributes and NTFS allocation semantics are not interpreted."));
        }
        if (hashing is not null || additionalHashing is not null)
        {
            await uncompressed.CopyToAsync(Stream.Null, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
        }
        return new(entries, whiteouts, opaqueDirectories) { Diagnostics = [.. diagnostics.Values] };
    }

    private static async Task<string?> ReadContentAsync(TarEntry entry, ImageLayerReference layer,
        CountingReadStream uncompressed, long offset, bool hashContent, CancellationToken cancellationToken)
    {
        string? hash = hashContent ? await ImageTarReader.HashEntryAsync(entry, layer, cancellationToken) : null;
        await ImageTarReader.DrainEntryAsync(entry, layer, cancellationToken);
        if (GetFileType(entry) == ImageFileType.File && uncompressed.BytesRead - offset != entry.Length)
        {
            throw new InvalidDataException(
                $"Layer {layer.Index} ('{layer.Digest}') contains truncated content for '{entry.Name}'.");
        }
        return hash;
    }

    private static bool UpdateGlobalWindowsAttributes(
        Dictionary<string, string> attributes, IEnumerable<KeyValuePair<string, string>> changes)
    {
        bool changed = false;
        foreach ((string key, string value) in changes)
        {
            if (!key.StartsWith("MSWINDOWS.", StringComparison.Ordinal)) continue;
            if (value.Length == 0) changed |= attributes.Remove(key);
            else if (!attributes.TryGetValue(key, out string? previous) || previous != value)
            {
                attributes[key] = value;
                changed = true;
            }
        }
        return changed;
    }

    private static WindowsFileMetadata ReadWindowsMetadata(TarEntry entry, string path,
        IReadOnlyDictionary<string, string> globalAttributes) =>
        ReadWindowsMetadataCore(entry, path, globalAttributes
            .Where(static pair => pair.Key.StartsWith("MSWINDOWS.", StringComparison.Ordinal))
            .ToDictionary(static pair => pair.Key, static pair => pair.Value, StringComparer.Ordinal), null);

    private static WindowsFileMetadata ReadWindowsMetadataCore(TarEntry entry, string path,
        IReadOnlyDictionary<string, string> globalAttributes, WindowsMetadataCache? cache)
    {
        Dictionary<string, string>? overrides = null;
        if (entry is PaxTarEntry pax)
        {
            foreach ((string key, string value) in pax.ExtendedAttributes)
            {
                if (!key.StartsWith("MSWINDOWS.", StringComparison.Ordinal))
                {
                    continue;
                }
                bool present = globalAttributes.TryGetValue(key, out string? inherited);
                if (value.Length == 0 ? !present : present && inherited == value) continue;
                overrides ??= new(StringComparer.Ordinal);
                overrides.Add(key, value);
            }
        }
        ParsedWindowsMetadata metadata;
        if (overrides is null)
        {
            metadata = cache is null ? ParseWindowsMetadata(globalAttributes, path)
                : cache.Global ??= ParseWindowsMetadata(globalAttributes, path);
        }
        else if (cache?.Find(overrides, globalAttributes) is { } local)
        {
            metadata = local;
        }
        else
        {
            Dictionary<string, string> effective = new(globalAttributes, StringComparer.Ordinal);
            foreach ((string key, string value) in overrides)
            {
                if (value.Length == 0) effective.Remove(key);
                else effective[key] = value;
            }
            metadata = ParseWindowsMetadata(effective, path);
            if (cache is not null)
            {
                cache.Remember(overrides, metadata);
            }
        }
        string? reason = entry.EntryType == TarEntryType.Directory ? metadata.DirectoryReason : metadata.Reason;
        reason ??= path.Contains(':') ? "Alternate data streams are not supported." : null;
        if (metadata.MountPoint && entry.EntryType != TarEntryType.SymbolicLink)
        {
            reason = "A Windows mountpoint must have a symbolic-link tar representation.";
        }
        return new(metadata.Attributes, metadata.MountPoint, metadata.HasSecurityDescriptor, metadata.Fingerprint, reason);
    }

    private static ParsedWindowsMetadata ParseWindowsMetadata(IReadOnlyDictionary<string, string> attributes, string path)
    {
        uint? fileAttributes = null;
        if (attributes.TryGetValue("MSWINDOWS.fileattr", out string? raw))
        {
            if (!uint.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out uint parsed))
            {
                throw new InvalidDataException($"Invalid Windows file attributes for '{path}'.");
            }
            fileAttributes = parsed;
        }
        string? reason = null;
        string? directoryReason = null;
        bool mountPoint = attributes.ContainsKey("MSWINDOWS.mountpoint");
        foreach (string key in attributes.Keys)
        {
            if (key.StartsWith("MSWINDOWS.xattr.", StringComparison.Ordinal))
            {
                directoryReason = "Directory extended attributes may alter Windows filesystem semantics and are not supported.";
            }
            if (!IsKnownWindowsAttribute(key))
            {
                reason = $"Unsupported Windows metadata '{TerminalText.EscapeControls(key)}'.";
                directoryReason = reason;
            }
        }
        return new(fileAttributes, mountPoint, attributes.ContainsKey("MSWINDOWS.rawsd") ||
            attributes.ContainsKey("MSWINDOWS.sd"), MetadataFingerprint(attributes), reason, directoryReason);
    }

    private sealed record ParsedWindowsMetadata(uint? Attributes, bool MountPoint, bool HasSecurityDescriptor,
        string Fingerprint, string? Reason, string? DirectoryReason);

    private static bool IsKnownWindowsAttribute(string key) =>
        key is "MSWINDOWS.fileattr" or "MSWINDOWS.sd" or "MSWINDOWS.rawsd" or "MSWINDOWS.mountpoint" ||
        key.StartsWith("MSWINDOWS.xattr.", StringComparison.Ordinal);

    private sealed class WindowsMetadataCache
    {
        private const int MaxProfiles = 8;
        private const long MaxRetainedBytes = 128 * 1024;
        private readonly List<OverrideProfile> locals = [];
        private long retainedBytes;
        private bool? knownGlobals;

        public ParsedWindowsMetadata? Global { get; set; }

        public ParsedWindowsMetadata? Find(Dictionary<string, string> overrides,
            IReadOnlyDictionary<string, string> globals)
        {
            // Unknown metadata makes diagnostic precedence depend on merged dictionary order.
            bool ignoreOrder = (knownGlobals ??= globals.Keys.All(IsKnownWindowsAttribute)) &&
                overrides.Keys.All(IsKnownWindowsAttribute);
            for (int i = locals.Count - 1; i >= 0; i--)
            {
                OverrideProfile profile = locals[i];
                if (overrides.Count != profile.Overrides.Count) continue;
                bool matches = ignoreOrder
                    ? SameValues(overrides, profile.Overrides)
                    : overrides.SequenceEqual(profile.Overrides);
                if (!matches) continue;
                if (i != locals.Count - 1)
                {
                    locals.RemoveAt(i);
                    locals.Add(profile);
                }
                return profile.Metadata;
            }
            return null;
        }

        public void Remember(Dictionary<string, string> overrides, ParsedWindowsMetadata metadata)
        {
            // Conservatively include object storage so small strings cannot evade the retention bound.
            long bytes = 512 + StringBytes(metadata.Fingerprint) +
                StringBytes(metadata.Reason) + StringBytes(metadata.DirectoryReason);
            foreach ((string key, string value) in overrides)
            {
                bytes += 128 + StringBytes(key) + StringBytes(value);
            }
            if (bytes > MaxRetainedBytes) return;
            while (locals.Count >= MaxProfiles || retainedBytes + bytes > MaxRetainedBytes)
            {
                retainedBytes -= locals[0].Bytes;
                locals.RemoveAt(0);
            }
            locals.Add(new(overrides, metadata, bytes));
            retainedBytes += bytes;
        }

        public void Clear()
        {
            Global = null;
            knownGlobals = null;
            locals.Clear();
            retainedBytes = 0;
        }

        private static long StringBytes(string? value) => value is null ? 0 : 32 + 2L * value.Length;

        private static bool SameValues(Dictionary<string, string> first, Dictionary<string, string> second)
        {
            foreach ((string key, string value) in first)
            {
                if (!second.TryGetValue(key, out string? other) || value != other) return false;
            }
            return true;
        }

        private sealed record OverrideProfile(Dictionary<string, string> Overrides, ParsedWindowsMetadata Metadata, long Bytes);
    }

    private static string MetadataFingerprint(IReadOnlyDictionary<string, string> attributes)
    {
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Encoder encoder = Encoding.UTF8.GetEncoder();
        Span<byte> buffer = stackalloc byte[1024];
        bool first = true;
        foreach ((string key, string value) in attributes.OrderBy(static pair => pair.Key, StringComparer.Ordinal))
        {
            if (!first) hash.AppendData("\n"u8);
            first = false;
            AppendUtf8(hash, encoder, key, buffer);
            hash.AppendData("="u8);
            AppendUtf8(hash, encoder, value, buffer);
        }
        Span<byte> digest = stackalloc byte[SHA256.HashSizeInBytes];
        hash.GetHashAndReset(digest);
        return Convert.ToHexStringLower(digest);
    }

    private static void AppendUtf8(IncrementalHash hash, Encoder encoder, ReadOnlySpan<char> text, Span<byte> buffer)
    {
        bool completed;
        do
        {
            encoder.Convert(text, buffer, flush: true, out int chars, out int bytes, out completed);
            hash.AppendData(buffer[..bytes]);
            text = text[chars..];
        }
        while (!completed);
    }

    internal static void ValidateRootOpaqueWhiteout(TarEntry entry, string parent)
    {
        if (parent.Length == 0 && (GetFileType(entry) != ImageFileType.File || entry.Length != 0))
        {
            throw new InvalidDataException("A root opaque whiteout must be an empty regular file.");
        }
    }

    internal static ImageFileType GetFileType(TarEntry entry) =>
        entry.EntryType switch
        {
            TarEntryType.RegularFile or
                TarEntryType.V7RegularFile or
                TarEntryType.ContiguousFile => ImageFileType.File,
            TarEntryType.SymbolicLink => ImageFileType.SymbolicLink,
            TarEntryType.HardLink => ImageFileType.HardLink,
            TarEntryType.Directory => ImageFileType.Directory,
            _ => ImageFileType.Other
        };
}

internal sealed record LayerChanges(
    IReadOnlyList<ScannedEntry> Entries,
    IReadOnlyList<string> Whiteouts,
    IReadOnlyList<string> OpaqueDirectories)
{
    public IReadOnlyList<LayerDiagnostic> Diagnostics { get; init; } = [];
}

internal sealed record LayerDiagnostic(string Path, string Message);

internal sealed record ScannedEntry(
    string Path,
    ImageFileType Type,
    int Mode,
    int UserId,
    int GroupId,
    long Size,
    DateTime ModifiedTime,
    string? LinkTarget,
    int EntryIndex,
    long UncompressedOffset,
    long CompressedHighWaterMark,
    string? ContentHash)
{
    public WindowsFileMetadata? Windows { get; init; }

    public ImageFileSystemEntry ToEntry(
        ImageLayerReference introduced,
        ImageLayerReference? modified,
        ImageLayerReference layer) =>
        new()
        {
            Path = Path,
            Type = Type,
            Mode = Mode,
            UserId = UserId,
            GroupId = GroupId,
            Size = Size,
            ModifiedTime = ModifiedTime,
            LinkTarget = LinkTarget,
            Windows = Windows,
            IntroducedLayer = introduced,
            ModifiedLayer = modified,
            ContentLayerIndex = layer.Index,
            ContentPath = Type == ImageFileType.File ? Path : null,
            ContentEntryIndex = EntryIndex
        };
}
