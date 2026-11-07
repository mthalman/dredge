namespace Valleysoft.Dredge;

internal sealed class ImageFileSystemSemantics
{
    internal const string LinuxInterpretation = "linux-v2";
    internal const string LinuxIndexKind = "index-" + LinuxInterpretation;
    internal const string LinuxViewKind = "view-" + LinuxInterpretation;
    internal const string WindowsInterpretation = "windows-v13";
    internal const string WindowsIndexKind = "index-" + WindowsInterpretation;
    internal const string WindowsViewKind = "view-" + WindowsInterpretation;

    public static ImageFileSystemSemantics Linux { get; } = new(false);
    public static ImageFileSystemSemantics Windows { get; } = new(true);

    private ImageFileSystemSemantics(bool windows) => IsWindows = windows;

    public bool IsWindows { get; }
    public StringComparer Comparer => IsWindows ? WindowsImagePathComparer.Instance : StringComparer.Ordinal;
    public bool StartsWith(string value, string prefix) => IsWindows
        ? WindowsImagePathComparer.StartsWith(value, prefix)
        : value.StartsWith(prefix, StringComparison.Ordinal);
    public string IndexKind => IsWindows ? WindowsIndexKind : LinuxIndexKind;
    public string ViewKind => IsWindows ? WindowsViewKind : LinuxViewKind;

    public static ImageFileSystemSemantics ForOs(string? os) =>
        string.Equals(os, "windows", StringComparison.OrdinalIgnoreCase) ? Windows : Linux;

    public string NormalizeRequested(string? value) =>
        IsWindows ? NormalizeWindows(value ?? "", allowParents: false) : ImagePath.NormalizeRequested(value);

    public string NormalizeArchivePath(string value) =>
        IsWindows ? NormalizeWindowsSegments(value.Replace('\\', '/'), allowParents: false)
            : ImagePath.NormalizeArchive(value);

    public string NormalizeHardLinkTarget(string target, string linkPath) =>
        IsWindows ? NormalizeArchivePath(target) : ResolveLinkTarget("", target, "", linkPath);

    public static void ValidateHardLinkTarget(string path, string targetPath, ImageFileType? targetType)
    {
        if (targetType is null or ImageFileType.Directory)
        {
            throw new InvalidDataException($"Hard link '/{path}' targets missing or invalid path '/{targetPath}'.");
        }
    }

    public string[] Descendants(SortedSet<string> paths, string path)
    {
        if (path.Length == 0)
        {
            return [.. paths];
        }
        string prefix = path + "/";
        // Bounding the separator includes suffixes beginning with U+FFFF.
        return [.. paths.GetViewBetween(prefix, path + "0").Where(candidate => StartsWith(candidate, prefix))];
    }

    public void ValidateParent(string path, string parent, ImageFileType? type)
    {
        if (IsWindows && type == ImageFileType.Other)
        {
            throw new NotSupportedException($"Cannot apply '{path}' through unsupported Windows parent '{parent}'.");
        }
    }

    public static WindowsFileMetadata UnsupportedHardLink(string target, WindowsFileMetadata? metadata) =>
        (metadata ?? new(null, false, false, "", null)) with
        {
            UnsupportedReason = $"Hard link targets unsupported Windows path '/{target}'."
        };

    public string? MapArchivePath(string value)
    {
        if (!IsWindows)
        {
            return ImagePath.NormalizeArchive(value);
        }
        string path = ImagePath.NormalizeArchive(value.Replace('\\', '/'));
        if (Comparer.Equals(path, "Files"))
        {
            return "";
        }
        if (StartsWith(path, "Files/"))
        {
            string mapped = path[6..];
            ReadOnlySpan<char> canonical = mapped;
            foreach (Range segment in canonical.Split('/'))
            {
                ValidateWindowsSegmentEnding(canonical[segment], mapped);
            }
            return mapped;
        }
        if (Comparer.Equals(path, "UtilityVM") || StartsWith(path, "UtilityVM/") ||
            Comparer.Equals(path, "Hives") || StartsWith(path, "Hives/"))
        {
            return null;
        }
        throw new NotSupportedException($"Unsupported Windows layer layout at '{value}'; expected a Files/ filesystem root.");
    }

    public bool IsAbsolute(string value) =>
        IsWindows ? value.StartsWith('/') || value.StartsWith('\\') ||
            value.Length >= 2 && value[1] == ':' : ImagePath.IsAbsolute(value);

    public string ResolveLinkTarget(string basePath, string target, string remainder, string linkPath)
    {
        if (!IsWindows)
        {
            return ImagePath.ResolveLinkTarget(basePath, target, remainder, linkPath);
        }
        string normalizedTarget = NormalizeWindowsRoot(target);
        string combined = IsAbsolute(target) ? normalizedTarget : ImagePath.Join(basePath, normalizedTarget);
        return NormalizeWindowsSegments(ImagePath.Join(combined, remainder.Replace('\\', '/')), allowParents: true);
    }

    public void ValidateLayerMediaType(string? mediaType)
    {
        if (IsWindows && mediaType is not (
            "application/vnd.docker.image.rootfs.diff.tar.gzip" or
            "application/vnd.docker.image.rootfs.foreign.diff.tar.gzip" or
            "application/vnd.oci.image.layer.v1.tar+gzip" or
            "application/vnd.oci.image.layer.nondistributable.v1.tar+gzip"))
        {
            throw new NotSupportedException($"Unsupported Windows layer media type '{mediaType}'; only gzip-compressed tar layers are supported.");
        }
    }

    private static string NormalizeWindows(string value, bool allowParents) =>
        NormalizeWindowsSegments(NormalizeWindowsRoot(value), allowParents);

    private static string NormalizeWindowsRoot(string value)
    {
        value = value.Replace('\\', '/');
        int rootEnd = value.Length > 0 ? value.IndexOf('/', 1) : -1;
        ReadOnlySpan<char> root = value.AsSpan(0, rootEnd < 0 ? value.Length : rootEnd);
        if (ImagePath.ContainsControlCharacter(value) || value.StartsWith("//", StringComparison.Ordinal) ||
            root.Equals("/??", StringComparison.OrdinalIgnoreCase) ||
            root.Equals("/Device", StringComparison.OrdinalIgnoreCase) ||
            root.Equals("/DosDevices", StringComparison.OrdinalIgnoreCase) ||
            root.Equals("/GLOBAL??", StringComparison.OrdinalIgnoreCase) ||
            root.Equals("/GLOBALROOT", StringComparison.OrdinalIgnoreCase) ||
            root.Equals("/SystemRoot", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"Unsupported Windows image path '{value}'. UNC and device paths are not supported.");
        }
        if (value.Length >= 2 && value[1] == ':')
        {
            if (char.ToUpperInvariant(value[0]) != 'C' || value.Length < 3 || value[2] != '/')
            {
                throw new InvalidDataException($"Unsupported Windows image path '{value}'; only absolute C-drive paths are supported.");
            }
            value = value[3..];
        }
        return value;
    }

    private static string NormalizeWindowsSegments(string value, bool allowParents)
    {
        List<string> parts = [];
        foreach (string part in value.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (part == ".")
            {
                continue;
            }
            if (part == "..")
            {
                if (!allowParents || parts.Count == 0)
                {
                    throw new InvalidDataException($"Windows image path '{value}' escapes the image root.");
                }
                parts.RemoveAt(parts.Count - 1);
                continue;
            }
            ImagePath.ValidateSegment(part, "Windows image path");
            ValidateWindowsSegmentEnding(part, value);
            parts.Add(part);
        }
        return string.Join('/', parts);
    }

    private static void ValidateWindowsSegmentEnding(ReadOnlySpan<char> segment, string path)
    {
        if (segment.Length > 0 && segment[^1] is '.' or ' ')
        {
            throw new NotSupportedException($"Windows image path '{path}' has unsupported trailing-dot or trailing-space semantics.");
        }
    }
}
