namespace Valleysoft.Dredge;

internal enum LayerChangeKind { Added, Modified, Identical, Deleted }

// Entry includes derived content metadata changes, or for a deletion the entry it hid.
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
        [.. LiveEntries.Values
            .Where(static entry => entry.Type == ImageFileType.File && entry.Size > 0)
            .Select(static entry => (Entry: entry, Kind: PotentialKind(entry.Path)))
            .Where(static item => item.Kind is not null)
            .GroupBy(static item => item.Kind!.Value)
            .Select(static group => new ImagePotentialSaving(group.Key,
                group.Sum(static item => item.Entry.Size),
                [.. group.Select(static item => item.Entry.Path).Order(StringComparer.Ordinal)]))
            .OrderByDescending(static item => item.Bytes)];

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
    private sealed record LiveEntry(ScannedEntry Entry, int Layer, FileContent? Content, string? ContentLinkTarget);

    private sealed class FileContent(ScannedEntry entry, int layer)
    {
        public ScannedEntry Entry { get; } = entry;
        public int Layer { get; } = layer;
        public ImageContentId Id { get; } = new(layer, entry.Path, entry.EntryIndex);
        public int References { get; set; }
    }

    public static ImageAnalysisResult Analyze(IReadOnlyList<LayerChanges> layers,
        ImageFileSystemSemantics? semantics = null)
    {
        semantics ??= ImageFileSystemSemantics.Linux;
        Dictionary<string, LiveEntry> live = new(semantics.Comparer);
        WindowsAlternateStreams? alternateStreams = semantics.IsWindows ? new() : null;
        Dictionary<ImageContentId, ImageContentPaths>? contentPaths = semantics.IsWindows ? [] : null;
        HashSet<ImageContentId>? unreferencedContents = semantics.IsWindows ? [] : null;
        SortedSet<string> paths = new(semantics.Comparer);
        long[] hidden = new long[layers.Count];
        List<ImageLayerAnalysis> analyses = [];
        List<HiddenFile> hiddenFiles = [];
        long total = 0;

        for (int index = 0; index < layers.Count; index++)
        {
            LayerChanges layer = layers[index];
            List<LayerFileChange> changes = [];
            Dictionary<string, int>? changeIndexes = semantics.IsWindows ? new(semantics.Comparer) : null;
            long layerBytes = 0;

            void Record(LayerFileChange change)
            {
                if (changeIndexes is not null) changeIndexes[change.Path] = changes.Count;
                changes.Add(change);
            }

            void Remove(string path, LiveEntry old)
            {
                RemoveOwnerStreams(path);
                Charge(path, old, LayerChangeKind.Deleted);
                Record(new(path, index, LayerChangeKind.Deleted, old.Entry.Type,
                    old.Content?.Entry.Size ?? old.Entry.Size, old.Entry) { ContentId = old.Content?.Id });
                live.Remove(path);
                paths.Remove(path);
                alternateStreams?.Forget(path);
            }

            void Delete(string path, bool includePath)
            {
                if (includePath && path.Contains(':') && alternateStreams is not null)
                {
                    foreach (string stream in alternateStreams.DeleteStream(path, GetStreamOwner(path)))
                    {
                        if (live.TryGetValue(stream, out LiveEntry? old)) Remove(stream, old);
                    }
                }
                else if (includePath)
                {
                    RemoveOwnerStreams(path);
                }
                string[] descendants = semantics.Descendants(paths, path);
                foreach (string existing in includePath && live.ContainsKey(path)
                    ? descendants.Prepend(path) : descendants)
                {
                    if (live.TryGetValue(existing, out LiveEntry? old)) Remove(existing, old);
                }
            }

            void RemoveOwnerStreams(string path)
            {
                if (alternateStreams is null) return;
                foreach (string stream in alternateStreams.PathsForOwner(path))
                {
                    if (live.TryGetValue(stream, out LiveEntry? old)) Remove(stream, old);
                }
            }

            void Charge(string path, LiveEntry old, LayerChangeKind reason, string? replacedByHardLink = null)
            {
                if (old.Content is FileContent previous && contentPaths is not null)
                {
                    ImageContentPaths aliases = contentPaths[previous.Id];
                    aliases.Remove(path, semantics.Comparer);
                    if (aliases.Count == 0)
                    {
                        contentPaths.Remove(previous.Id);
                        unreferencedContents!.Add(previous.Id);
                    }
                    else
                    {
                        contentPaths[previous.Id] = aliases;
                    }
                }
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

            foreach (ScannedEntry scanned in layer.Entries)
            {
                ScannedEntry entry = scanned;
                if (entry.Type != ImageFileType.Directory)
                {
                    Delete(entry.Path, includePath: false);
                }
                string parent = ImagePath.GetDirectoryName(entry.Path);
                while (parent.Length > 0)
                {
                    live.TryGetValue(parent, out LiveEntry? parentEntry);
                    semantics.ValidateParent(entry.Path, parent, parentEntry?.Entry.Type);
                    if (parentEntry is not null && parentEntry.Entry.Type != ImageFileType.Directory)
                    {
                        Remove(parent, parentEntry);
                    }
                    if (semantics.IsWindows && parentEntry?.Entry.Type != ImageFileType.Directory)
                    {
                        ScannedEntry directory = new(parent, ImageFileType.Directory, 0x1ED, 0, 0, 0,
                            DateTime.MinValue, null, 0, 0, 0, null)
                        {
                            Windows = new(null, false, false, "", null)
                        };
                        live[parent] = new(directory, index, null, null);
                        paths.Add(parent);
                        Record(new(parent, index, LayerChangeKind.Added, ImageFileType.Directory, 0, directory));
                    }
                    parent = ImagePath.GetDirectoryName(parent);
                }

                LayerChangeKind kind = LayerChangeKind.Added;
                LiveEntry? hardLinkTarget = entry.Type == ImageFileType.HardLink
                    ? GetHardLinkTarget(entry, live, semantics) : null;
                if (semantics.IsWindows && hardLinkTarget?.Entry.Type == ImageFileType.Other)
                {
                    entry = entry with
                    {
                        Type = ImageFileType.Other,
                        Windows = ImageFileSystemSemantics.UnsupportedHardLink(hardLinkTarget.Entry.Path, entry.Windows)
                    };
                }
                FileContent? content = entry.Type == ImageFileType.File
                    ? new(entry, index) : hardLinkTarget?.Content;
                string? contentLinkTarget = entry.Type == ImageFileType.SymbolicLink
                    ? entry.LinkTarget : hardLinkTarget?.ContentLinkTarget;
                if (content is not null)
                {
                    content.References++;
                }
                if (alternateStreams is not null && content is not null && entry.Windows is not null &&
                    entry.Windows.HasAlternateStreams != alternateStreams.HasStreams(content.Id))
                {
                    entry = entry with
                    {
                        Windows = entry.Windows with { HasAlternateStreams = alternateStreams.HasStreams(content.Id) }
                    };
                }
                if (live.TryGetValue(entry.Path, out LiveEntry? previous))
                {
                    if (previous.Entry.Type != ImageFileType.Directory || entry.Type != ImageFileType.Directory)
                    {
                        RemoveOwnerStreams(entry.Path);
                    }
                    kind = Same(previous.Entry, entry) &&
                        (entry.Type != ImageFileType.HardLink ||
                            (SameContent(previous.Content, content) && previous.ContentLinkTarget == contentLinkTarget))
                        ? LayerChangeKind.Identical : LayerChangeKind.Modified;
                    Charge(entry.Path, previous, entry.Type == ImageFileType.HardLink ? LayerChangeKind.Modified : kind,
                        entry.Type == ImageFileType.HardLink ? entry.Path : null);
                }
                live[entry.Path] = new(entry, index, content, contentLinkTarget);
                if (content is not null && contentPaths is not null)
                {
                    contentPaths.TryGetValue(content.Id, out ImageContentPaths aliases);
                    aliases.Add(entry.Path, semantics.Comparer);
                    contentPaths[content.Id] = aliases;
                }
                paths.Add(entry.Path);
                if (alternateStreams is not null && entry.Path.Contains(':'))
                {
                    alternateStreams.Track(entry.Path, GetStreamOwner(entry.Path));
                }
                Record(new(entry.Path, index, kind, entry.Type, content?.Entry.Size ?? entry.Size, entry)
                    { ContentId = content?.Id });
                if (entry.Type == ImageFileType.File)
                {
                    layerBytes += entry.Size;
                }

            }
            if (alternateStreams is not null)
            {
                foreach (ScannedEntry entry in layer.Entries)
                {
                    if (live.TryGetValue(entry.Path, out LiveEntry? owner) && owner.ContentLinkTarget is null)
                    {
                        alternateStreams.BindPending(entry.Path, owner.Content?.Id);
                    }
                }
                foreach (ImageContentId content in unreferencedContents!)
                {
                    if (!contentPaths!.ContainsKey(content)) alternateStreams.ForgetContent(content);
                }
                unreferencedContents!.Clear();
                foreach (ImageContentId content in alternateStreams.TakePresenceChanges())
                {
                    if (!contentPaths!.TryGetValue(content, out ImageContentPaths aliases)) continue;
                    bool hasStreams = alternateStreams.HasStreams(content);
                    foreach (string path in aliases)
                    {
                        LiveEntry owner = live[path];
                        if (owner.Entry.Windows is null || owner.Entry.Windows.HasAlternateStreams == hasStreams) continue;
                        ScannedEntry updated = owner.Entry with
                        {
                            Windows = owner.Entry.Windows with { HasAlternateStreams = hasStreams }
                        };
                        live[path] = owner with { Entry = updated, Layer = index };
                        if (changeIndexes!.TryGetValue(path, out int position))
                        {
                            LayerFileChange written = changes[position];
                            changes[position] = written with
                            {
                                Entry = updated,
                                Kind = written.Kind == LayerChangeKind.Identical ? LayerChangeKind.Modified : written.Kind
                            };
                        }
                        else
                        {
                            Record(new(path, index, LayerChangeKind.Modified, updated.Type,
                                owner.Content!.Entry.Size, updated) { ContentId = content });
                        }
                    }
                }
            }

            total += layerBytes;
            analyses.Add(new(index, layerBytes, 0, changes));
        }

        return new(total, hidden.Sum(), [.. analyses.Select(layer => layer with { HiddenBytes = hidden[layer.Index] })])
        {
            LiveEntries = live.ToDictionary(pair => pair.Key, pair => pair.Value.Entry,
                semantics.Comparer),
            LiveContents = live.Where(pair => pair.Value.Content is not null)
                .ToDictionary(pair => pair.Key, pair => pair.Value.Content!.Entry, semantics.Comparer),
            LiveLayers = live.ToDictionary(pair => pair.Key, pair => pair.Value.Layer,
                semantics.Comparer),
            HiddenFiles = hiddenFiles
        };

        ImageContentId? GetStreamOwner(string path) =>
            live.TryGetValue(WindowsAlternateStreams.BasePath(path), out LiveEntry? owner) &&
            owner.ContentLinkTarget is null ? owner.Content?.Id : null;
    }
    private static LiveEntry GetHardLinkTarget(ScannedEntry entry, IReadOnlyDictionary<string, LiveEntry> live,
        ImageFileSystemSemantics semantics)
    {
        string path = semantics.NormalizeHardLinkTarget(entry.LinkTarget ??
            throw new InvalidDataException($"Hard link '/{entry.Path}' has no target."), entry.Path);
        for (int hop = 0; hop < 40; hop++)
        {
            string[] parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
            List<string> resolved = [];
            bool followed = false;
            for (int i = 0; i < parts.Length - 1; i++)
            {
                if (!semantics.IsWindows && parts[i] == ".")
                {
                    continue;
                }
                if (!semantics.IsWindows && parts[i] == "..")
                {
                    if (resolved.Count > 0)
                    {
                        resolved.RemoveAt(resolved.Count - 1);
                    }
                    continue;
                }
                resolved.Add(parts[i]);
                string parent = string.Join('/', resolved);
                if (live.TryGetValue(parent, out LiveEntry? link) && link.Entry.Type == ImageFileType.SymbolicLink)
                {
                    string target = link.Entry.LinkTarget ??
                        throw new InvalidDataException($"Link '/{parent}' has no target.");
                    path = semantics.ResolveLinkTarget(
                        semantics.IsAbsolute(target) ? "" : ImagePath.GetDirectoryName(parent),
                        target, string.Join('/', parts.Skip(i + 1)), parent);
                    followed = true;
                    break;
                }
            }
            if (!followed)
            {
                path = parts.Length == 0 ? string.Empty :
                    ImagePath.Join(string.Join('/', resolved), parts[^1]);
                live.TryGetValue(path, out LiveEntry? target);
                ImageFileSystemSemantics.ValidateHardLinkTarget(entry.Path, path, target?.Entry.Type);
                return target!;
            }
        }
        throw new InvalidDataException($"Link resolution for '/{entry.Path}' exceeded 40 hops.");
    }

    private static bool SameContent(FileContent? a, FileContent? b) =>
        ReferenceEquals(a, b) || (a is not null && b is not null && Same(a.Entry, b.Entry));

    private static bool Same(ScannedEntry a, ScannedEntry b) =>
        a.Type == b.Type &&
        (a.Windows is not null || a.Mode == b.Mode &&
            a.UserId == b.UserId && a.GroupId == b.GroupId) &&
        a.Size == b.Size &&
        a.ContentHash == b.ContentHash &&
        a.LinkTarget == b.LinkTarget &&
        a.Windows == b.Windows;
}
