using System.Globalization;
using Spectre.Console;
using SemanticCompareOptions = Valleysoft.Dredge.Commands.Image.CompareOptions;

namespace Valleysoft.Dredge;

internal static class CompareTextRenderer
{
    private static readonly string[] KindOrder = ["added", "removed", "changed"];

    internal static void Write(TextWriter writer, CompareResult result, int limit, bool isColorDisabled = true)
    {
        bool useColor = !isColorDisabled &&
            ReferenceEquals(writer, Console.Out) &&
            !Console.IsOutputRedirected &&
            AnsiConsole.Console.Profile.Capabilities.Ansi &&
            AnsiConsole.Console.Profile.Capabilities.ColorSystem != ColorSystem.NoColors;

        writer.WriteLine(TerminalText.EscapeControls($"Base    {result.Base.Requested}  {result.Base.Platform}  {result.Base.Identity}"));
        writer.WriteLine(TerminalText.EscapeControls($"Target  {result.Target.Requested}  {result.Target.Platform}  {result.Target.Identity}"));
        WriteStatus(writer, result, useColor);

        foreach (string scope in SemanticCompareOptions.AllScopes.Where(result.Options.Scopes.Contains))
        {
            CompareSection section = result.Sections[scope];
            Line($"  {Title(scope),-9} {FormatCounts(section)}{(IsIncomplete(section) ? $" ({Humanize(section.Coverage)})" : "")}");
        }

        if (result.Options.Scopes.Contains("files") && result.IgnoredFileDifferences > 0)
        {
            Line($"Ignored filesystem differences: {result.IgnoredFileDifferences}");
        }

        WriteCoverage();
        WriteSizes();
        WriteDetails();

        void WriteCoverage()
        {
            var incompleteSections = SemanticCompareOptions.AllScopes
                .Where(scope => result.Options.Scopes.Contains(scope) &&
                    IsIncomplete(result.Sections[scope]))
                .ToArray();
            var packageIssues = result.PackageCoverage
                .Where(static item => item.Coverage is not ("available" or "notDetected") ||
                    item.Failed > 0 || item.SkippedRecords > 0)
                .OrderBy(static item => item.Ecosystem, StringComparer.Ordinal)
                .ThenBy(static item => item.Operand, StringComparer.Ordinal)
                .ToArray();

            if (incompleteSections.Length == 0 && packageIssues.Length == 0 && result.Diagnostics.Count == 0)
            {
                return;
            }

            Line("Coverage");
            foreach (string scope in incompleteSections)
            {
                Line($"  {Title(scope)}: {Humanize(result.Sections[scope].Coverage)}");
            }
            foreach (ComparePackageCoverage item in packageIssues)
            {
                string counts = $"{item.Parsed}/{item.Candidates} parsed";
                if (item.Failed > 0) { counts += $", {item.Failed} failed"; }
                if (item.SkippedRecords > 0) { counts += $", {item.SkippedRecords} skipped"; }
                Line($"  {item.Ecosystem} {item.Operand}: {Humanize(item.Coverage)} ({counts})");
            }
            foreach (CompareDiagnostic diagnostic in result.Diagnostics)
            {
                string location = $"{Title(diagnostic.Section)}";
                if (diagnostic.Operand != "comparison") { location += $" {diagnostic.Operand}"; }
                if (diagnostic.Path is not null) { location += $" /{diagnostic.Path}"; }
                Line($"  Warning: {location}: {diagnostic.Message}");
            }
        }

        void WriteSizes()
        {
            if (!result.Options.Scopes.Contains("sizes") || result.Sizes.Count == 0)
            {
                return;
            }

            Line("Sizes");
            foreach (CompareSize size in result.Sizes.OrderBy(static item => item.Name, StringComparer.Ordinal))
            {
                string before = FormatBytes(size.BaseBytes);
                string after = FormatBytes(size.TargetBytes);
                string delta = size.DeltaBytes is long bytes ? FormatDelta(bytes) : "unavailable";
                string estimate = size.Estimate ? " (estimate)" : "";
                Line($"  {Humanize(size.Name)}: {before} -> {after} ({delta}){estimate}");
            }
        }

        void WriteDetails()
        {
            List<DetailRow> rows = [];
            if (result.Options.Scopes.Contains("files"))
            {
                rows.AddRange(result.Files.Select(static item => new DetailRow(
                    "files", "", item.Kind, item.Path, $"{item.Path} ({string.Join(", ", item.Reasons)})")));
            }
            if (result.Options.Scopes.Contains("packages"))
            {
                rows.AddRange(result.Packages.Select(static item =>
                {
                    string versions = item.Kind switch
                    {
                        "added" => JoinVersions(item.Target),
                        "removed" => JoinVersions(item.Base),
                        "changed" => $"{JoinVersions(item.Base)} -> {JoinVersions(item.Target)}",
                        _ => throw new InvalidOperationException($"Unsupported package difference kind '{item.Kind}'.")
                    };
                    string observed = item.Definitive ? "" : " (observed)";
                    return new DetailRow("packages", item.Ecosystem, item.Kind,
                        item.Name, $"{item.Name} [{DisplayScope(item.Scope)}] {versions}{observed}", item.Scope);
                }));
            }
            if (result.Options.Scopes.Contains("config"))
            {
                rows.AddRange(result.Config.Select(static item => new DetailRow(
                    "config", "", item.Kind, item.Path, $"{item.Path}: {FormatDifference(item.Kind, item.Base, item.Target)}")));
            }
            if (result.Options.Scopes.Contains("layers"))
            {
                rows.AddRange(result.Layers.Select(static item => new DetailRow(
                    "layers", "", item.Kind, item.Path, $"{item.Path}: {FormatDifference(item.Kind, item.Base, item.Target)}")));
            }

            if (rows.Count == 0)
            {
                return;
            }

            List<DetailGroup> groups = rows
                .GroupBy(static row => (row.Section, row.Ecosystem, row.Kind))
                .Select(group => new DetailGroup(
                    group.Key.Section,
                    group.Key.Ecosystem,
                    group.Key.Kind,
                    group.OrderBy(static row => row.SortKey, StringComparer.Ordinal)
                        .ThenBy(static row => row.SortScope, StringComparer.Ordinal).ToArray()))
                .OrderBy(static group => Array.IndexOf(SemanticCompareOptions.AllScopes, group.Section))
                .ThenBy(static group => group.Ecosystem, StringComparer.Ordinal)
                .ThenBy(static group => Array.IndexOf(KindOrder, group.Kind))
                .ToList();

            int shown = 0;
            while (shown < limit)
            {
                bool selectedAny = false;
                foreach (DetailGroup group in groups)
                {
                    if (shown >= limit) { break; }
                    if (group.Selected.Count >= group.Rows.Length) { continue; }
                    group.Selected.Add(group.Rows[group.Selected.Count]);
                    shown++;
                    selectedAny = true;
                }
                if (!selectedAny) { break; }
            }

            string? lastSection = null;
            string? lastEcosystem = null;
            foreach (DetailGroup group in groups)
            {
                if (group.Section != lastSection)
                {
                    Line(Title(group.Section));
                    lastSection = group.Section;
                    lastEcosystem = null;
                }
                if (group.Ecosystem.Length > 0 && group.Ecosystem != lastEcosystem)
                {
                    Line($"  {group.Ecosystem}");
                    lastEcosystem = group.Ecosystem;
                }

                Line($"    {Title(group.Kind)} ({group.Rows.Length})");
                char marker = Marker(group.Kind);
                string color = MarkerColor(group.Kind);
                foreach (DetailRow row in group.Selected)
                {
                    WriteDetail($"      {marker} ", row.Text, color);
                }
                int omitted = group.Rows.Length - group.Selected.Count;
                if (omitted > 0)
                {
                    Line($"      ... {omitted} more {group.Kind}");
                }
            }

            if (shown < rows.Count)
            {
                Line($"Showing {shown} of {rows.Count} differences; use --output json for all differences.");
            }
        }

        void Line(string text) => writer.WriteLine(Truncate(TerminalText.EscapeControls(text)));

        void WriteDetail(string prefix, string text, string color)
        {
            string safePrefix = TerminalText.EscapeControls(prefix);
            string safeText = Truncate(TerminalText.EscapeControls(prefix + text));
            if (safeText.Length > safePrefix.Length)
            {
                safeText = safeText[safePrefix.Length..];
            }
            else
            {
                safeText = "";
            }

            if (useColor)
            {
                writer.WriteLine($"{color}{safePrefix}\u001b[0m{safeText}");
            }
            else
            {
                writer.WriteLine(safePrefix + safeText);
            }
        }
    }

    private static void WriteStatus(TextWriter writer, CompareResult result, bool useColor)
    {
        string status = result.Complete ? result.AreEqual == true ? "Equal" : "Different" : "Incomplete";
        string color = status switch
        {
            "Equal" => "\u001b[32m",
            "Different" => "\u001b[33m",
            _ => "\u001b[31m"
        };
        string text = $"Status: {status}";
        string safeText = Truncate(TerminalText.EscapeControls(text));
        if (useColor) { writer.WriteLine($"{color}{safeText}\u001b[0m"); }
        else { writer.WriteLine(safeText); }
    }

    private static string FormatCounts(CompareSection section) =>
        section.Differences == 0
            ? IsIncomplete(section) ? "no known differences" : "equal"
            : $"+{section.Added} -{section.Removed} ~{section.Changed}";

    private static bool IsIncomplete(CompareSection section) =>
        section.Coverage is not ("available" or "notDetected" or "notRequested");

    private static string JoinVersions(ComparePackageFact? package) =>
        package is null ? "" : string.Join(", ", package.Versions);

    private static string DisplayScope(string scope) =>
        scope.StartsWith("system/", StringComparison.Ordinal) ? scope["system/".Length..] : scope;

    private static string FormatDifference(string kind, System.Text.Json.JsonElement? @base, System.Text.Json.JsonElement? target) =>
        kind switch
        {
            "added" => target?.GetRawText() ?? "",
            "removed" => @base?.GetRawText() ?? "",
            "changed" => $"{@base?.GetRawText()} -> {target?.GetRawText()}",
            _ => throw new InvalidOperationException($"Unsupported value difference kind '{kind}'.")
        };

    private static string FormatBytes(long? bytes)
    {
        if (bytes is null) { return "unavailable"; }
        string[] units = ["B", "KiB", "MiB", "GiB", "TiB", "PiB"];
        double value = bytes.Value;
        int unit = 0;
        while (Math.Abs(value) >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return $"{value.ToString("0.#", CultureInfo.InvariantCulture)} {units[unit]}";
    }

    private static string FormatDelta(long bytes) =>
        bytes > 0 ? $"+{FormatBytes(bytes)}" : FormatBytes(bytes);

    private static string Humanize(string value) =>
        value switch
        {
            "notRequested" => "not requested",
            "notDetected" => "not detected",
            "registryManifestDigest" => "registry manifest digest",
            "dockerImageId" => "Docker image ID",
            "registryCompressedLayerBytes" => "Registry compressed layers",
            "totalLayerFilePayloadBytes" => "Total layer file payload",
            "effectiveLiveContentBytes" => "Effective live content",
            "hiddenContentBytes" => "Hidden content",
            "targetOnlyUniqueRegistryBlobBytes" => "Target-only unique registry blobs",
            _ => value.Length == 0 ? value : char.ToUpperInvariant(value[0]) + value[1..]
        };

    private static string Title(string value) => Humanize(value);

    private static char Marker(string kind) =>
        kind switch
        {
            "added" => '+',
            "removed" => '-',
            "changed" => '~',
            _ => throw new InvalidOperationException($"Unsupported difference kind '{kind}'.")
        };

    private static string MarkerColor(string kind) =>
        kind switch
        {
            "added" => "\u001b[32m",
            "removed" => "\u001b[31m",
            "changed" => "\u001b[33m",
            _ => throw new InvalidOperationException($"Unsupported difference kind '{kind}'.")
        };

    private static string Truncate(string text)
    {
        if (text.Length <= 512) { return text; }
        int length = char.IsHighSurrogate(text[508]) ? 508 : 509;
        return text[..length] + "...";
    }

    private sealed record DetailRow(string Section, string Ecosystem, string Kind, string SortKey, string Text, string SortScope = "");

    private sealed class DetailGroup(string section, string ecosystem, string kind, DetailRow[] rows)
    {
        public string Section { get; } = section;
        public string Ecosystem { get; } = ecosystem;
        public string Kind { get; } = kind;
        public DetailRow[] Rows { get; } = rows;
        public List<DetailRow> Selected { get; } = [];
    }
}
