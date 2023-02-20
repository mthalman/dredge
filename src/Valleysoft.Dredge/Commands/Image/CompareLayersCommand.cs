using Newtonsoft.Json;
using Spectre.Console;
using Spectre.Console.Rendering;
using System.Reflection;
using System.Runtime.Serialization;
using Valleysoft.Dredge.Core;

namespace Valleysoft.Dredge.Commands.Image;

public class CompareLayersCommand : RegistryCommandBase<CompareLayersOptions>
{
    private static readonly string[] SizeSuffixes =
        { "bytes", "KB", "MB", "GB", "TB", "PB", "EB", "ZB", "YB" };

    private readonly AppSettings appSettings;

    public CompareLayersCommand(IDockerRegistryClientFactory dockerRegistryClientFactory)
        : base("layers", "Compares two images by layers", dockerRegistryClientFactory)
    {
        this.appSettings = AppSettingsHelper.Load();
    }

    protected override Task ExecuteAsync()
    {
        return CommandHelper.ExecuteCommandAsync(registry: null, async () =>
        {
            IRenderable output = await GetOutputAsync();
            AnsiConsole.Write(output);
        });
    }

    public async Task<IRenderable> GetOutputAsync()
    {
        CompareLayersResult result = await LayersComparer.GetCompareLayersResult(
            DockerRegistryClientFactory, Options.BaseImage, Options.TargetImage, appSettings, Options.ToLayerCompareOptions());
        OutputFormatter formatter = OutputFormatter.Create(Options.OutputFormat);
        IRenderable output = formatter.GetOutput(result, Options);
        return output;
    }

    private static Color GetLayerDiffColor(LayerDiff diff, bool isBaseLayer, bool isColorDisabled) =>
        isColorDisabled ? Color.Default : diff switch
        {
            LayerDiff.Removed => Color.Red,
            LayerDiff.Added => Color.Green,
            LayerDiff.NotEqual => isBaseLayer ? Color.Red : Color.Green,
            LayerDiff.Equal => Color.Default,
            _ => throw new NotImplementedException()
        };

    private static Markup GetLayerDataMarkup(string? layerData, LayerDiff diff, bool isBase, bool isColorDisabled,
        bool isInline) =>
        new(
            Markup.Escape($"{GetTextOffset(diff, isInline, isBase)}{layerData ?? string.Empty}"),
            new Style(GetLayerDiffColor(diff, isBaseLayer: isBase, isColorDisabled)));

    private static Markup GetDigestMarkup(LayerInfo? layer, LayerDiff diff, bool isBase, bool isColorDisabled,
        bool includeSupplementalData, bool isInline)
    {
        if (layer is null)
        {
            return new Markup(string.Empty);
        }

        string digestMarkup = string.Empty;
        if (includeSupplementalData)
        {
            digestMarkup += $"[{Decoration.Invert.ToString().ToLower()}]";
        }

        digestMarkup += $"{Markup.Escape(layer?.Digest ?? "<empty layer>")}";

        if (includeSupplementalData)
        {
            // Close the markup tag
            digestMarkup += "[/]";
        }

        return new(
            $"{GetTextOffset(diff, isInline, isBase)}{digestMarkup}",
            new Style(GetLayerDiffColor(diff, isBase, isColorDisabled)));
    }

    private static string GetTextOffset(LayerDiff diff, bool isInline, bool isBase) =>
        !isInline ? string.Empty : diff switch
        {
            LayerDiff.Added => "+ ",
            LayerDiff.Equal => "  ",
            LayerDiff.NotEqual => isBase ? "- " : "+ ",
            LayerDiff.Removed => "- ",
            _ => throw new NotImplementedException()
        };

    private static string? FormatCompressedSize(long? size)
    {
        if (size is null)
        {
            return null;
        }

        return $"Size (compressed): {SizeSuffix(size.Value)}";
    }

    private static string SizeSuffix(long value)
    {
        int decimalPlaces = 1;

        int i = 0;
        decimal dValue = value;
        while (Math.Round(dValue, decimalPlaces) >= 1000)
        {
            dValue /= 1024;
            i++;
        }

        dValue = Math.Round(dValue, decimalPlaces);

        // If the number to the right of the decimal point is 0, don't include the decimal point in the output
        if (dValue * 10 % 10 == 0)
        {
            decimalPlaces = 0;
        }

        return string.Format("{0:n" + decimalPlaces + "} {1}", dValue, SizeSuffixes[i]);
    }

    private abstract class OutputFormatter
    {
        public static OutputFormatter Create(CompareLayersOutput outputFormat) =>
            outputFormat switch
            {
                CompareLayersOutput.Inline => new InlineFormatter(),
                CompareLayersOutput.Json => new JsonFormatter(),
                CompareLayersOutput.SideBySide => new SideBySideFormatter(),
                _ => throw new NotImplementedException()
            };

        public abstract IRenderable GetOutput(CompareLayersResult result, CompareLayersOptions options);

        private class SideBySideFormatter : OutputFormatter
        {
            public override IRenderable GetOutput(CompareLayersResult result, CompareLayersOptions options)
            {
                Table table = new Table()
                    .AddColumn(options.BaseImage);

                if (options.IsColorDisabled)
                {
                    // Use a comparison column to indicate the diff result with text instead of color
                    table.AddColumn(new TableColumn("Compare") { Alignment = Justify.Center });
                }

                table.AddColumn(options.TargetImage);

                for (int i = 0; i < result.LayerComparisons.Count(); i++)
                {
                    AddTableRows(result, options.IsColorDisabled, options.IncludeHistory, options.IncludeCompressedSize, table, i);
                }

                return table;
            }

            private static void AddTableRows(CompareLayersResult result, bool isColorDisabled, bool includeHistory,
                bool includeCompressedSize, Table table, int i)
            {
                LayerComparison layerComparison = result.LayerComparisons.ElementAt(i);
                IEnumerable<IRenderable> digestRowCells =
                    GetDigestRowCells(isColorDisabled, includeHistory || includeCompressedSize, layerComparison);
                table.AddRow(digestRowCells);

                if (includeHistory)
                {
                    List<IRenderable> historyRowCells = GetHistoryRowCells(isColorDisabled, layerComparison);
                    table.AddRow(historyRowCells);
                }

                if (includeCompressedSize)
                {
                    List<IRenderable> compressedSizeRowCells = GetCompressedSizeRowCells(isColorDisabled, layerComparison);
                    table.AddRow(compressedSizeRowCells);
                }

                if ((includeHistory || includeCompressedSize) && i + 1 != result.LayerComparisons.Count())
                {
                    table.AddEmptyRow();
                }
            }

            private static List<IRenderable> GetHistoryRowCells(bool isColorDisabled, LayerComparison layerComparison) =>
                GetLayerDataRowCells(isColorDisabled, layerComparison, layerInfo => layerInfo?.History);

            private static List<IRenderable> GetCompressedSizeRowCells(bool isColorDisabled, LayerComparison layerComparison) =>
                GetLayerDataRowCells(isColorDisabled, layerComparison, layerInfo => FormatCompressedSize(layerInfo?.CompressedSize));

            private static List<IRenderable> GetLayerDataRowCells(bool isColorDisabled, LayerComparison layerComparison, Func<LayerInfo?, string?> getLayerData)
            {
                List<IRenderable> historyCells = new()
                        {
                            GetLayerDataMarkup(getLayerData(layerComparison.Base), layerComparison.LayerDiff, isBase: true, isColorDisabled, isInline: false)
                        };

                if (isColorDisabled)
                {
                    historyCells.Add(new Markup(string.Empty));
                }

                historyCells.Add(GetLayerDataMarkup(getLayerData(layerComparison.Target), layerComparison.LayerDiff, isBase: false, isColorDisabled, isInline: false));
                return historyCells;
            }

            private static IEnumerable<IRenderable> GetDigestRowCells(bool isColorDisabled, bool includeSupplementalData, LayerComparison layerComparison)
            {
                List<IRenderable> shaCells = new()
                        {
                            GetDigestMarkup(
                                layerComparison.Base, layerComparison.LayerDiff, isBase : true, isColorDisabled, includeSupplementalData, isInline: false)
                        };
                if (isColorDisabled)
                {
                    shaCells.Add(new Markup(GetLayerDiffDisplayName(layerComparison.LayerDiff)));
                }

                shaCells.Add(
                    GetDigestMarkup(
                        layerComparison.Target, layerComparison.LayerDiff, isBase: false, isColorDisabled, includeSupplementalData, isInline: false));
                return shaCells;
            }

            private static string GetLayerDiffDisplayName(LayerDiff diff) =>
                typeof(LayerDiff).GetMember(diff.ToString()).Single().GetCustomAttribute<EnumMemberAttribute>()?.Value ??
                    throw new Exception($"Enum member not set for {diff}.");
        }

        private class InlineFormatter : OutputFormatter
        {
            public override IRenderable GetOutput(CompareLayersResult result, CompareLayersOptions options)
            {
                List<IRenderable> rows = new();

                for (int i = 0; i < result.LayerComparisons.Count(); i++)
                {
                    LayerComparison layerComparison = result.LayerComparisons.ElementAt(i);

                    if (layerComparison.Base is not null)
                    {
                        AddInlineLayerInfo(
                            rows, layerComparison.Base, layerComparison.LayerDiff, isBase: true, options.IsColorDisabled, options.IncludeHistory,
                            options.IncludeCompressedSize);
                    }

                    if (layerComparison.LayerDiff != LayerDiff.Equal && layerComparison.Target is not null)
                    {
                        AddInlineLayerInfo(
                            rows, layerComparison.Target, layerComparison.LayerDiff, isBase: false, options.IsColorDisabled, options.IncludeHistory,
                            options.IncludeCompressedSize);
                    }

                    // Add an empty row if we're not on the last layer
                    if ((options.IncludeHistory || options.IncludeCompressedSize) && i + 1 != result.LayerComparisons.Count())
                    {
                        rows.Add(new Text(string.Empty));
                    }
                }

                return new Rows(rows);
            }

            private static void AddInlineLayerInfo(List<IRenderable> rows, LayerInfo layer, LayerDiff diff, bool isBase,
                bool isColorDisabled, bool includeHistory, bool includeCompressedSize)
            {
                rows.Add(GetDigestMarkup(layer, diff, isBase, isColorDisabled, includeHistory || includeCompressedSize, isInline: true));
                if (includeHistory)
                {
                    rows.Add(GetLayerDataMarkup(layer.History, diff, isBase, isColorDisabled, isInline: true));
                }
                if (includeCompressedSize)
                {
                    rows.Add(GetLayerDataMarkup(FormatCompressedSize(layer.CompressedSize), diff, isBase, isColorDisabled, isInline: true));
                }
            }
        }

        private class JsonFormatter : OutputFormatter
        {
            public override IRenderable GetOutput(CompareLayersResult result, CompareLayersOptions options)
            {
                string output = JsonConvert.SerializeObject(result, new JsonSerializerSettings
                {
                    NullValueHandling = NullValueHandling.Ignore,
                    Formatting = Formatting.Indented
                });

                return new Text(output);
            }
        }
    }
}