namespace Valleysoft.Dredge;

internal sealed class ImagePathResolver
{
    private const int MaximumLinkHops = 40;

    private readonly IReadOnlyDictionary<string, ImageFileSystemEntry> entries;
    private readonly ImageFileSystemSemantics semantics;

    public ImagePathResolver(IReadOnlyDictionary<string, ImageFileSystemEntry> entries,
        ImageFileSystemSemantics? semantics = null)
    {
        this.entries = entries;
        this.semantics = semantics ?? ImageFileSystemSemantics.Linux;
    }

    public ImageFileSystemEntry ResolveContentEntry(string requestedPath, IReadOnlyDictionary<string, ImageFileSystemEntry> entries)
    {
        string path = semantics.NormalizeRequested(requestedPath);
        if (path.Length == 0)
        {
            throw new InvalidDataException("The image root is a directory.");
        }

        int hops = 0;
        ImageFileSystemEntry entry = ResolvePath(path, entries, ref hops);
        while (entry.Type == ImageFileType.HardLink &&
            entry.ContentLinkTarget is string linkTarget)
        {
            CountLinkHop(requestedPath, ref hops);
            string basePath = semantics.IsAbsolute(linkTarget)
                ? string.Empty
                : ImagePath.GetDirectoryName(entry.Path);
            string targetPath = semantics.ResolveLinkTarget(
                basePath, linkTarget, string.Empty, entry.Path);
            entry = ResolvePath(targetPath, entries, ref hops);
        }
        if (entry.Type == ImageFileType.Directory)
        {
            throw new InvalidDataException($"Path '/{path}' is a directory.");
        }
        if (entry.Type == ImageFileType.HardLink)
        {
            return entry with { Type = ImageFileType.File };
        }
        if (entry.Type != ImageFileType.File)
        {
            throw new NotSupportedException(
                $"Path '/{path}' has unsupported file type '{entry.Type}'.");
        }
        return entry;
    }

    public ImageFileSystemEntry ResolvePath(string requestedPath, IReadOnlyDictionary<string, ImageFileSystemEntry> entries)
    {
        int hops = 0;
        return ResolvePath(requestedPath, entries, ref hops);
    }

    private ImageFileSystemEntry ResolvePath(string requestedPath, IReadOnlyDictionary<string, ImageFileSystemEntry> entries,
        ref int hops)
    {
        string path = ResolvePathName(requestedPath, entries, ref hops);
        if (path.Length == 0)
        {
            throw new InvalidDataException("The image root is a directory.");
        }
        return entries[path];
    }

    private string ResolvePathName(string requestedPath, IReadOnlyDictionary<string, ImageFileSystemEntry> entries)
    {
        int hops = 0;
        return ResolvePathName(requestedPath, entries, ref hops);
    }

    private string ResolvePathName(string requestedPath, IReadOnlyDictionary<string, ImageFileSystemEntry> entries,
        ref int hops)
    {
        string current = semantics.NormalizeRequested(requestedPath);
        while (true)
        {
            string[] segments = current.Split('/', StringSplitOptions.RemoveEmptyEntries);
            bool followedLink = false;
            for (int i = 0; i < segments.Length; i++)
            {
                string candidate = string.Join('/', segments.Take(i + 1));
                if (!entries.TryGetValue(candidate, out ImageFileSystemEntry? entry))
                {
                    throw new FileNotFoundException(
                        $"Path '/{requestedPath}' resolves to missing path '/{candidate}'.");
                }

                if (entry.Type != ImageFileType.SymbolicLink)
                {
                    continue;
                }

                CountLinkHop(requestedPath, ref hops);
                string target = entry.LinkTarget ??
                    throw new InvalidDataException(
                        $"Link '/{candidate}' has no target.");
                string basePath = !semantics.IsAbsolute(target)
                    ? ImagePath.GetDirectoryName(candidate)
                    : string.Empty;
                string remainder = string.Join('/', segments.Skip(i + 1));
                current = semantics.ResolveLinkTarget(basePath, target, remainder, candidate);
                followedLink = true;
                break;
            }

            if (!followedLink)
            {
                if (current.Length == 0)
                {
                    return current;
                }
                if (!entries.TryGetValue(current, out ImageFileSystemEntry? result))
                {
                    throw new FileNotFoundException(
                        $"Path '/{requestedPath}' does not exist in the image.");
                }
                return result.Path;
            }
        }

    }

    private static void CountLinkHop(string requestedPath, ref int hops)
    {
        if (++hops >= MaximumLinkHops)
        {
            throw new InvalidDataException(
                $"Link resolution for '/{requestedPath}' exceeded {MaximumLinkHops} hops.");
        }
    }

    public string ResolveParentComponents(string path, IReadOnlyDictionary<string, ImageFileSystemEntry> entries)
    {
        string parentPath = ImagePath.GetDirectoryName(path);
        if (parentPath.Length == 0)
        {
            return path;
        }

        string resolvedParent = ResolvePathName(parentPath, entries);
        if (resolvedParent.Length > 0 && entries[resolvedParent].Type != ImageFileType.Directory)
        {
            throw new InvalidDataException(
                $"Path '/{path}' has a non-directory parent '/{resolvedParent}'.");
        }
        return ImagePath.Join(resolvedParent, ImagePath.GetFileName(path));
    }

    public bool ResolvesToDirectory(string requestedPath, IReadOnlyDictionary<string, ImageFileSystemEntry> entries)
    {
        try
        {
            string path = ResolvePathName(requestedPath, entries);
            return path.Length == 0 || entries[path].Type == ImageFileType.Directory;
        }
        catch (FileNotFoundException)
        {
            return false;
        }
        catch (InvalidDataException)
        {
            return false;
        }
    }

    public string GetHardLinkTargetPath(ImageFileSystemEntry entry, IReadOnlyDictionary<string, ImageFileSystemEntry> entries)
    {
        string target = entry.LinkTarget ??
            throw new InvalidDataException($"Hard link '/{entry.Path}' has no target.");
        string targetPath = semantics.NormalizeHardLinkTarget(target, entry.Path);
        return ResolveParentComponents(targetPath, entries);
    }

    public string? TryGetHardLinkTargetPath(ImageFileSystemEntry entry, IReadOnlyDictionary<string, ImageFileSystemEntry> entries)
    {
        try
        {
            return GetHardLinkTargetPath(entry, entries);
        }
        catch (FileNotFoundException)
        {
            return null;
        }
        catch (InvalidDataException)
        {
            return null;
        }
    }

    public ImageFileSystemEntry GetExtractionSource(string sourcePath, IReadOnlyDictionary<string, ImageFileSystemEntry> entries)
    {
        string lookupPath = ResolveParentComponents(sourcePath, entries);
        if (!entries.TryGetValue(lookupPath, out ImageFileSystemEntry? source))
        {
            throw new FileNotFoundException(
                $"Path '/{sourcePath}' does not exist in the image.");
        }
        if (source.Type == ImageFileType.Other)
        {
            throw new NotSupportedException(
                $"Path '/{source.Path}' has unsupported file type '{source.Type}'.");
        }
        return source;
    }

    public string ResolveParentComponents(string path) => ResolveParentComponents(path, entries);
}
