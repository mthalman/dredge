using System.Formats.Tar;
using System.IO.Compression;

namespace Valleysoft.Dredge;

internal static class ImageLayerScanner
{
    private const string WhiteoutPrefix = ".wh.";
    private const string OpaqueWhiteout = ".wh..wh..opq";

    public static async Task<LayerChanges> ScanAsync(
        Stream blob,
        ImageLayerReference layer,
        CancellationToken cancellationToken)
    {
        List<ScannedEntry> entries = [];
        List<string> whiteouts = [];
        List<string> opaqueDirectories = [];

        using GZipStream gzip = new(blob, CompressionMode.Decompress, leaveOpen: true);
        using TarReader tar = new(gzip, leaveOpen: true);
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
            await ImageTarReader.DrainEntryAsync(tarEntry, layer, cancellationToken);

            string path = ImagePath.NormalizeArchive(tarEntry.Name);
            if (path.Length == 0)
            {
                continue;
            }

            string fileName = ImagePath.GetFileName(path);
            string parent = ImagePath.GetDirectoryName(path);
            if (string.Equals(fileName, OpaqueWhiteout, StringComparison.Ordinal))
            {
                if (parent.Length == 0)
                {
                    throw new InvalidDataException(
                        "The opaque whiteout marker cannot exist at the image root.");
                }
                opaqueDirectories.Add(parent);
                continue;
            }

            if (fileName.StartsWith(WhiteoutPrefix, StringComparison.Ordinal))
            {
                string targetName = fileName[WhiteoutPrefix.Length..];
                ImagePath.ValidateSegment(targetName, "whiteout target");
                whiteouts.Add(
                    parent.Length == 0 ? targetName : $"{parent}/{targetName}");
                continue;
            }

            ImageFileType type = GetFileType(tarEntry);
            string? linkTarget = type is ImageFileType.SymbolicLink or ImageFileType.HardLink
                ? tarEntry.LinkName
                : null;
            if (linkTarget is not null && ImagePath.ContainsControlCharacter(linkTarget))
            {
                throw new InvalidDataException(
                    $"Link '/{path}' has an invalid target.");
            }
            if (type == ImageFileType.SymbolicLink)
            {
                string basePath = ImagePath.IsAbsolute(linkTarget!)
                    ? string.Empty
                    : parent;
                _ = ImagePath.ResolveLinkTarget(
                    basePath,
                    linkTarget!,
                    string.Empty,
                    path);
            }

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
                layer));
        }
        return new(entries, whiteouts, opaqueDirectories);
    }

    private static ImageFileType GetFileType(TarEntry entry) =>
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
    IReadOnlyList<string> OpaqueDirectories);

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
    ImageLayerReference Layer)
{
    public ImageFileSystemEntry ToEntry(
        ImageLayerReference introduced,
        ImageLayerReference? modified) =>
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
            IntroducedLayer = introduced,
            ModifiedLayer = modified,
            ContentLayerIndex = Layer.Index,
            ContentPath = Type == ImageFileType.File ? Path : null,
            ContentEntryIndex = EntryIndex
        };
}
