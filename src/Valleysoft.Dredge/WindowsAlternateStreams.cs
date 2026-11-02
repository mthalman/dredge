namespace Valleysoft.Dredge;

internal sealed class WindowsAlternateStreams
{
    private readonly Dictionary<ImageContentId, HashSet<string>> streams = [];
    private readonly Dictionary<string, ImageContentId> owners = new(WindowsImagePathComparer.Instance);
    private readonly Dictionary<string, HashSet<string>> pathsByOwner = new(WindowsImagePathComparer.Instance);
    private readonly Dictionary<ImageContentId, Dictionary<string, HashSet<string>>> pathsByContent = [];
    private readonly Dictionary<string, Dictionary<string, HashSet<string>>> pendingPathsByOwner = new(WindowsImagePathComparer.Instance);
    private readonly HashSet<ImageContentId> presenceChanges = [];

    public bool HasStreams(ImageContentId content) => streams.Count > 0 && streams.ContainsKey(content);

    public static string BasePath(string path) => path[..path.IndexOf(':')];

    private static string StreamName(string path)
    {
        string name = path[(path.IndexOf(':') + 1)..];
        return name.EndsWith(":$DATA", StringComparison.OrdinalIgnoreCase) ? name[..^6] : name;
    }

    public void Track(string path, ImageContentId? owner)
    {
        Forget(path);
        string basePath = BasePath(path);
        if (!pathsByOwner.TryGetValue(basePath, out HashSet<string>? paths))
        {
            paths = new(WindowsImagePathComparer.Instance);
            pathsByOwner.Add(basePath, paths);
        }
        paths.Add(path);
        if (owner is ImageContentId content)
        {
            Bind(path, content);
        }
        else
        {
            TrackPath(pendingPathsByOwner, basePath, StreamName(path), path);
        }
    }

    public void BindPending(string basePath, ImageContentId? owner)
    {
        if (owner is not ImageContentId content ||
            !pendingPathsByOwner.TryGetValue(basePath, out Dictionary<string, HashSet<string>>? names)) return;
        foreach (HashSet<string> paths in names.Values)
        {
            foreach (string path in paths)
            {
                Bind(path, content);
            }
        }
        pendingPathsByOwner.Remove(basePath);
    }

    private void Bind(string path, ImageContentId owner)
    {
        if (!streams.TryGetValue(owner, out HashSet<string>? names))
        {
            names = new(WindowsImagePathComparer.Instance);
            streams.Add(owner, names);
            presenceChanges.Add(owner);
        }
        owners[path] = owner;
        string name = StreamName(path);
        TrackPath(pathsByContent, owner, name, path);
        names.Add(name);
    }

    public string[] PathsForOwner(string path) =>
        pathsByOwner.TryGetValue(path, out HashSet<string>? paths) ? [.. paths] : [];

    public string[] DeleteStream(string path, ImageContentId? owner)
    {
        string name = StreamName(path);
        if (owner is ImageContentId content && streams.TryGetValue(content, out HashSet<string>? names))
        {
            names.Remove(name);
            if (names.Count == 0)
            {
                streams.Remove(content);
                presenceChanges.Add(content);
            }
        }
        HashSet<string>? boundPaths = owner is ImageContentId id ? FindPaths(pathsByContent, id, name) : null;
        HashSet<string>? pendingPaths = FindPaths(pendingPathsByOwner, BasePath(path), name);
        return [.. boundPaths ?? Enumerable.Empty<string>(), .. pendingPaths ?? Enumerable.Empty<string>()];
    }

    public ImageContentId[] TakePresenceChanges()
    {
        ImageContentId[] changed = [.. presenceChanges];
        presenceChanges.Clear();
        return changed;
    }

    public void ForgetContent(ImageContentId content)
    {
        streams.Remove(content);
        presenceChanges.Remove(content);
    }

    public void Forget(string path)
    {
        // A removed pathname can leave its content and streams alive through another hard link.
        if (!path.Contains(':')) return;
        string basePath = BasePath(path);
        if (!pathsByOwner.TryGetValue(basePath, out HashSet<string>? paths) || !paths.Remove(path)) return;
        if (paths.Count == 0) pathsByOwner.Remove(basePath);
        bool bound = owners.Remove(path, out ImageContentId owner);
        string name = StreamName(path);
        if (bound) ForgetPath(pathsByContent, owner, name, path);
        else ForgetPath(pendingPathsByOwner, basePath, name, path);
    }

    private static HashSet<string>? FindPaths<TKey>(
        Dictionary<TKey, Dictionary<string, HashSet<string>>> index, TKey owner, string name) where TKey : notnull =>
        index.TryGetValue(owner, out Dictionary<string, HashSet<string>>? names) &&
            names.TryGetValue(name, out HashSet<string>? paths) ? paths : null;

    private static void TrackPath<TKey>(
        Dictionary<TKey, Dictionary<string, HashSet<string>>> index, TKey owner, string name, string path) where TKey : notnull
    {
        if (!index.TryGetValue(owner, out Dictionary<string, HashSet<string>>? names))
        {
            names = new(WindowsImagePathComparer.Instance);
            index.Add(owner, names);
        }
        if (!names.TryGetValue(name, out HashSet<string>? paths))
        {
            paths = new(WindowsImagePathComparer.Instance);
            names.Add(name, paths);
        }
        paths.Add(path);
    }

    private static void ForgetPath<TKey>(
        Dictionary<TKey, Dictionary<string, HashSet<string>>> index, TKey owner, string name, string path) where TKey : notnull
    {
        Dictionary<string, HashSet<string>> names = index[owner];
        HashSet<string> paths = names[name];
        paths.Remove(path);
        if (paths.Count == 0) names.Remove(name);
        if (names.Count == 0) index.Remove(owner);
    }
}
