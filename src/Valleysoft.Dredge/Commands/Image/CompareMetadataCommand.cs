using Spectre.Console;
using Spectre.Console.Rendering;
using System.Text.Json.Nodes;
using static Valleysoft.Dredge.ImageMetadataComparison;

namespace Valleysoft.Dredge.Commands.Image;

public class CompareMetadataCommand : RegistryCommandBase<CompareMetadataOptions>
{
    private readonly IAnsiConsole ansiConsole;
    private readonly IAppSettingsStore settingsStore;

    public CompareMetadataCommand(
        IDockerRegistryClientFactory dockerRegistryClientFactory,
        IAnsiConsole? ansiConsole = null)
        : this(dockerRegistryClientFactory, ansiConsole, new AppSettingsStore())
    {
    }

    internal CompareMetadataCommand(
        IDockerRegistryClientFactory dockerRegistryClientFactory,
        IAnsiConsole? ansiConsole,
        IAppSettingsStore settingsStore)
        : base("metadata", "Compares two images by configuration and platform metadata", dockerRegistryClientFactory)
    {
        this.ansiConsole = ansiConsole ?? AnsiConsole.Console;
        this.settingsStore = settingsStore;
    }

    protected override Task ExecuteAsync(CancellationToken cancellationToken)
    {
        return ExecuteCommandAsync(
            registry: null,
            cancellationToken,
            async ct =>
            {
                CompareMetadataResult result = await GetResultAsync(ct);
                if (Options.OutputFormat == CompareOutput.Json)
                {
                    WriteJson(result);
                }
                else
                {
                    ansiConsole.Write(GetOutput(result));
                }
            });
    }

    public async Task<CompareMetadataResult> GetResultAsync(CancellationToken cancellationToken = default)
    {
        var sources = await OpenComparisonAsync(
            Options.BaseImage, Options.TargetImage, Options, cancellationToken, settingsStore,
            ImageSourceRequirements.Metadata);
        await using ResolvedImage baseSource = sources.Base;
        await using ResolvedImage targetSource = sources.Target;
        ReportComparison(baseSource, targetSource);
        bool includeDistributionMetadata = !baseSource.IsDaemon && !targetSource.IsDaemon;
        if (!includeDistributionMetadata)
        {
            Error.WriteLine(
                "Original registry manifests, annotations, and platform inventories are unavailable for Docker daemon images; comparing configuration metadata only.");
        }
        MetadataDocument baseDocument = GetMetadata(baseSource, includeDistributionMetadata);
        MetadataDocument targetDocument = GetMetadata(targetSource, includeDistributionMetadata);
        List<MetadataComparison> comparisons = Compare(baseDocument, targetDocument);

        CompareMetadataSummary summary = new(
            areEqual: comparisons.All(static comparison => comparison.Diff == CompareDiff.Equal),
            equal: comparisons.Count(static comparison => comparison.Diff == CompareDiff.Equal),
            changed: comparisons.Count(static comparison => comparison.Diff == CompareDiff.NotEqual),
            added: comparisons.Count(static comparison => comparison.Diff == CompareDiff.Added),
            removed: comparisons.Count(static comparison => comparison.Diff == CompareDiff.Removed));

        return new CompareMetadataResult(summary, comparisons);
    }

    public async Task<IRenderable> GetOutputAsync(CancellationToken cancellationToken = default) =>
        GetOutput(await GetResultAsync(cancellationToken));

    private void WriteJson(CompareMetadataResult result) =>
        CompareMetadataRenderer.WriteJson(ansiConsole, result);

    private IRenderable GetOutput(CompareMetadataResult result) =>
        CompareMetadataRenderer.GetOutput(Options, ansiConsole, result, isColorDisabled: false);

    internal static class CompareMetadataRenderer
    {
        public static void WriteJson(IAnsiConsole ansiConsole, CompareMetadataResult result) =>
            ansiConsole.Profile.Out.Writer.WriteLine(JsonHelper.Serialize(result));

        public static IRenderable GetOutput(
            CompareMetadataOptions options,
            IAnsiConsole ansiConsole,
            CompareMetadataResult result,
            bool isColorDisabled)
        {
            bool effectiveColorDisabled = isColorDisabled ||
                options.IsColorDisabled ||
                !ansiConsole.Profile.Capabilities.Ansi ||
                ansiConsole.Profile.Capabilities.ColorSystem == ColorSystem.NoColors;

            return options.OutputFormat switch
            {
                CompareOutput.SideBySide => GetSideBySideOutput(options, result, effectiveColorDisabled),
                CompareOutput.Inline => GetInlineOutput(result, effectiveColorDisabled),
                CompareOutput.Json => new Text(JsonHelper.Serialize(result)),
                _ => throw new NotSupportedException($"Unsupported metadata comparison output format '{options.OutputFormat}'.")
            };
        }

        private static Table GetSideBySideOutput(
            CompareMetadataOptions options,
            CompareMetadataResult result,
            bool isColorDisabled)
        {
            Table table = new Table()
                .AddColumn("Metadata")
                .AddColumn(options.BaseImage);

            if (isColorDisabled)
            {
                table.AddColumn(new TableColumn("Compare") { Alignment = Justify.Center });
            }

            table.AddColumn(options.TargetImage);

            foreach (MetadataComparison comparison in result.Comparisons)
            {
                List<IRenderable> cells =
                [
                    new Markup(Markup.Escape($"{comparison.Category}.{comparison.Path}")),
                    GetValueMarkup(comparison.BaseValue, comparison.Diff, isBase: true, isColorDisabled)
                ];

                if (isColorDisabled)
                {
                    cells.Add(new Markup(GetDiffDisplayName(comparison.Diff)));
                }

                cells.Add(GetValueMarkup(comparison.TargetValue, comparison.Diff, isBase: false, isColorDisabled));
                table.AddRow(cells);
            }

            return table;
        }

        private static Rows GetInlineOutput(CompareMetadataResult result, bool isColorDisabled)
        {
            List<IRenderable> rows = [];

            foreach (MetadataComparison comparison in result.Comparisons)
            {
                string path = $"{comparison.Category}.{comparison.Path}";
                if (comparison.Diff == CompareDiff.Equal)
                {
                    rows.Add(GetInlineMarkup("  ", path, comparison.BaseValue, Color.Default));
                    continue;
                }

                if (comparison.Diff is CompareDiff.NotEqual or CompareDiff.Removed)
                {
                    rows.Add(GetInlineMarkup(
                        "- ",
                        path,
                        comparison.BaseValue,
                        isColorDisabled ? Color.Default : Color.Red));
                }

                if (comparison.Diff is CompareDiff.NotEqual or CompareDiff.Added)
                {
                    rows.Add(GetInlineMarkup(
                        "+ ",
                        path,
                        comparison.TargetValue,
                        isColorDisabled ? Color.Default : Color.Green));
                }
            }

            return new Rows(rows);
        }

        private static Markup GetInlineMarkup(string prefix, string path, JsonNode? value, Color color) =>
            new(Markup.Escape($"{prefix}{path} = {FormatValue(value)}"), new Style(color));

        private static Markup GetValueMarkup(
            JsonNode? value,
            CompareDiff diff,
            bool isBase,
            bool isColorDisabled)
        {
            Color color = isColorDisabled ? Color.Default : diff switch
            {
                CompareDiff.NotEqual => isBase ? Color.Red : Color.Green,
                CompareDiff.Added => isBase ? Color.Default : Color.Green,
                CompareDiff.Removed => isBase ? Color.Red : Color.Default,
                _ => Color.Default
            };

            return new Markup(Markup.Escape(FormatValue(value)), new Style(color));
        }

        private static string FormatValue(JsonNode? value) =>
            value is null ? string.Empty : value.ToJsonString(JsonHelper.CompactSettings);

        private static string GetDiffDisplayName(CompareDiff diff) =>
            diff switch
            {
                CompareDiff.Equal => "Equal",
                CompareDiff.NotEqual => "Changed",
                CompareDiff.Added => "Added",
                CompareDiff.Removed => "Removed",
                _ => throw new ArgumentOutOfRangeException(nameof(diff), diff, "Unsupported metadata diff state.")
            };
    }
}
