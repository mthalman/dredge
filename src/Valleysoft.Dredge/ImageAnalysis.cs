namespace Valleysoft.Dredge;

internal enum LayerChangeKind { Added, Modified, Identical, Deleted }

// Entry is the entry this layer wrote, or for a deletion the entry it hid.
internal sealed record LayerFileChange(
    string Path, int Layer, LayerChangeKind Kind, ImageFileType Type, long Size,
    ScannedEntry? Entry = null)
{
    public ImageContentId? ContentId { get; init; }
}

internal sealed record ImageLayerAnalysis(
    int Index, long FileBytes, long HiddenBytes, IReadOnlyList<LayerFileChange> Changes);

// Attribution stays with the shipped content even when a hard link is its last surviving name.
internal sealed record HiddenFile(
    string Path, int Layer, int HiddenBy, LayerChangeKind Reason, long Size,
    string? ReplacedByHardLink = null);

internal sealed record ImageAnalysisResult(
    long FileBytes, long HiddenBytes, IReadOnlyList<ImageLayerAnalysis> Layers)
{
    public double Efficiency => FileBytes == 0 ? 1 : 1 - (double)HiddenBytes / FileBytes;
    public IReadOnlyDictionary<string, ScannedEntry> LiveEntries { get; init; } =
        new Dictionary<string, ScannedEntry>(StringComparer.Ordinal);
    public IReadOnlyDictionary<string, ScannedEntry> LiveContents { get; init; } =
        new Dictionary<string, ScannedEntry>(StringComparer.Ordinal);
    public IReadOnlyDictionary<string, int> LiveLayers { get; init; } =
        new Dictionary<string, int>(StringComparer.Ordinal);
    public IReadOnlyList<HiddenFile> HiddenFiles { get; init; } = [];

    public IReadOnlyList<ImagePotentialSaving> FindPotentialSavings() =>
        LiveEntries.Values
            .Where(entry => entry.Type == ImageFileType.File && entry.Size > 0)
            .Select(entry => (Entry: entry, Kind: PotentialKind(entry.Path)))
            .Where(item => item.Kind is not null)
            .GroupBy(item => item.Kind!.Value)
            .Select(group => new ImagePotentialSaving(group.Key,
                group.Sum(item => item.Entry.Size),
                group.Select(item => item.Entry.Path).Order(StringComparer.Ordinal).ToArray()))
            .OrderByDescending(item => item.Bytes).ToArray();

    internal static PotentialSavingKind? PotentialKind(string path)
    {
        string rooted = "/" + path;
        if (rooted.Contains("/.npm/_cacache/", StringComparison.Ordinal))
        {
            return PotentialSavingKind.NpmCache;
        }
        if (rooted.StartsWith("/var/lib/apt/lists/", StringComparison.Ordinal))
        {
            return PotentialSavingKind.AptLists;
        }
        if (rooted.StartsWith("/var/cache/apt/", StringComparison.Ordinal))
        {
            return PotentialSavingKind.AptCache;
        }
        if (rooted.StartsWith("/var/cache/apk/", StringComparison.Ordinal))
        {
            return PotentialSavingKind.ApkCache;
        }
        if (rooted.Contains("/.cache/pip/", StringComparison.Ordinal))
        {
            return PotentialSavingKind.PipCache;
        }
        if (rooted.Contains("/.cache/yarn/", StringComparison.Ordinal))
        {
            return PotentialSavingKind.YarnCache;
        }
        if (rooted.Contains("/.git/", StringComparison.Ordinal))
        {
            return PotentialSavingKind.GitMetadata;
        }
        if (rooted.Contains("-darwin-", StringComparison.OrdinalIgnoreCase) ||
            rooted.Contains("-win32-", StringComparison.OrdinalIgnoreCase))
        {
            return PotentialSavingKind.OtherOsNative;
        }
        return null;
    }
}

internal enum PotentialSavingKind
{
    NpmCache,
    AptLists,
    AptCache,
    ApkCache,
    PipCache,
    YarnCache,
    GitMetadata,
    OtherOsNative
}

internal sealed record ImagePotentialSaving(PotentialSavingKind Kind, long Bytes, IReadOnlyList<string> Paths);

internal static class ImageAnalysis
{
    private sealed record LiveEntry(ScannedEntry Entry, int Layer, FileContent? Content);

    private sealed class FileContent(ScannedEntry entry, int layer)
    {
        public ScannedEntry Entry { get; } = entry;
        public int Layer { get; } = layer;
        public ImageContentId Id { get; } = new(layer, entry.Path, entry.EntryIndex);
        public int References { get; set; }
    }

    public static ImageAnalysisResult Analyze(IReadOnlyList<LayerChanges> layers)
    {
        Dictionary<string, LiveEntry> live = new(StringComparer.Ordinal);
        SortedSet<string> paths = new(StringComparer.Ordinal);
        long[] hidden = new long[layers.Count];
        List<ImageLayerAnalysis> analyses = [];
        List<HiddenFile> hiddenFiles = [];
        long total = 0;

        for (int index = 0; index < layers.Count; index++)
        {
            LayerChanges layer = layers[index];
            List<LayerFileChange> changes = [];
            long layerBytes = 0;

            void Remove(string path, LiveEntry old)
            {
                Charge(old, LayerChangeKind.Deleted);
                changes.Add(new(path, index, LayerChangeKind.Deleted, old.Entry.Type,
                    old.Content?.Entry.Size ?? old.Entry.Size, old.Entry) { ContentId = old.Content?.Id });
                live.Remove(path);
                paths.Remove(path);
            }

            void Delete(string path, bool includePath)
            {
                string prefix = path + "/";
                string[] descendants = paths.GetViewBetween(prefix, prefix + '\uffff')
                    .Where(key => key.StartsWith(prefix, StringComparison.Ordinal)).ToArray();
                foreach (string existing in includePath && live.ContainsKey(path)
                    ? descendants.Prepend(path) : descendants)
                {
                    Remove(existing, live[existing]);
                }
            }

            void Charge(LiveEntry old, LayerChangeKind reason, string? replacedByHardLink = null)
            {
                if (old.Content is FileContent content && --content.References == 0)
                {
                    hidden[content.Layer] += content.Entry.Size;
                    hiddenFiles.Add(new(content.Entry.Path, content.Layer, index, reason, content.Entry.Size,
                        replacedByHardLink));
                }
            }

            foreach (string path in layer.OpaqueDirectories)
            {
                Delete(path, includePath: false);
            }
            foreach (string path in layer.Whiteouts)
            {
                Delete(path, includePath: true);
            }

            foreach (ScannedEntry entry in layer.Entries)
            {
                if (entry.Type != ImageFileType.Directory)
                {
                    Delete(entry.Path, includePath: false);
                }
                string parent = ImagePath.GetDirectoryName(entry.Path);
                while (parent.Length > 0)
                {
                    if (live.TryGetValue(parent, out LiveEntry? parentEntry) &&
                        parentEntry.Entry.Type != ImageFileType.Directory)
                    {
                        Remove(parent, parentEntry);
                    }
                    parent = ImagePath.GetDirectoryName(parent);
                }

                LayerChangeKind kind = LayerChangeKind.Added;
                FileContent? content = entry.Type == ImageFileType.File
                    ? new(entry, index)
                    : entry.Type == ImageFileType.HardLink ? GetHardLinkContent(entry, live) : null;
                if (content is not null)
                {
                    content.References++;
                }
                if (live.TryGetValue(entry.Path, out LiveEntry? previous))
                {
                    kind = Same(previous.Entry, entry) &&
                        (entry.Type != ImageFileType.HardLink || SameContent(previous.Content, content))
                        ? LayerChangeKind.Identical : LayerChangeKind.Modified;
                    Charge(previous, entry.Type == ImageFileType.HardLink ? LayerChangeKind.Modified : kind,
                        entry.Type == ImageFileType.HardLink ? entry.Path : null);
                }
                live[entry.Path] = new(entry, index, content);
                paths.Add(entry.Path);
                changes.Add(new(entry.Path, index, kind, entry.Type, content?.Entry.Size ?? entry.Size, entry)
                    { ContentId = content?.Id });
                if (entry.Type == ImageFileType.File)
                {
                    layerBytes += entry.Size;
                }

            }

            total += layerBytes;
            analyses.Add(new(index, layerBytes, 0, changes));
        }

        return new(total, hidden.Sum(), analyses
            .Select(layer => layer with { HiddenBytes = hidden[layer.Index] }).ToArray())
        {
            LiveEntries = live.ToDictionary(pair => pair.Key, pair => pair.Value.Entry,
                StringComparer.Ordinal),
            LiveContents = live.Where(pair => pair.Value.Content is not null)
                .ToDictionary(pair => pair.Key, pair => pair.Value.Content!.Entry, StringComparer.Ordinal),
            LiveLayers = live.ToDictionary(pair => pair.Key, pair => pair.Value.Layer,
                StringComparer.Ordinal),
            HiddenFiles = hiddenFiles
        };
    }
    private static FileContent? GetHardLinkContent(ScannedEntry entry, IReadOnlyDictionary<string, LiveEntry> live)
    {
        string path = ImagePath.ResolveLinkTarget("", entry.LinkTarget ??
            throw new InvalidDataException($"Hard link '/{entry.Path}' has no target."), "", entry.Path);
        for (int hop = 0; hop < 40; hop++)
        {
            string[] parts = path.Split('/');
            bool followed = false;
            for (int i = 0; i < parts.Length - 1; i++)
            {
                string parent = string.Join('/', parts.Take(i + 1));
                if (live.TryGetValue(parent, out LiveEntry? link) && link.Entry.Type == ImageFileType.SymbolicLink)
                {
                    string target = link.Entry.LinkTarget ??
                        throw new InvalidDataException($"Link '/{parent}' has no target.");
                    path = ImagePath.ResolveLinkTarget(
                        ImagePath.IsAbsolute(target) ? "" : ImagePath.GetDirectoryName(parent),
                        target, string.Join('/', parts.Skip(i + 1)), parent);
                    followed = true;
                    break;
                }
            }
            if (!followed)
            {
                return live.TryGetValue(path, out LiveEntry? target)
                    ? target.Content
                    : throw new InvalidDataException($"Hard link '/{entry.Path}' targets missing path '/{path}'.");
            }
        }
        throw new InvalidDataException($"Link resolution for '/{entry.Path}' exceeded 40 hops.");
    }

    private static bool SameContent(FileContent? a, FileContent? b) =>
        ReferenceEquals(a, b) || (a is not null && b is not null && Same(a.Entry, b.Entry));

    private static bool Same(ScannedEntry a, ScannedEntry b) =>
        a.Type == b.Type &&
        a.Mode == b.Mode &&
        a.UserId == b.UserId &&
        a.GroupId == b.GroupId &&
        a.Size == b.Size &&
        a.ContentHash == b.ContentHash &&
        a.LinkTarget == b.LinkTarget;
}
