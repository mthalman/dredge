namespace Valleysoft.Dredge;

internal enum LayerChangeKind { Added, Modified, Identical, Deleted }

// Entry is the entry this layer wrote, or for a deletion the entry it hid.
internal sealed record LayerFileChange(
    string Path, int Layer, LayerChangeKind Kind, ImageFileType Type, long Size,
    ScannedEntry? Entry = null);

internal sealed record ImageLayerAnalysis(
    int Index, long FileBytes, long HiddenBytes, IReadOnlyList<LayerFileChange> Changes);

// A regular file shipped by Layer whose bytes are hidden because HiddenBy
// replaced (Modified or Identical) or deleted it.
internal sealed record HiddenFile(
    string Path, int Layer, int HiddenBy, LayerChangeKind Reason, long Size);

internal sealed record ImageAnalysisResult(
    long FileBytes, long HiddenBytes, IReadOnlyList<ImageLayerAnalysis> Layers)
{
    public double Efficiency => FileBytes == 0 ? 1 : 1 - (double)HiddenBytes / FileBytes;
    public IReadOnlyDictionary<string, ScannedEntry> LiveEntries { get; init; } =
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
    private sealed record LiveEntry(ScannedEntry Entry, int Layer);

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
                Charge(path, old, LayerChangeKind.Deleted);
                changes.Add(new(path, index, LayerChangeKind.Deleted, old.Entry.Type, old.Entry.Size, old.Entry));
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

            void Charge(string path, LiveEntry old, LayerChangeKind reason)
            {
                if (old.Entry.Type == ImageFileType.File)
                {
                    hidden[old.Layer] += old.Entry.Size;
                    hiddenFiles.Add(new(path, old.Layer, index, reason, old.Entry.Size));
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
                if (live.TryGetValue(entry.Path, out LiveEntry? previous))
                {
                    kind = Same(previous.Entry, entry)
                        ? LayerChangeKind.Identical : LayerChangeKind.Modified;
                    Charge(entry.Path, previous, kind);
                }
                live[entry.Path] = new(entry, index);
                paths.Add(entry.Path);
                changes.Add(new(entry.Path, index, kind, entry.Type, entry.Size, entry));
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
            LiveLayers = live.ToDictionary(pair => pair.Key, pair => pair.Value.Layer,
                StringComparer.Ordinal),
            HiddenFiles = hiddenFiles
        };
    }
    private static bool Same(ScannedEntry a, ScannedEntry b) =>
        a.Type == b.Type &&
        a.Mode == b.Mode &&
        a.UserId == b.UserId &&
        a.GroupId == b.GroupId &&
        a.Size == b.Size &&
        a.ContentHash == b.ContentHash &&
        a.LinkTarget == b.LinkTarget;
}
