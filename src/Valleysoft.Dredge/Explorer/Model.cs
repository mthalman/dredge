using System.Globalization;
using Valleysoft.DockerRegistryClient.Models.Images;

namespace Valleysoft.Dredge.Explorer;

internal enum Change { None, Added, Modified, Identical, Removed }
internal enum Kind { Dir, File, Link }

// One row of a file tree. Trees are built once and never mutated afterward,
// so sizes and counts are cached on first use.
internal sealed class Node
{
    private long? size;
    private long? shipped;
    private Dictionary<Change, int>? counts;
    private bool? containsNote;

    public required string Name { get; init; }
    public required string Path { get; init; }
    public required Kind Kind { get; init; }
    public Change Change { get; set; }
    public long OwnSize { get; set; }
    public string Mode { get; set; } = "";
    public string Owner { get; set; } = "";
    public string? Target { get; set; }
    public bool HardLink { get; set; }
    public ScannedEntry? Entry { get; set; }
    public List<Node> Children { get; } = [];
    public string? Note { get; set; }

    public long Size => size ??= Kind switch
    {
        Kind.Dir => Children.Where(child => child.Change != Change.Removed).Sum(child => child.Size),
        _ => OwnSize
    };

    public long ShippedSize => shipped ??= Kind switch
    {
        Kind.Dir => Children.Sum(child => child.ShippedSize),
        _ => OwnSize
    };

    // Leaf counts by change: files, links and empty directories.
    public IReadOnlyDictionary<Change, int> Counts
    {
        get
        {
            if (counts is null)
            {
                counts = [];
                if (Kind != Kind.Dir || Children.Count == 0)
                {
                    counts[Change] = 1;
                }
                foreach (Node child in Children)
                {
                    foreach ((Change change, int count) in child.Counts)
                    {
                        counts[change] = counts.GetValueOrDefault(change) + count;
                    }
                }
            }
            return counts;
        }
    }

    public bool ContainsNote => containsNote ??= Note is not null || Children.Any(child => child.ContainsNote);

    public int FileCount => Counts.Values.Sum();
}

internal sealed record HistoryRow(
    int? Layer,
    string Instruction,
    long Download = 0,
    bool IsBase = false,
    string Created = "",
    long Reclaimable = 0);

internal sealed record SearchHit(
    string Path, bool Dir, long Size, (int Layer, Change Change)[] Layers, string? Note = null);

// What the explorer knows about the image it shows. Layers arrive over time:
// SetIndexed records a layer's raw index, SetAnalysis the analysis of the
// contiguous indexed prefix, and SetSession the fully loaded image.
internal sealed class ExplorerImage
{
    private readonly Dictionary<int, LayerChanges> raw = [];
    private readonly Dictionary<int, List<Node>> layerTrees = [];
    private readonly LinkedList<(int Layer, List<Node> Tree)> wholeTrees = new();
    private Dictionary<string, List<(int Layer, Change Change)>>? searchIndex;
    private List<HistoryRow> history;

    public ExplorerImage(
        string reference, string? platform, string digest, IReadOnlyList<string> layerDigests,
        IReadOnlyList<long> layerSizes, IReadOnlyList<LayerHistory>? configHistory, int? baseLayerCount,
        string? baseName, string? baseWarning = null, DateTime? now = null,
        IReadOnlyList<ExplorerBaseImage>? baseImages = null)
    {
        Reference = reference;
        Platform = platform ?? "linux";
        Digest = digest;
        LayerDigests = layerDigests;
        LayerDownloads = layerSizes;
        BaseLayerCount = baseLayerCount;
        BaseName = baseName;
        BaseImages = baseImages ?? (baseLayerCount is int count && baseName is not null
            ? [new ExplorerBaseImage(baseName, count)] : []);
        BaseWarning = baseWarning;
        LayerCount = layerDigests.Count;
        States = new ExplorerLayerState[LayerCount];
        Progress = new double[LayerCount];
        Errors = new string?[LayerCount];
        (history, Instructions) = BuildHistory(configHistory, layerSizes, baseLayerCount, now ?? DateTime.UtcNow);
        string repo = reference.Split('@')[0];
        int slash = repo.LastIndexOf('/');
        int colon = repo.LastIndexOf(':');
        RepoName = (colon > slash ? repo[..colon] : repo)[(slash + 1)..];
    }

    public static ExplorerImage FromSource(ExplorerSource source) => new(
        source.Image.ToString(),
        source.Platform?.ToString() ?? $"{source.Config.Os}/{source.Config.Architecture}",
        source.Resolved.ManifestInfo.DockerContentDigest,
        source.Resolved.Manifest.Layers.Select(layer => layer.Digest ?? "").ToArray(),
        source.Resolved.Manifest.Layers.Select(layer => layer.Size).ToArray(),
        source.Config.History, source.BaseLayerCount, source.BaseName, source.BaseWarning,
        baseImages: source.BaseImages)
    {
        PlatformArguments = source.Platforms.Count > 1 && source.Platform is ExplorerPlatform platform
            ? PlatformArgumentsFor(platform) : "",
    };

    // The flags that select this platform on other dredge commands.
    internal static string PlatformArgumentsFor(ExplorerPlatform platform)
    {
        string text = $"--os {ShellCommand.Quote(platform.Os)} --arch {ShellCommand.Quote(platform.Architecture)}";
        if (!string.IsNullOrEmpty(platform.OsVersion))
        {
            text += $" --os-version {ShellCommand.Quote(platform.OsVersion)}";
        }
        return text;
    }

    public string PlatformArguments { get; init; } = "";

    public string Reference { get; }
    public string ResolvedReference => DigestReference(ImageName.Parse(Reference), Digest);

    internal static string DigestReference(ImageName image, string digest) =>
        new ImageName(image.Registry, image.Repo, tag: null, digest).ToString();
    public string Platform { get; }
    public string Digest { get; }
    public string RepoName { get; }
    public string? BaseName { get; }
    public IReadOnlyList<ExplorerBaseImage> BaseImages { get; }
    public string? BaseWarning { get; }
    public int? BaseLayerCount { get; }
    public int LayerCount { get; }
    public IReadOnlyList<string> LayerDigests { get; }
    public IReadOnlyList<long> LayerDownloads { get; }
    public IReadOnlyList<string> Instructions { get; }
    public IReadOnlyList<HistoryRow> History => history;

    // Written by indexing workers; read by the UI for progress animation.
    public ExplorerLayerState[] States { get; }
    public double[] Progress { get; }
    public string?[] Errors { get; }

    public ImageAnalysisResult? Analysis { get; private set; }
    public int AnalyzedCount => Analysis?.Layers.Count ?? 0;
    public ExplorerInsightsResult Insights { get; private set; } = ExplorerInsightsResult.Empty;
    public IReadOnlyList<ExplorerFinding> Findings => Insights.Findings;
    public ExplorerSession? Session { get; private set; }
    public string? SessionError { get; set; }
    public bool Complete => Session is not null;
    public bool Loading => !Complete && States.Any(state => state != ExplorerLayerState.Ready);
    public int ReadyCount => States.Count(state => state == ExplorerLayerState.Ready);

    public IEnumerable<int> LayerIndexes => Enumerable.Range(0, LayerCount);
    public bool IsBase(int layer) => layer < (BaseLayerCount ?? 0);
    public string? BaseImageAt(int layer) =>
        BaseImages.FirstOrDefault(baseImage => layer < baseImage.LayerCount)?.Name;
    public string BaseLabel(string reference)
    {
        string name = ImageName.Parse(reference).Repo.Split('/')[^1];
        return name == RepoName || BaseImages.Any(baseImage =>
            baseImage.Name != reference && ImageName.Parse(baseImage.Name).Repo.Split('/')[^1] == name)
            ? reference : name;
    }
    public string GroupAt(int layer) => BaseImageAt(layer) ?? Reference;
    public bool IsIndexed(int layer) => raw.ContainsKey(layer) || IsAnalyzed(layer);
    public bool IsAnalyzed(int layer) => layer < AnalyzedCount;

    public HistoryRow Row(int layer) => history.First(row => row.Layer == layer);

    public long LayerSize(int layer) =>
        IsAnalyzed(layer) ? Analysis!.Layers[layer].FileBytes :
        raw.TryGetValue(layer, out LayerChanges? changes)
            ? changes.Entries.Where(entry => entry.Type == ImageFileType.File).Sum(entry => entry.Size)
            : 0;

    public long TotalSize => Analysis?.FileBytes ?? LayerIndexes.Sum(LayerSize);
    public long TotalDownload => LayerDownloads.Sum();
    public long TotalReclaimable => Insights.HiddenBytes;
    public long PotentialSavings => Insights.PotentialBytes;
    public double Efficiency => TotalSize == 0 ? 1 : 1 - (double)TotalReclaimable / TotalSize;

    public int? FirstUserLayer => LayerIndexes.Where(layer => !IsBase(layer)).Cast<int?>().FirstOrDefault();

    public void SetIndexed(int layer, LayerChanges changes)
    {
        raw[layer] = changes;
        States[layer] = ExplorerLayerState.Ready;
        Progress[layer] = 1;
        Errors[layer] = null;
        layerTrees.Remove(layer);
        searchIndex = null;
    }

    public bool ReconcileIndexes(IReadOnlyDictionary<int, StoredLayerIndex> indexes)
    {
        bool changed = false;
        foreach ((int layer, StoredLayerIndex index) in indexes)
        {
            if (States[layer] != ExplorerLayerState.Ready)
            {
                SetIndexed(layer, index.Changes);
                changed = true;
            }
        }
        return changed;
    }

    public IReadOnlyList<LayerChanges>? IndexedPrefix()
    {
        int count = 0;
        while (count < LayerCount && raw.ContainsKey(count))
        {
            count++;
        }
        return count == 0 ? null : Enumerable.Range(0, count).Select(layer => raw[layer]).ToArray();
    }

    public void SetAnalysis(ImageAnalysisResult analysis, ExplorerInsightsResult insights)
    {
        Analysis = analysis;
        Insights = insights;
        history = history.Select(row => row.Layer is int layer && layer < analysis.Layers.Count
            ? row with { Reclaimable = analysis.Layers[layer].HiddenBytes } : row).ToList();
        layerTrees.Clear();
        wholeTrees.Clear();
        searchIndex = null;
    }

    public void SetSession(ExplorerSession session, ExplorerInsightsResult insights)
    {
        Session = session;
        for (int layer = 0; layer < LayerCount; layer++)
        {
            States[layer] = ExplorerLayerState.Ready;
            Progress[layer] = 1;
        }
        SetAnalysis(session.Analysis, insights);
    }

    // ───────────────────────────── trees ─────────────────────────────

    public List<Node> LayerTree(int layer)
    {
        if (layerTrees.TryGetValue(layer, out List<Node>? cached))
        {
            return cached;
        }
        TreeBuilder builder = new();
        if (IsAnalyzed(layer))
        {
            foreach (LayerFileChange change in Analysis!.Layers[layer].Changes)
            {
                builder.Set(change.Path, change.Entry, change.Type, ToChange(change.Kind), change.Size);
            }
        }
        else if (raw.TryGetValue(layer, out LayerChanges? changes))
        {
            foreach (ScannedEntry entry in changes.Entries)
            {
                builder.Set(entry.Path, entry, entry.Type, Change.None, entry.Size);
            }
            foreach (string whiteout in changes.Whiteouts)
            {
                builder.Set(whiteout, null, ImageFileType.Other, Change.Removed, 0);
            }
        }
        List<Node> tree = builder.Build();
        ApplyNotes(tree, layer);
        layerTrees[layer] = tree;
        return tree;
    }

    // Effective filesystem after applying layers 0..layer, marking this layer's changes.
    public List<Node>? WholeTree(int layer)
    {
        if (!IsAnalyzed(layer))
        {
            return null;
        }
        LinkedListNode<(int Layer, List<Node> Tree)>? hit = wholeTrees.First;
        while (hit is not null && hit.Value.Layer != layer)
        {
            hit = hit.Next;
        }
        if (hit is not null)
        {
            wholeTrees.Remove(hit);
            wholeTrees.AddFirst(hit);
            return hit.Value.Tree;
        }

        Dictionary<string, LayerFileChange> live = new(StringComparer.Ordinal);
        for (int index = 0; index < layer; index++)
        {
            foreach (LayerFileChange change in Analysis!.Layers[index].Changes)
            {
                if (change.Kind == LayerChangeKind.Deleted)
                {
                    live.Remove(change.Path);
                }
                else
                {
                    live[change.Path] = change;
                }
            }
        }
        Dictionary<string, Change> marks = new(StringComparer.Ordinal);
        foreach (LayerFileChange change in Analysis!.Layers[layer].Changes)
        {
            marks[change.Path] = ToChange(change.Kind);
            if (change.Kind != LayerChangeKind.Deleted || !live.ContainsKey(change.Path))
            {
                live[change.Path] = change;
            }
        }
        TreeBuilder builder = new();
        foreach (LayerFileChange change in live.Values)
        {
            builder.Set(change.Path, change.Entry, change.Type,
                marks.GetValueOrDefault(change.Path, Change.None), change.Size);
        }
        List<Node> tree = builder.Build();
        ApplyNotes(tree, layer);
        wholeTrees.AddFirst((layer, tree));
        while (wholeTrees.Count > 3)
        {
            wholeTrees.RemoveLast();
        }
        return tree;
    }

    private void ApplyNotes(List<Node> tree, int layer)
    {
        foreach (ExplorerFinding finding in Findings.Where(finding => finding.Layers.Contains(layer)))
        {
            foreach (string root in finding.Roots)
            {
                if (Find(tree, root) is Node node && node.Note is null)
                {
                    node.Note = finding.NoteFor(layer);
                }
            }
        }
    }

    public static Node? Find(List<Node> roots, string path)
    {
        List<Node> level = roots;
        Node? node = null;
        foreach (string part in path.Split('/'))
        {
            node = level.FirstOrDefault(candidate => candidate.Name == part);
            if (node is null)
            {
                return null;
            }
            level = node.Children;
        }
        return node;
    }

    // ───────────────────────────── search ─────────────────────────────

    public IReadOnlyDictionary<string, List<(int Layer, Change Change)>> SearchIndex
    {
        get
        {
            if (searchIndex is not null)
            {
                return searchIndex;
            }
            Dictionary<string, List<(int, Change)>> index = new(StringComparer.Ordinal);
            void Add(string path, int layer, Change change)
            {
                if (!index.TryGetValue(path, out List<(int, Change)>? list))
                {
                    index[path] = list = [];
                }
                if (list.Count > 0 && list[^1].Item1 == layer)
                {
                    list[^1] = (layer, change);
                }
                else
                {
                    list.Add((layer, change));
                }
            }
            for (int layer = 0; layer < LayerCount; layer++)
            {
                if (IsAnalyzed(layer))
                {
                    foreach (LayerFileChange change in Analysis!.Layers[layer].Changes)
                    {
                        Add(change.Path, layer, ToChange(change.Kind));
                    }
                }
                else if (raw.TryGetValue(layer, out LayerChanges? changes))
                {
                    foreach (ScannedEntry entry in changes.Entries)
                    {
                        Add(entry.Path, layer, Change.None);
                    }
                }
            }
            return searchIndex = index;
        }
    }

    public List<(int Layer, Change Change)> PathHistory(string path) =>
        SearchIndex.TryGetValue(path, out List<(int Layer, Change Change)>? list) ? list : [];

    public ScannedEntry? EntryAt(string path, int layer)
    {
        if (!IsAnalyzed(layer))
        {
            return raw.TryGetValue(layer, out LayerChanges? changes)
                ? changes.Entries.LastOrDefault(entry => entry.Path == path) : null;
        }
        ScannedEntry? result = null;
        for (int index = 0; index <= layer; index++)
        {
            foreach (LayerFileChange change in Analysis!.Layers[index].Changes)
            {
                if (change.Path == path)
                {
                    result = change.Kind == LayerChangeKind.Deleted && index < layer ? null : change.Entry;
                }
            }
        }
        return result;
    }

    // ───────────────────────────── helpers ─────────────────────────────

    internal static Change ToChange(LayerChangeKind kind) => kind switch
    {
        LayerChangeKind.Added => Change.Added,
        LayerChangeKind.Modified => Change.Modified,
        LayerChangeKind.Identical => Change.Identical,
        _ => Change.Removed
    };

    internal static string FormatMode(ImageFileType type, int mode)
    {
        char kind = type switch
        {
            ImageFileType.Directory => 'd',
            ImageFileType.SymbolicLink => 'l',
            ImageFileType.File or ImageFileType.HardLink => '-',
            _ => '?'
        };
        char[] text = new char[10];
        text[0] = kind;
        int[] masks = [0x100, 0x80, 0x40, 0x20, 0x10, 0x8, 0x4, 0x2, 0x1];
        string symbols = "rwxrwxrwx";
        for (int index = 0; index < masks.Length; index++)
        {
            text[index + 1] = (mode & masks[index]) == 0 ? '-' : symbols[index];
        }
        text[3] = Special(text[3], mode, 0x800, 's', 'S');
        text[6] = Special(text[6], mode, 0x400, 's', 'S');
        text[9] = Special(text[9], mode, 0x200, 't', 'T');
        return new string(text);

        static char Special(char execute, int mode, int mask, char with, char without) =>
            (mode & mask) == 0 ? execute : execute == 'x' ? with : without;
    }

    internal static string Relative(DateTime? created, DateTime now)
    {
        if (created is not DateTime value || value.Year < 2000)
        {
            return "";
        }
        TimeSpan age = now - value.ToUniversalTime();
        if (age < TimeSpan.Zero)
        {
            return value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        }
        return age.TotalMinutes < 1 ? "just now" :
            age.TotalHours < 1 ? Plural((int)age.TotalMinutes, "minute") :
            age.TotalDays < 1 ? Plural((int)age.TotalHours, "hour") :
            age.TotalDays < 45 ? Plural((int)age.TotalDays, "day") :
            age.TotalDays < 365 ? Plural((int)(age.TotalDays / 30), "month") :
            Plural((int)(age.TotalDays / 365), "year");

        static string Plural(int value, string unit) => $"{value} {unit}{(value == 1 ? "" : "s")} ago";
    }

    private static (List<HistoryRow>, string[]) BuildHistory(
        IReadOnlyList<LayerHistory>? configHistory, IReadOnlyList<long> sizes, int? baseLayerCount, DateTime now)
    {
        int baseCount = baseLayerCount ?? 0;
        string[] instructions = Enumerable.Range(0, sizes.Count).Select(layer => $"Layer {layer}").ToArray();
        List<HistoryRow> rows = [];
        IReadOnlyList<LayerHistory> items = configHistory ?? [];
        if (items.Count(item => !item.IsEmptyLayer) != sizes.Count)
        {
            // History that doesn't line up with the layers can't be attributed; show layers alone.
            for (int layer = 0; layer < sizes.Count; layer++)
            {
                rows.Add(new(layer, "(no history for this layer)", sizes[layer], layer < baseCount));
            }
            return (rows, instructions);
        }

        int next = 0;
        List<(LayerHistory Item, int? Layer)> mapped = items
            .Select(item => (item, item.IsEmptyLayer ? (int?)null : next++)).ToList();
        for (int index = 0; index < mapped.Count; index++)
        {
            (LayerHistory item, int? layer) = mapped[index];
            string instruction = InstructionText.Normalize(item.CreatedBy ?? item.Comment);
            // An instruction without files belongs to the base if a later base layer follows it.
            bool isBase = layer is int value
                ? value < baseCount
                : mapped.Skip(index + 1).Any(other => other.Layer is int later && later < baseCount);
            if (layer is int current)
            {
                instructions[current] = instruction;
                rows.Add(new(current, instruction, sizes[current], isBase, Relative(item.Created, now)));
            }
            else
            {
                rows.Add(new(null, instruction, 0, isBase));
            }
        }
        return (rows, instructions);
    }

    private sealed class TreeBuilder
    {
        private readonly List<Node> roots = [];
        private readonly Dictionary<string, Node> nodes = new(StringComparer.Ordinal);

        public void Set(string path, ScannedEntry? entry, ImageFileType type, Change change, long size)
        {
            if (path.Length == 0)
            {
                return;
            }
            Kind kind = type switch
            {
                ImageFileType.Directory => Kind.Dir,
                ImageFileType.SymbolicLink or ImageFileType.HardLink => Kind.Link,
                _ => Kind.File
            };
            Node node = GetOrCreate(path, kind, explicitKind: true, removed: change == Change.Removed);
            node.Change = change;
            node.OwnSize = kind == Kind.Dir ? 0 : size;
            node.Entry = entry;
            if (entry is not null)
            {
                node.Mode = FormatMode(entry.Type, entry.Mode);
                node.Owner = $"{entry.UserId}:{entry.GroupId}";
                node.Target = entry.LinkTarget;
                node.HardLink = entry.Type == ImageFileType.HardLink;
            }
        }

        private Node GetOrCreate(string path, Kind kind, bool explicitKind = false, bool removed = false)
        {
            if (nodes.TryGetValue(path, out Node? existing))
            {
                if (existing.Kind == kind || (!explicitKind && removed))
                {
                    return existing;
                }
                Node replacement = new() { Name = existing.Name, Path = path, Kind = kind };
                replacement.Children.AddRange(existing.Children);
                List<Node> siblings = Parent(path, removed);
                siblings[siblings.IndexOf(existing)] = replacement;
                nodes[path] = replacement;
                return replacement;
            }
            Node node = new() { Name = path[(path.LastIndexOf('/') + 1)..], Path = path, Kind = kind };
            Parent(path, removed).Add(node);
            nodes[path] = node;
            return node;
        }

        private List<Node> Parent(string path, bool removed)
        {
            int slash = path.LastIndexOf('/');
            return slash < 0 ? roots : GetOrCreate(path[..slash], Kind.Dir, removed: removed).Children;
        }

        public List<Node> Build()
        {
            Sort(roots);
            return roots;
        }

        private static void Sort(List<Node> list)
        {
            list.Sort((left, right) => string.CompareOrdinal(left.Name, right.Name));
            foreach (Node node in list)
            {
                Sort(node.Children);
            }
        }
    }
}
