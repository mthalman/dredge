namespace Valleysoft.Dredge;

internal sealed class ImagePathResolver
{
    private const int MaximumLinkHops = 40;

    private readonly IReadOnlyDictionary<string, ImageFileSystemEntry> entries;

    public ImagePathResolver(IReadOnlyDictionary<string, ImageFileSystemEntry> entries)
    {
        this.entries = entries;
    }

    public ImageFileSystemEntry ResolveContentEntry(string requestedPath, IReadOnlyDictionary<string, ImageFileSystemEntry> entries)
    {
        string path = ImagePath.NormalizeRequested(requestedPath);
        if (path.Length == 0)
        {
            throw new InvalidDataException("The image root is a directory.");
        }

        ImageFileSystemEntry entry = ResolvePath(path, entries);
        if (entry.Type == ImageFileType.Directory)
        {
            throw new InvalidDataException($"Path '/{path}' is a directory.");
        }
        if (entry.Type == ImageFileType.HardLink &&
            entry.ContentLinkTarget is string linkTarget)
        {
            string basePath = ImagePath.IsAbsolute(linkTarget)
                ? string.Empty
                : ImagePath.GetDirectoryName(entry.Path);
            string targetPath = ImagePath.ResolveLinkTarget(
                basePath,
                linkTarget,
                string.Empty,
                entry.Path);
            entry = ResolvePath(targetPath, entries);
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
        string current = ImagePath.NormalizeRequested(requestedPath);
        for (int hop = 0; hop < MaximumLinkHops; hop++)
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

                string target = entry.LinkTarget ??
                    throw new InvalidDataException(
                        $"Link '/{candidate}' has no target.");
                string basePath = !ImagePath.IsAbsolute(target)
                    ? ImagePath.GetDirectoryName(candidate)
                    : string.Empty;
                string remainder = string.Join('/', segments.Skip(i + 1));
                current = ImagePath.ResolveLinkTarget(basePath, target, remainder, candidate);
                followedLink = true;
                break;
            }

            if (!followedLink)
            {
                if (!entries.TryGetValue(current, out ImageFileSystemEntry? result))
                {
                    throw new FileNotFoundException(
                        $"Path '/{requestedPath}' does not exist in the image.");
                }
                return result;
            }
        }

        throw new InvalidDataException(
            $"Link resolution for '/{requestedPath}' exceeded {MaximumLinkHops} hops.");
    }

    public string ResolveParentComponents(string path, IReadOnlyDictionary<string, ImageFileSystemEntry> entries)
    {
        string parentPath = ImagePath.GetDirectoryName(path);
        if (parentPath.Length == 0)
        {
            return path;
        }

        ImageFileSystemEntry parent = ResolvePath(parentPath, entries);
        if (parent.Type != ImageFileType.Directory)
        {
            throw new InvalidDataException(
                $"Path '/{path}' has a non-directory parent '/{parent.Path}'.");
        }
        return $"{parent.Path}/{ImagePath.GetFileName(path)}";
    }

    public ImageFileSystemEntry? TryResolvePath(string requestedPath, IReadOnlyDictionary<string, ImageFileSystemEntry> entries)
    {
        try
        {
            return ResolvePath(requestedPath, entries);
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

    public string GetHardLinkTargetPath(ImageFileSystemEntry entry, IReadOnlyDictionary<string, ImageFileSystemEntry> entries)
    {
        string target = entry.LinkTarget ??
            throw new InvalidDataException($"Hard link '/{entry.Path}' has no target.");
        string targetPath = ImagePath.ResolveLinkTarget(
            string.Empty,
            target,
            string.Empty,
            entry.Path);
        string parentPath = ImagePath.GetDirectoryName(targetPath);
        if (parentPath.Length == 0)
        {
            return targetPath;
        }

        ImageFileSystemEntry parent = ResolvePath(parentPath, entries);
        if (parent.Type != ImageFileType.Directory)
        {
            throw new InvalidDataException(
                $"Hard link '/{entry.Path}' targets path '/{targetPath}' with a non-directory parent.");
        }
        return $"{parent.Path}/{ImagePath.GetFileName(targetPath)}";
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

    public string ResolveParentComponents(string path)
    {
        string parentPath = ImagePath.GetDirectoryName(path);
        if (parentPath.Length == 0)
        {
            return path;
        }

        ImageFileSystemEntry parent = ResolvePath(parentPath, entries);
        if (parent.Type != ImageFileType.Directory)
        {
            throw new InvalidDataException(
                $"Path '/{path}' has a non-directory parent '/{parent.Path}'.");
        }
        return $"{parent.Path}/{ImagePath.GetFileName(path)}";
    }
}
