namespace Valleysoft.Dredge.Explorer;

internal sealed class TagChoice
{
    public TagChoice(string tag) => Tag = tag;

    public string Tag { get; }
    public string? Digest { get; set; }
    public int? Shared { get; set; }
    public int? LayerCount { get; set; }
    public long? AdditionalDownload { get; set; }
    public string? Note { get; set; }
    public bool Failed { get; set; }
}

// Everything the explorer window needs from the outside world. The real host
// talks to the registry and the layer store; tests supply a fake.
internal interface IExplorerHost
{
    KeyMap Keys { get; }
    bool ClipboardEnabled { get; }

    void Prioritize(int layer);
    void Retry(int layer);

    Task<IReadOnlyList<string>> ListTagsAsync(CancellationToken cancellationToken);
    Task DescribeTagAsync(TagChoice choice, CancellationToken cancellationToken);
    Task<ExplorerComparison> CompareAsync(string tag, Action readingPackages, CancellationToken cancellationToken);
    Task<InstalledPackageMetadata> PackagesAsync(int layer, CancellationToken cancellationToken);
    Task<PreviewContent> PreviewAsync(string path, int layer, CancellationToken cancellationToken);
    Task<TextDiffContent> DiffAsync(ExplorerComparison comparison, string path, CancellationToken cancellationToken);
    Task<PackageFilesContent> PackageFilesAsync(
        ExplorerComparison comparison, ExplorerPackageDifference package, CancellationToken cancellationToken);
    Task<string> ExtractAsync(string path, string destination, CancellationToken cancellationToken);
    Task<string> PrepareForViewerAsync(string path, CancellationToken cancellationToken);
    // False when the clipboard couldn't be reached.
    bool WriteClipboard(string text);
}

internal enum ExplorerExitKind { Quit, Viewer }

internal sealed record ExplorerExit(ExplorerExitKind Kind, string? ViewerFile = null);
