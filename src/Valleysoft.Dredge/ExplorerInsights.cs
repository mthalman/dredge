using System.Globalization;

namespace Valleysoft.Dredge;

internal enum ExplorerFindingKind { Replaced, Identical, Deleted, BaseReplaced, FromBase, Potential }

internal sealed record ExplorerFinding(
    ExplorerFindingKind Kind,
    string Title,
    string Category,
    long Bytes,
    int FileCount,
    IReadOnlyList<int> Layers,
    IReadOnlyList<string> Roots,
    string Where,
    string Why,
    string FixLabel,
    string Fix,
    bool FixIsDockerfile,
    IReadOnlyList<string> Explain)
{
    public bool Certain => Kind != ExplorerFindingKind.Potential;
    public bool FromBase => Kind == ExplorerFindingKind.FromBase;

    // The note a tree row at one of Roots shows in the given layer.
    public string? NoteFor(int layer) => Kind switch
    {
        ExplorerFindingKind.Potential => Title.Contains("cache", StringComparison.OrdinalIgnoreCase) ||
            Title.Contains("lists", StringComparison.Ordinal) ? "left behind" : "likely unintended",
        ExplorerFindingKind.Deleted when layer == Layers[0] => $"deleted in layer {Layers[^1]}",
        ExplorerFindingKind.Deleted => $"deletes layer {Layers[0]}",
        _ when layer == Layers[0] => $"hidden by layer {Layers[^1]}",
        _ => $"replaces layer {Layers[0]}",
    };
}

internal sealed record ExplorerInsightsResult(
    IReadOnlyList<ExplorerFinding> Findings,
    long HiddenBytes,
    long BaseChurnBytes,
    long PotentialBytes)
{
    public static readonly ExplorerInsightsResult Empty = new([], 0, 0, 0);
}

// Turns exact hidden-file records and heuristic potential savings into the
// findings the Insights view lists. Only hidden bytes count toward efficiency.
internal static class ExplorerInsights
{
    internal const long BaseChurnThreshold = 5_000_000;
    internal const long SmallGroupThreshold = 1_000_000;
    private const int MaxRoots = 3;

    public static ExplorerInsightsResult Build(
        ImageAnalysisResult analysis,
        IReadOnlyList<string> instructions,
        int? baseLayerCount,
        bool includePotential = true)
    {
        int baseCount = baseLayerCount ?? 0;
        List<ExplorerFinding> findings = [];
        long churn = 0;
        List<HiddenFile> small = [];

        foreach (IGrouping<(int Layer, int HiddenBy, LayerChangeKind Reason), HiddenFile> group in analysis.HiddenFiles
            .GroupBy(file => (file.Layer, file.HiddenBy, file.Reason)))
        {
            (int layer, int hiddenBy, LayerChangeKind reason) = group.Key;
            HiddenFile[] files = group.ToArray();
            long bytes = files.Sum(file => file.Size);
            if (bytes == 0)
            {
                continue;
            }
            bool producerIsBase = layer < baseCount;
            bool hiderIsBase = hiddenBy < baseCount;
            if (producerIsBase && hiderIsBase)
            {
                findings.Add(Create(ExplorerFindingKind.FromBase, files, instructions));
            }
            else if (producerIsBase)
            {
                if (bytes < BaseChurnThreshold)
                {
                    churn += bytes;
                }
                else
                {
                    findings.Add(Create(ExplorerFindingKind.BaseReplaced, files, instructions));
                }
            }
            else if (bytes < SmallGroupThreshold)
            {
                small.AddRange(files);
            }
            else
            {
                findings.Add(Create(reason switch
                {
                    LayerChangeKind.Identical => ExplorerFindingKind.Identical,
                    LayerChangeKind.Deleted => ExplorerFindingKind.Deleted,
                    _ => ExplorerFindingKind.Replaced
                }, files, instructions));
            }
        }

        if (small.Count > 0)
        {
            findings.Add(CreateSmall(small));
        }

        long potentialBytes = 0;
        if (includePotential)
        {
            foreach (ImagePotentialSaving saving in analysis.FindPotentialSavings())
            {
                potentialBytes += saving.Bytes;
                findings.Add(CreatePotential(saving, analysis.LiveLayers, instructions));
            }
        }

        List<ExplorerFinding> ordered =
        [
            .. findings.Where(finding => finding.Certain && !finding.FromBase).OrderByDescending(finding => finding.Bytes),
            .. findings.Where(finding => !finding.Certain).OrderByDescending(finding => finding.Bytes),
            .. findings.Where(finding => finding.FromBase).OrderByDescending(finding => finding.Bytes),
        ];
        return new(ordered, analysis.HiddenBytes, churn, potentialBytes);
    }

    private static ExplorerFinding Create(
        ExplorerFindingKind kind, HiddenFile[] files, IReadOnlyList<string> instructions)
    {
        int layer = files[0].Layer, hiddenBy = files[0].HiddenBy;
        string[] paths = files.Select(file => file.Path).ToArray();
        IReadOnlyList<string> roots = SummarizeRoots(paths);
        string where = FormatWhere(roots, paths);
        long bytes = files.Sum(file => file.Size);
        string producer = Instruction(instructions, layer);
        string hider = Instruction(instructions, hiddenBy);
        string count = N(files.Length);
        string noun = files.Length == 1 ? "file" : "files";
        (string title, string category) = kind switch
        {
            ExplorerFindingKind.Identical => ("Files rewritten with identical bytes", "Replaced by later layers"),
            ExplorerFindingKind.Deleted => ("Files deleted after they were shipped", "Deleted later"),
            ExplorerFindingKind.BaseReplaced => ("Base image files replaced", "Base image files replaced"),
            ExplorerFindingKind.FromBase => ("Hidden inside the base image", "From the base image"),
            _ => ("Files replaced by a later layer", "Replaced by later layers"),
        };
        string why = kind switch
        {
            ExplorerFindingKind.Deleted =>
                $"Layer {hiddenBy} hides them, but layer {layer} still downloads them.",
            ExplorerFindingKind.Identical =>
                $"Layer {hiddenBy} wrote the same bytes layer {layer} already shipped.",
            ExplorerFindingKind.BaseReplaced =>
                $"Layer {hiddenBy} replaced files the base image ships in layer {layer}.",
            ExplorerFindingKind.FromBase =>
                $"The base image replaces its own files; only the base image can fix this.",
            _ => $"Layer {hiddenBy} wrote over files layer {layer} already shipped.",
        };
        (string fixLabel, string fix, bool dockerfile) = kind switch
        {
            ExplorerFindingKind.BaseReplaced or ExplorerFindingKind.FromBase =>
                ("Use a newer base image", "FROM <base>:<newer tag>", true),
            _ => Fix(kind, producer, hider, paths),
        };
        string verb = kind == ExplorerFindingKind.Deleted ? "deleted" : "wrote";
        List<string> explain =
        [
            $"Layer {layer} shipped {count} {noun} under {where}.",
            $"Layer {hiddenBy} {verb} {(files.Length == 1 ? "it" : "them")} again, so {Size(bytes)} is hidden.",
            "",
            $"The older copies are hidden, but every pull still downloads them as part of layer {layer}.",
        ];
        if (kind == ExplorerFindingKind.Deleted)
        {
            explain[1] = $"Layer {hiddenBy} deleted {(files.Length == 1 ? "it" : "them")}, hiding {Size(bytes)}.";
        }
        return new(kind, title, category, bytes, files.Length, [layer, hiddenBy], roots, where,
            why, fixLabel, fix, dockerfile, explain);
    }

    private static ExplorerFinding CreateSmall(List<HiddenFile> files)
    {
        string[] paths = files.Select(file => file.Path).ToArray();
        IReadOnlyList<string> roots = SummarizeRoots(paths);
        int[] layers = files.SelectMany(file => new[] { file.Layer, file.HiddenBy }).Distinct().Order().ToArray();
        long bytes = files.Sum(file => file.Size);
        return new(ExplorerFindingKind.Replaced, "Other small overwrites", "Replaced by later layers", bytes,
            files.Count, layers, roots, FormatWhere(roots, paths),
            "Several layers each hide a little of what earlier layers shipped.",
            "Write each file in one layer", "Combine the steps that touch these files", false,
            [
                $"{Count(files.Count, "file")} {(files.Count == 1 ? "is" : "are")} hidden in groups under {Size(SmallGroupThreshold)} each.",
                $"Together they add {Size(bytes)} to every pull.",
            ]);
    }

    private static ExplorerFinding CreatePotential(
        ImagePotentialSaving saving, IReadOnlyDictionary<string, int> liveLayers, IReadOnlyList<string> instructions)
    {
        int[] layers = saving.Paths
            .Select(path => liveLayers.TryGetValue(path, out int layer) ? layer : -1)
            .Where(layer => layer >= 0).Distinct().Order().ToArray();
        IReadOnlyList<string> roots = SummarizeRoots(saving.Paths);
        bool underNodeModules = saving.Paths.Any(path => path.Contains("node_modules/", StringComparison.Ordinal));
        bool copied = layers.Any(layer => IsCopy(Instruction(instructions, layer)));
        (string title, string why, string label, string fix, bool dockerfile) = saving.Kind switch
        {
            PotentialSavingKind.NpmCache => ("npm cache left in the image",
                "npm keeps a download cache that the running app never reads.",
                "Cache it outside the image", "RUN --mount=type=cache,target=/root/.npm npm ci", true),
            PotentialSavingKind.AptLists => ("apt package lists left in the image",
                "apt-get update downloads package indexes that are only needed to install.",
                "Clean up in the same RUN", "RUN ... && rm -rf /var/lib/apt/lists/*", true),
            PotentialSavingKind.AptCache => ("apt download cache left in the image",
                "Downloaded .deb archives stay in /var/cache/apt after installing.",
                "Clean up in the same RUN", "RUN ... && apt-get clean", true),
            PotentialSavingKind.ApkCache => ("apk cache left in the image",
                "apk keeps downloaded package indexes and archives.",
                "Skip the cache", "RUN apk add --no-cache ...", true),
            PotentialSavingKind.PipCache => ("pip cache left in the image",
                "pip keeps downloaded wheels that the running app never reads.",
                "Skip the cache", "RUN pip install --no-cache-dir ...", true),
            PotentialSavingKind.YarnCache => ("Yarn cache left in the image",
                "Yarn keeps a package cache that the running app never reads.",
                "Clean up in the same RUN", "RUN yarn install && yarn cache clean", true),
            PotentialSavingKind.GitMetadata => ("Git history copied into the image",
                copied ? "COPY included the repository's .git directory." : "A .git directory is in the image.",
                "Add to .dockerignore", ".git", false),
            _ => ("Native files for another OS",
                "These files are built for macOS or Windows and can't run on Linux.",
                underNodeModules && copied ? "Add to .dockerignore" : "Install dependencies inside the image",
                underNodeModules && copied ? "node_modules" : "RUN npm ci", !(underNodeModules && copied)),
        };
        string where = FormatWhere(roots, saving.Paths);
        string layerText = layers.Length == 1 ? $"layer {layers[0]}" : $"layers {string.Join(", ", layers)}";
        return new(ExplorerFindingKind.Potential, title, "Potential savings", saving.Bytes, saving.Paths.Count,
            layers, roots, where, why, label, fix, dockerfile,
            [
                $"{Count(saving.Paths.Count, "file")} under {where}, from {layerText}.",
                "",
                "These bytes are live in the final filesystem, so they only count as savings if your app does not need them.",
            ]);
    }

    private static (string Label, string Fix, bool Dockerfile) Fix(
        ExplorerFindingKind kind, string producer, string hider, IReadOnlyList<string> paths)
    {
        bool nodeModules = paths.Any(path => path.Contains("node_modules/", StringComparison.Ordinal));
        if (IsCopy(hider) && nodeModules)
        {
            return ("Add to .dockerignore", "node_modules", false);
        }
        if (kind == ExplorerFindingKind.Deleted)
        {
            return IsCopy(producer)
                ? ("Build in a separate stage", "COPY --from=build /app/dist ./dist", true)
                : ("Delete in the same RUN that created them", "RUN ... && rm -rf <paths>", true);
        }
        if (IsCopy(hider))
        {
            return ("Copy each file once", "COPY only the files this step needs", false);
        }
        return ("Combine the steps", "RUN <first step> && <second step>", true);
    }

    private static bool IsCopy(string instruction) =>
        instruction.StartsWith("COPY", StringComparison.OrdinalIgnoreCase) ||
        instruction.StartsWith("ADD", StringComparison.OrdinalIgnoreCase);

    private static string Instruction(IReadOnlyList<string> instructions, int layer) =>
        layer >= 0 && layer < instructions.Count ? instructions[layer] : "";

    // The most specific set of at most three directories that covers every path.
    internal static IReadOnlyList<string> SummarizeRoots(IReadOnlyList<string> paths)
    {
        if (paths.Count == 0)
        {
            return [];
        }
        if (paths.Count == 1)
        {
            return [paths[0]];
        }
        string[][] split = paths.Select(path => path.Split('/')).ToArray();
        string[] best = split.Select(parts => parts[0]).Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal).ToArray();
        int maxDepth = split.Max(parts => parts.Length);
        for (int depth = 2; depth <= maxDepth; depth++)
        {
            string[] roots = split
                .Select(parts => string.Join('/', parts.Take(Math.Min(depth, Math.Max(1, parts.Length - 1)))))
                .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
            if (roots.Length > MaxRoots)
            {
                break;
            }
            best = roots;
        }
        return best;
    }

    internal static string FormatWhere(IReadOnlyList<string> roots, IReadOnlyList<string> paths)
    {
        if (roots.Count == 0)
        {
            return "";
        }
        IEnumerable<string> shown = roots.Take(MaxRoots).Select(root => "/" + root);
        string text = string.Join(", ", shown);
        return roots.Count > MaxRoots ? $"{text} +{N(roots.Count - MaxRoots)} more" : text;
    }

    private static string N(int value) => value.ToString("N0", CultureInfo.InvariantCulture);

    private static string Count(int value, string noun) => $"{N(value)} {noun}{(value == 1 ? "" : "s")}";

    internal static string Size(long bytes)
    {
        CultureInfo c = CultureInfo.InvariantCulture;
        if (bytes < 1000)
        {
            return $"{bytes} B";
        }
        double kb = bytes / 1000.0;
        if (kb < 1000)
        {
            return kb < 10 ? $"{kb.ToString("0.0", c)} KB" : $"{kb.ToString("0", c)} KB";
        }
        double mb = kb / 1000.0;
        return mb < 1000 ? $"{mb.ToString("0.0", c)} MB" : $"{(mb / 1000.0).ToString("0.00", c)} GB";
    }
}
