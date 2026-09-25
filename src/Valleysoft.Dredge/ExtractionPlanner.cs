namespace Valleysoft.Dredge;

internal sealed class ExtractionPlanner
{
    private readonly ImagePathResolver resolver;
    private readonly IReadOnlyDictionary<string, ImageFileSystemEntry> entries;

    public ExtractionPlanner(ImagePathResolver resolver, IReadOnlyDictionary<string, ImageFileSystemEntry> entries)
    {
        this.resolver = resolver;
        this.entries = entries;
    }

    public ExtractionPlan CreatePlan(
        string requestedPath,
        string outputPath,
        bool extractingRoot,
        ImageFileSystemEntry? source,
        IReadOnlyDictionary<string, ImageFileSystemEntry> entries)
    {
        string sourcePath = ImagePath.NormalizeRequested(requestedPath);
        if (!extractingRoot && source is not null)
        {
            sourcePath = source.Path;
        }

        string fullOutputPath = Path.GetFullPath(outputPath);
        ImageFileSystemExtractor.ValidateNewDestination(fullOutputPath);
        string? missingParentRoot = ImageFileSystemExtractor.GetMissingParentRoot(fullOutputPath);
        List<ImageFileSystemEntry> selected = SelectExtractionEntries(sourcePath, source, extractingRoot, entries);
        ValidateExtractionEntries(selected);
        Dictionary<string, string> destinations = CreateExtractionDestinations(
            selected,
            sourcePath,
            fullOutputPath,
            extractingRoot);
        Dictionary<string, string> hardLinkTargets = GetExtractionHardLinkTargets(selected);
        HashSet<string> preservableHardLinks = GetPreservableHardLinks(
            selected,
            destinations,
            hardLinkTargets,
            entries);
        return new(
            source,
            extractingRoot,
            fullOutputPath,
            missingParentRoot,
            selected,
            destinations,
            hardLinkTargets,
            preservableHardLinks);
    }

    public static List<ImageFileSystemEntry> SelectExtractionEntries(
        string sourcePath,
        ImageFileSystemEntry? source,
        bool extractingRoot,
        IReadOnlyDictionary<string, ImageFileSystemEntry> entries)
    {
        return extractingRoot
            ? OrderExtractionEntries(entries.Values)
            : source!.Type == ImageFileType.Directory
                ? OrderExtractionEntries(entries.Values.Where(entry =>
                    entry.Path == sourcePath ||
                    entry.Path.StartsWith($"{sourcePath}/", StringComparison.Ordinal)))
                : [source];
    }

    public static List<ImageFileSystemEntry> OrderExtractionEntries(IEnumerable<ImageFileSystemEntry> selected) =>
        selected
            .OrderBy(entry => entry.Path.Count(c => c == '/'))
            .ThenBy(entry => entry.Path, StringComparer.Ordinal)
            .ToList();

    public static void ValidateExtractionEntries(IEnumerable<ImageFileSystemEntry> selected)
    {
        ImageFileSystemEntry? unsupported = selected.FirstOrDefault(
            entry => entry.Type == ImageFileType.Other);
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
        bool extractingRoot) =>
        selected.ToDictionary(
            entry => entry.Path,
            entry => !extractingRoot && entry.Path == sourcePath
                ? outputPath
                : ImageFileSystemExtractor.GetContainedDestination(
                    outputPath,
                    extractingRoot
                        ? entry.Path
                        : entry.Path[(sourcePath.Length + 1)..]),
            StringComparer.Ordinal);

    public Dictionary<string, string> GetExtractionHardLinkTargets(IEnumerable<ImageFileSystemEntry> selected) =>
        selected
            .Where(entry => entry.Type == ImageFileType.HardLink)
            .Select(entry => (Entry: entry, Target: resolver.TryGetHardLinkTargetPath(entry, entries.ToDictionary(kvp => kvp.Key, kvp => kvp.Value))))
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

    public static void CreateExtractionSubdirectories(ExtractionPlan plan, CancellationToken cancellationToken)
    {
        foreach (ImageFileSystemEntry directory in plan.Entries
            .Where(entry => entry.Type == ImageFileType.Directory))
        {
            cancellationToken.ThrowIfCancellationRequested();
            Directory.CreateDirectory(plan.Destinations[directory.Path]);
        }
    }

    public static List<(ImageFileSystemEntry Entry, string Destination)> GetContentExtractionRequests(ExtractionPlan plan) =>
        plan.Entries
            .Where(entry =>
                entry.Type == ImageFileType.File ||
                (entry.Type == ImageFileType.HardLink &&
                    !plan.PreservableHardLinks.Contains(entry.Path) &&
                    entry.ContentLinkTarget is null))
            .Select(entry => (entry, plan.Destinations[entry.Path]))
            .ToList();
}

internal sealed record ExtractionPlan(
    ImageFileSystemEntry? Source,
    bool ExtractingRoot,
    string OutputPath,
    string? MissingParentRoot,
    IReadOnlyList<ImageFileSystemEntry> Entries,
    IReadOnlyDictionary<string, string> Destinations,
    IReadOnlyDictionary<string, string> HardLinkTargets,
    IReadOnlySet<string> PreservableHardLinks);
