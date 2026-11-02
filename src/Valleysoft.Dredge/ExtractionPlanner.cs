namespace Valleysoft.Dredge;

internal sealed class ExtractionPlanner
{
    private readonly ImagePathResolver resolver;
    private readonly IReadOnlyDictionary<string, ImageFileSystemEntry> entries;
    private readonly ImageFileSystemSemantics semantics;

    public ExtractionPlanner(ImagePathResolver resolver, IReadOnlyDictionary<string, ImageFileSystemEntry> entries,
        ImageFileSystemSemantics? semantics = null)
    {
        this.resolver = resolver;
        this.entries = entries;
        this.semantics = semantics ?? ImageFileSystemSemantics.Linux;
    }

    public ExtractionPlan CreatePlan(
        string requestedPath,
        string outputPath,
        bool extractingRoot,
        ImageFileSystemEntry? source,
        IReadOnlyDictionary<string, ImageFileSystemEntry> entries,
        bool useCapturedStreamState = false)
    {
        string sourcePath = semantics.NormalizeRequested(requestedPath);
        if (!extractingRoot && source is not null)
        {
            sourcePath = source.Path;
        }

        string fullOutputPath = Path.GetFullPath(outputPath);
        ImageFileSystemExtractor.ValidateNewDestination(fullOutputPath);
        string? missingParentRoot = ImageFileSystemExtractor.GetMissingParentRoot(fullOutputPath);
        List<ImageFileSystemEntry> selected = SelectExtractionEntries(sourcePath, source, extractingRoot, entries, semantics);
        ValidateExtractionEntries(selected);
        if (semantics.IsWindows)
        {
            ImageFileSystemEntry? unsupported = selected.FirstOrDefault(static entry =>
                entry.Type == ImageFileType.SymbolicLink || entry.ContentLinkTarget is not null ||
                entry.Windows?.UnsupportedReason is not null || entry.Windows?.HasAlternateStreams == true);
            string streamPrefix = sourcePath + ":";
            if (unsupported is not null || (!useCapturedStreamState &&
                entries.Keys.Any(path => semantics.StartsWith(path, streamPrefix))))
            {
                throw new NotSupportedException($"Windows extraction is content-only; '{unsupported?.Path ?? sourcePath}' has unsupported link or alternate-stream semantics.");
            }
        }
        Dictionary<string, string> destinations = CreateExtractionDestinations(
            selected,
            sourcePath,
            fullOutputPath,
            extractingRoot,
            semantics);
        Dictionary<string, string> hardLinkTargets = semantics.IsWindows ? [] : GetExtractionHardLinkTargets(selected);
        HashSet<string> preservableHardLinks = semantics.IsWindows ? [] : GetPreservableHardLinks(
            selected,
            destinations,
            hardLinkTargets,
            entries);
        IReadOnlyDictionary<string, bool> symbolicLinkDirectoryTargets =
            semantics.IsWindows ? new Dictionary<string, bool>() : GetSymbolicLinkDirectoryTargets(selected);
        return new(
            source,
            extractingRoot,
            fullOutputPath,
            missingParentRoot,
            selected,
            destinations,
            hardLinkTargets,
            preservableHardLinks,
            symbolicLinkDirectoryTargets);
    }

    public static List<ImageFileSystemEntry> SelectExtractionEntries(
        string sourcePath,
        ImageFileSystemEntry? source,
        bool extractingRoot,
        IReadOnlyDictionary<string, ImageFileSystemEntry> entries,
        ImageFileSystemSemantics? semantics = null)
    {
        semantics ??= ImageFileSystemSemantics.Linux;
        return extractingRoot
            ? OrderExtractionEntries(entries.Values)
            : source!.Type == ImageFileType.Directory
                ? OrderExtractionEntries(entries.Values.Where(entry =>
                    semantics.Comparer.Equals(entry.Path, sourcePath) ||
                    semantics.StartsWith(entry.Path, $"{sourcePath}/")))
                : [source];
    }

    public static List<ImageFileSystemEntry> OrderExtractionEntries(IEnumerable<ImageFileSystemEntry> selected) =>
        [.. selected
            .OrderBy(static entry => entry.Path.Count(static c => c == '/'))
            .ThenBy(static entry => entry.Path, StringComparer.Ordinal)];

    public static void ValidateExtractionEntries(IEnumerable<ImageFileSystemEntry> selected)
    {
        ImageFileSystemEntry? unsupported = selected.FirstOrDefault(
            static entry => entry.Type == ImageFileType.Other);
        if (unsupported is not null)
        {
            throw new NotSupportedException(
                $"Path '/{unsupported.Path}' has unsupported file type '{unsupported.Type}'.");
        }
    }

    public static Dictionary<string, string> CreateExtractionDestinations(
        IEnumerable<ImageFileSystemEntry> selected,
        string sourcePath,
        string outputPath,
        bool extractingRoot,
        ImageFileSystemSemantics? semantics = null)
    {
        semantics ??= ImageFileSystemSemantics.Linux;
        Dictionary<string, string> destinations = new(semantics.Comparer);
        foreach (ImageFileSystemEntry entry in selected)
        {
            string destination = !extractingRoot && semantics.Comparer.Equals(entry.Path, sourcePath)
                ? outputPath
                : semantics.IsWindows && destinations.TryGetValue(ImagePath.GetDirectoryName(entry.Path), out string? parent)
                    ? ImageFileSystemExtractor.GetContainedDestination(parent, ImagePath.GetFileName(entry.Path))
                    : ImageFileSystemExtractor.GetContainedDestination(
                        outputPath,
                        extractingRoot ? entry.Path : entry.Path[(sourcePath.Length + 1)..]);
            destinations.Add(entry.Path, destination);
        }
        return destinations;
    }

    public Dictionary<string, string> GetExtractionHardLinkTargets(IEnumerable<ImageFileSystemEntry> selected) =>
        selected
            .Where(entry => entry.Type == ImageFileType.HardLink)
            .Select(entry => (Entry: entry, Target: resolver.TryGetHardLinkTargetPath(entry, entries)))
            .Where(item => item.Target is not null)
            .ToDictionary(
                item => item.Entry.Path,
                item => item.Target!,
                StringComparer.Ordinal);

    public static HashSet<string> GetPreservableHardLinks(
        IEnumerable<ImageFileSystemEntry> selected,
        IReadOnlyDictionary<string, string> destinations,
        IReadOnlyDictionary<string, string> hardLinkTargets,
        IReadOnlyDictionary<string, ImageFileSystemEntry> entries) =>
        selected
            .Where(entry =>
                entry.Type == ImageFileType.HardLink &&
                entry.ContentLinkTarget is null)
            .Where(entry =>
                hardLinkTargets.TryGetValue(entry.Path, out string? targetPath) &&
                destinations.ContainsKey(targetPath) &&
                entries.TryGetValue(targetPath, out ImageFileSystemEntry? target) &&
                target.ContentLayerIndex == entry.ContentLayerIndex &&
                target.ContentPath == entry.ContentPath &&
                target.ContentEntryIndex == entry.ContentEntryIndex)
            .Select(entry => entry.Path)
            .ToHashSet(StringComparer.Ordinal);

    public IReadOnlyDictionary<string, bool> GetSymbolicLinkDirectoryTargets(
        IEnumerable<ImageFileSystemEntry> selected)
    {
        return selected
            .Where(entry =>
                entry.Type == ImageFileType.SymbolicLink ||
                (entry.Type == ImageFileType.HardLink &&
                    entry.ContentLinkTarget is not null))
            .ToDictionary(
                entry => entry.Path,
                entry =>
                {
                    string? targetPath = entry.Type == ImageFileType.SymbolicLink
                        ? entry.Path
                        : resolver.TryGetHardLinkTargetPath(entry, entries);
                    return targetPath is not null &&
                        resolver.ResolvesToDirectory(targetPath, entries);
                },
                StringComparer.Ordinal);
    }

    public static List<(ImageFileSystemEntry Entry, string Destination)> GetContentExtractionRequests(ExtractionPlan plan) =>
        [.. plan.Entries
            .Where(entry =>
                entry.Type == ImageFileType.File ||
                (entry.Type == ImageFileType.HardLink &&
                    !plan.PreservableHardLinks.Contains(entry.Path) &&
                    entry.ContentLinkTarget is null))
            .Select(entry => (entry, plan.Destinations[entry.Path]))];
}

internal sealed record ExtractionPlan(
    ImageFileSystemEntry? Source,
    bool ExtractingRoot,
    string OutputPath,
    string? MissingParentRoot,
    IReadOnlyList<ImageFileSystemEntry> Entries,
    IReadOnlyDictionary<string, string> Destinations,
    IReadOnlyDictionary<string, string> HardLinkTargets,
    IReadOnlySet<string> PreservableHardLinks,
    IReadOnlyDictionary<string, bool> SymbolicLinkDirectoryTargets);
