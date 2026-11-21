using System.Text;
using System.Text.RegularExpressions;

namespace Valleysoft.Dredge;

internal static class ImageComparison
{
    internal sealed record FileFacts(string Path, ImageFileSystemEntry? Before, ImageFileSystemEntry? After,
        IReadOnlyList<string> Reasons, bool Incomplete);

    internal static IEnumerable<FileFacts> CompareFiles(
        IReadOnlyList<ImageFileSystemEntry> beforeEntries, IReadOnlyList<ImageFileSystemEntry> afterEntries,
        IReadOnlyDictionary<string, ScannedEntry> beforeContents, IReadOnlyDictionary<string, ScannedEntry> afterContents,
        ImageFileSystemSemantics semantics, bool timestamps, CancellationToken cancellationToken = default)
    {
        var before = beforeEntries.ToDictionary(static e => e.Path, semantics.Comparer);
        var after = afterEntries.ToDictionary(static e => e.Path, semantics.Comparer);
        foreach (string path in before.Keys.Concat(after.Keys).Distinct(semantics.Comparer).Order(semantics.Comparer))
        {
            cancellationToken.ThrowIfCancellationRequested();
            before.TryGetValue(path, out var old);
            after.TryGetValue(path, out var current);
            bool incomplete = Unavailable(old, beforeContents) || Unavailable(current, afterContents);
            IReadOnlyList<string> reasons = old is null ? ["added"] : current is null ? ["removed"] :
                Reasons(old, current, beforeContents, afterContents, semantics, timestamps);
            if (incomplete || reasons.Count > 0) { yield return new(path, old, current, reasons, incomplete); }
        }

        static bool Unavailable(ImageFileSystemEntry? entry, IReadOnlyDictionary<string, ScannedEntry> contents) =>
            entry is not null && (entry.Type == ImageFileType.Other || entry.Windows?.UnsupportedReason is not null ||
                HasContent(entry) && Hash(entry, contents) is null);
    }

    internal static Regex CreateIgnore(string pattern, bool windows)
    {
        if (string.IsNullOrWhiteSpace(pattern) || pattern.Contains('\\') ||
            pattern.IndexOfAny(['[', ']', '{', '}', '!', '\0']) >= 0 ||
            pattern.Split('/').Any(static segment => segment is "." or "..") ||
            pattern.Any(char.IsControl))
        {
            throw new ArgumentException($"Invalid image path glob '{TerminalText.EscapeControls(pattern)}'. Use /, *, ?, and ** without negation.");
        }
        pattern = pattern.TrimStart('/');
        StringBuilder expression = new("^");
        for (int i = 0; i < pattern.Length; i++)
        {
            char ch = pattern[i];
            if (ch == '*' && i + 1 < pattern.Length && pattern[i + 1] == '*')
            {
                i++;
                if (i + 1 < pattern.Length && pattern[i + 1] == '/')
                {
                    expression.Append("(?:.*/)?");
                    i++;
                }
                else { expression.Append(".*"); }
            }
            else { expression.Append(ch switch { '*' => "[^/]*", '?' => "[^/]", _ => Regex.Escape(ch.ToString()) }); }
        }
        expression.Append('$');
        return new(expression.ToString(), RegexOptions.CultureInvariant |
            (windows ? RegexOptions.IgnoreCase : RegexOptions.None), TimeSpan.FromSeconds(1));
    }

    internal static IReadOnlyList<string> Reasons(
        ImageFileSystemEntry left, ImageFileSystemEntry right,
        IReadOnlyDictionary<string, ScannedEntry> oldEntries,
        IReadOnlyDictionary<string, ScannedEntry> newEntries,
        ImageFileSystemSemantics semantics, bool timestamps)
    {
        List<string> reasons = [];
        if (left.Type != right.Type) { reasons.Add("type"); }
        if (!semantics.IsWindows)
        {
            if (left.Mode != right.Mode) { reasons.Add("mode"); }
            if (left.UserId != right.UserId) { reasons.Add("owner"); }
            if (left.GroupId != right.GroupId) { reasons.Add("group"); }
        }
        if (left.Size != right.Size) { reasons.Add("size"); }
        if (left.LinkTarget != right.LinkTarget || left.ContentLinkTarget != right.ContentLinkTarget) { reasons.Add("linkTarget"); }
        if (left.Windows != right.Windows) { reasons.Add("windowsMetadata"); }
        if (timestamps && left.ModifiedTime != right.ModifiedTime) { reasons.Add("modifiedTime"); }
        if (HasContent(left) && HasContent(right))
        {
            string? before = Hash(left, oldEntries), after = Hash(right, newEntries);
            if (before is not null && after is not null && before != after) { reasons.Add("content"); }
        }
        return reasons;
    }

    internal static bool HasContent(ImageFileSystemEntry entry) =>
        entry.Type is ImageFileType.File or ImageFileType.HardLink && entry.ContentLinkTarget is null;

    internal static string? Hash(ImageFileSystemEntry entry, IReadOnlyDictionary<string, ScannedEntry> contents) =>
        contents.TryGetValue(entry.Path, out ScannedEntry? content) ? content.ContentHash : null;

    internal static IReadOnlyList<ExplorerPackageDifference> Packages(
        InstalledPackageMetadata before, InstalledPackageMetadata after)
    {
        List<ExplorerPackageDifference> differences = [];
        bool scoped = before.Coverage.Count > 0 && after.Coverage.Count > 0;
        IReadOnlyList<ComparePackageFact> beforeFacts = scoped ? PackageFacts(before) : [];
        IReadOnlyList<ComparePackageFact> afterFacts = scoped ? PackageFacts(after) : [];
        foreach (InstalledPackageEcosystem ecosystem in Enum.GetValues<InstalledPackageEcosystem>())
        {
            if (before.Ecosystems[ecosystem].Availability != InstalledPackageMetadataAvailability.Available ||
                after.Ecosystems[ecosystem].Availability != InstalledPackageMetadataAvailability.Available)
            {
                continue;
            }
            if (scoped)
            {
                string name = ecosystem.ToString().ToLowerInvariant();
                differences.AddRange(ComparePackages(beforeFacts.Where(p => p.Ecosystem == name),
                    afterFacts.Where(p => p.Ecosystem == name), true)
                    .Select(change => new ExplorerPackageDifference(ecosystem, change.Name,
                        change.Base is null ? null : string.Join(", ", change.Base.Versions),
                        change.Target is null ? null : string.Join(", ", change.Target.Versions))
                    {
                        Scope = change.Scope
                    }));
                continue;
            }
            var old = before.Ecosystems[ecosystem].Packages;
            var current = after.Ecosystems[ecosystem].Packages;
            foreach (string key in old.Keys.Concat(current.Keys).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
            {
                old.TryGetValue(key, out var previous);
                current.TryGetValue(key, out var next);
                if (previous is null || next is null || !previous.SequenceEqual(next))
                {
                    differences.Add(new(ecosystem, key,
                        previous is null ? null : string.Join(", ", previous),
                        next is null ? null : string.Join(", ", next)));
                }
            }
        }
        return differences;
    }

    internal static IReadOnlyList<ComparePackageFact> PackageFacts(InstalledPackageMetadata metadata) =>
        metadata.ScopedPackages.GroupBy(static p => (p.Ecosystem, p.Scope, p.Name))
            .OrderBy(static g => g.Key.Ecosystem).ThenBy(static g => g.Key.Scope, StringComparer.Ordinal).ThenBy(static g => g.Key.Name, StringComparer.Ordinal)
            .Select(static g => new ComparePackageFact(g.Key.Ecosystem.ToString().ToLowerInvariant(), g.Key.Scope, g.Key.Name,
                g.Select(static p => p.Version).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray(),
                g.Select(static p => p.EvidencePath).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray())).ToArray();

    internal static IReadOnlyList<ComparePackageDifference> ComparePackages(
        IEnumerable<ComparePackageFact> before, IEnumerable<ComparePackageFact> after, bool definitive)
    {
        var old = before.ToDictionary(static p => p.Scope + "\0" + p.Name, StringComparer.Ordinal);
        var current = after.ToDictionary(static p => p.Scope + "\0" + p.Name, StringComparer.Ordinal);
        List<ComparePackageDifference> changes = [];
        foreach (string key in old.Keys.Concat(current.Keys).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            old.TryGetValue(key, out var previous);
            current.TryGetValue(key, out var next);
            if (previous is not null && next is not null && previous.Versions.SequenceEqual(next.Versions)) { continue; }
            var fact = previous ?? next!;
            changes.Add(new(fact.Ecosystem, fact.Scope, fact.Name,
                previous is null ? "added" : next is null ? "removed" : "changed",
                previous, next, definitive));
        }
        return changes;
    }
}
