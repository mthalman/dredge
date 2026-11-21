using System.Text.Encodings.Web;
using System.Text.Json;
using Valleysoft.Dredge.Commands.Image;
using Valleysoft.Dredge.Explorer;

namespace Valleysoft.Dredge;

internal sealed record HtmlReportFile(string Path, string Type, long Size, string? Change = null);
internal sealed record HtmlReportDifference(string Path, string Change, long? Before, long? After);
internal sealed record HtmlReportLayer(int Index, string Digest, long Size, string? BaseImage, HtmlReportFile[] Changes);
internal sealed record HtmlReportHistory(int? Layer, string Instruction, string Created);
internal sealed record HtmlReportImage(
    string Reference, string Identity, string Platform, string Metadata,
    HtmlReportHistory[] History, HtmlReportLayer[] Layers, HtmlReportFile[] Files, string[] Warnings);
internal sealed record HtmlReportData(
    HtmlReportImage[] Images, HtmlReportDifference[]? Differences, string[] Limitations, int? InitialLayer);

internal static class HtmlImageReport
{
    private const string DataPlaceholder = "/* DREDGE_REPORT_DATA */";
    private static readonly JsonSerializerOptions SafeJson = new(JsonHelper.CompactSettings)
    {
        Encoder = JavaScriptEncoder.Default
    };

    public static async Task<string> CreateAsync(
        IImageSourceFactory factory, IDockerRegistryClientFactory registryFactory,
        ExploreOptions options, TextWriter error, CancellationToken cancellationToken,
        LayerStore? layerStore = null)
    {
        await using ImageSourceFactory.ComparisonScope? scope =
            options.Compare is not null ? (factory as ImageSourceFactory)?.CreateComparisonScope() : null;
        factory = scope ?? factory;
        await using LayerStore? ownedStore = layerStore is null ? LayerStore.Create() : null;
        LayerStore store = layerStore ?? ownedStore!;
        await using ExplorerSource baselineSource = await ExplorerSource.OpenAsync(
            factory, registryFactory, options.Image, options, options.BaseImages, cancellationToken, store: store);
        baselineSource.Source?.Report(error);
        LayerIndexOption.ValidateLayer(options.Layer, baselineSource.LayerCount);
        ExplorerSession baseline = await ExplorerSession.CreateAsync(
            baselineSource.RegistryClient, baselineSource, store, null, cancellationToken);
        await using ImageFileSystem baselineFiles = baseline.Files;
        HtmlReportImage baselineReport = CreateImage(baselineSource, baseline);

        if (options.Compare is null)
        {
            return Render(new([baselineReport], null, [], options.Layer));
        }

        string targetReference = ExploreCommand.ResolveCompareReference(options.Image, options.Compare);
        await using LayerStore targetStore = store.CreateScope();
        await using ExplorerSource targetSource = await ExplorerSource.OpenAsync(
            factory, registryFactory, targetReference, options, null, cancellationToken,
            exactPlatform: baselineSource.Platform, store: targetStore, comparing: true);
        targetSource.Source?.Report(error);
        ExplorerSession target = await ExplorerSession.CreateAsync(
            targetSource.RegistryClient, targetSource, targetStore, null, cancellationToken);
        await using ImageFileSystem targetFiles = target.Files;
        if (baseline.IsDaemon || target.IsDaemon)
        {
            await baseline.EnsureContentIdentitiesAsync(cancellationToken);
            await target.EnsureContentIdentitiesAsync(cancellationToken);
        }
        cancellationToken.ThrowIfCancellationRequested();
        ExplorerComparison comparison = ExplorerSession.Compare(baseline, target, includePackages: false);
        HtmlReportDifference[] differences = [.. comparison.Files.Select(static file =>
            new HtmlReportDifference(file.Path, file.Kind.ToString(), file.Baseline?.Size, file.Target?.Size))];
        return Render(new(
            [baselineReport, CreateImage(targetSource, target)],
            differences, [.. comparison.Limitations], options.Layer));
    }

    internal static HtmlReportImage CreateImage(ExplorerSource source, ExplorerSession session)
    {
        ExplorerImage image = ExplorerImage.FromSource(source);
        HtmlReportLayer[] layers = [.. session.Analysis.Layers.Select(layer =>
            new HtmlReportLayer(layer.Index, image.LayerDigests[layer.Index],
                image.LayerDownloads[layer.Index], image.BaseImageAt(layer.Index),
                [.. layer.Changes.Select(static change =>
                    new HtmlReportFile(change.Path, change.Type.ToString(), change.Size, change.Kind.ToString()))]))];
        string[] warnings = [.. new[] { source.BaseWarning,
            session.Semantics.IsWindows
                ? "Windows content-only analysis excludes unsupported filesystem and runtime metadata."
                : null }.OfType<string>(),
            .. session.Files.Diagnostics.Select(static diagnostic => $"{diagnostic.Message} {diagnostic.Path}")];
        return new HtmlReportImage(
            source.Reference, session.SnapshotReference, image.Platform,
            JsonHelper.Serialize(session.Config),
            [.. image.History.Select(static row => new HtmlReportHistory(row.Layer, row.Instruction, row.Created))],
            layers,
            [.. session.Entries.Select(static entry =>
                new HtmlReportFile(entry.Path, entry.Type.ToString(), entry.Size))],
            warnings);
    }

    internal static string Render(HtmlReportData data)
    {
        using Stream stream = typeof(HtmlImageReport).Assembly.GetManifestResourceStream(
            "Valleysoft.Dredge.HtmlImageReport.html") ??
            throw new InvalidOperationException("The HTML report template is missing.");
        using StreamReader reader = new(stream);
        string template = reader.ReadToEnd();
        if (!template.Contains(DataPlaceholder, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The HTML report template has no data placeholder.");
        }
        return template.Replace(DataPlaceholder, JsonHelper.Serialize(data, SafeJson), StringComparison.Ordinal);
    }
}
