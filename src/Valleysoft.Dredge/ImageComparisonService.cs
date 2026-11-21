using System.Text;
using System.Text.Json;
using Valleysoft.Dredge.Commands.Image;

namespace Valleysoft.Dredge;

internal static class ImageComparisonService
{
    internal static async Task<CompareResult> CompareAsync(
        ResolvedImage baseline, ResolvedImage target, CompareOptions options,
        LayerStore? store, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        HashSet<string> scopes = new(options.Scopes, StringComparer.Ordinal);
        SortedDictionary<string, CompareSection> sections = new(StringComparer.Ordinal);
        List<CompareDiagnostic> diagnostics = [];
        List<CompareFileDifference> files = [];
        List<ComparePackageDifference> packages = [];
        List<ComparePackageCoverage> coverage = [];
        IReadOnlyList<ComparePackageFact> beforePackages = [], afterPackages = [];
        IReadOnlyList<CompareValueDifference> config = [], layers = [];
        List<CompareSize> sizes = [];
        int ignored = 0;
        ExplorerSession? before = null, after = null;
        bool contentIdentity = baseline.IsDaemon || target.IsDaemon;
        bool compatible = ExplorerPlatform.FromConfig(baseline.Configuration, baseline.SelectedPlatform)
            .IsCompatibleWith(ExplorerPlatform.FromConfig(target.Configuration, target.SelectedPlatform));
        try
        {
            foreach (string scope in CompareOptions.AllScopes)
            {
                sections[scope] = new("notRequested", null, 0, 0, 0);
            }
            bool needsFiles = scopes.Overlaps(["files", "packages", "sizes"]);
            if (needsFiles)
            {
                before = await LoadAsync(baseline, "base");
                after = await LoadAsync(target, "target");
            }
            if (scopes.Contains("config"))
            {
                config = ImageMetadataComparison.Compare(baseline, target, false);
                sections["config"] = Section("available", config.Select(static d => d.Kind));
            }
            if (scopes.Contains("layers"))
            {
                layers = ImageMetadataComparison.Compare(baseline, target, true);
                string layerCoverage = "available";
                try
                {
                    if (contentIdentity)
                    {
                        string[] oldIds = await ExplorerContentIdentity.GetVerifiedDigestsAsync(baseline, store!, cancellationToken);
                        string[] newIds = await ExplorerContentIdentity.GetVerifiedDigestsAsync(target, store!, cancellationToken);
                        List<CompareValueDifference> differences = [.. layers.Where(static d =>
                            d.Path != "RootFilesystem.diffIds" && !d.Path.StartsWith("RootFilesystem.diffIds[", StringComparison.Ordinal))];
                        for (int i = 0; i < Math.Max(oldIds.Length, newIds.Length); i++)
                        {
                            string? old = i < oldIds.Length ? oldIds[i] : null;
                            string? current = i < newIds.Length ? newIds[i] : null;
                            if (old != current)
                            {
                                differences.Add(new($"contentLayers[{i}].diffId",
                                    old is null ? "added" : current is null ? "removed" : "changed",
                                    StringElement(old), StringElement(current)));
                            }
                        }
                        layers = differences.OrderBy(static d => d.Path, StringComparer.Ordinal).ToArray();
                    }
                }
                catch (Exception exception) when (exception is NotSupportedException or InvalidDataException or IOException)
                {
                    layerCoverage = "partial";
                    diagnostics.Add(new("layerIdentityUnavailable", "comparison", "layers", null,
                        "Uncompressed layer content identity could not be verified. Available metadata differences are retained."));
                }
                sections["layers"] = Section(layerCoverage, layers.Select(static d => d.Kind));
            }
            if (scopes.Contains("files"))
            {
                string status = AnalysisCoverage("files");
                if (status == "available")
                {
                    var semantics = before!.Semantics;
                    var patterns = options.IgnorePaths.Select(p => ImageComparison.CreateIgnore(p, semantics.IsWindows)).ToArray();
                    foreach (var fact in ImageComparison.CompareFiles(before.Entries, after!.Entries,
                        before.Analysis.LiveContents, after.Analysis.LiveContents, semantics, options.IncludeTimestamps, cancellationToken))
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        string path = fact.Path;
                        var previous = fact.Before;
                        var next = fact.After;
                        if (fact.Incomplete)
                        {
                            status = "partial";
                            diagnostics.Add(new("fileIdentityUnavailable", "comparison", "files", path,
                                "Unsupported entry or unavailable content hash; content equality is indeterminate."));
                        }
                        IReadOnlyList<string> reasons = fact.Reasons;
                        if (reasons.Count == 0) { continue; }
                        if (patterns.Any(pattern => pattern.IsMatch(path))) { ignored++; continue; }
                        files.Add(new(path, previous is null ? "added" : next is null ? "removed" : "changed",
                            reasons, Fact(previous, before), Fact(next, after)));
                    }
                    AddFileDiagnostics(before, "base");
                    AddFileDiagnostics(after, "target");
                    if (before.Files.DiagnosticCount > 0 || after.Files.DiagnosticCount > 0) { status = "partial"; }
                }
                sections["files"] = Section(status, files.Select(static d => d.Kind));
            }
            if (scopes.Contains("packages"))
            {
                string status = AnalysisCoverage("packages");
                if (status == "available")
                {
                    await before!.EnsurePackagesAsync(cancellationToken);
                    await after!.EnsurePackagesAsync(cancellationToken);
                    beforePackages = ImageComparison.PackageFacts(before.Packages);
                    afterPackages = ImageComparison.PackageFacts(after.Packages);
                    foreach (InstalledPackageEcosystem ecosystem in Enum.GetValues<InstalledPackageEcosystem>())
                    {
                        string oldCoverage = AddPackageCoverage(before.Packages, ecosystem, "base");
                        string newCoverage = AddPackageCoverage(after.Packages, ecosystem, "target");
                        bool complete = oldCoverage == "available" && newCoverage == "available" ||
                            oldCoverage == "notDetected" && newCoverage == "notDetected";
                        if (!complete) { status = "partial"; }
                        string name = ecosystem.ToString().ToLowerInvariant();
                        packages.AddRange(ImageComparison.ComparePackages(
                            beforePackages.Where(p => p.Ecosystem == name),
                            afterPackages.Where(p => p.Ecosystem == name), complete));
                        if (!complete)
                        {
                            diagnostics.Add(new("packageCoverageIncomplete", "comparison", "packages", name,
                                $"Inventory coverage: base {oldCoverage}, target {newCoverage}. Observations do not establish definitive absence."));
                        }
                    }
                    foreach (var operand in new[] { (Session: before, Name: "base"), (Session: after, Name: "target") })
                    {
                        foreach (var diagnostic in operand.Session.Packages.Diagnostics)
                        {
                            diagnostics.Add(new("packageMetadataInvalid", operand.Name, "packages", diagnostic.Path,
                                "Package metadata could not be completely parsed."));
                        }
                    }
                    if (scopes.Contains("files"))
                    {
                        var oldAssociations = await AssociationsAsync(before, files.Select(static d => d.Path).ToHashSet(StringComparer.Ordinal), diagnostics, "base", cancellationToken);
                        var newAssociations = await AssociationsAsync(after, files.Select(static d => d.Path).ToHashSet(StringComparer.Ordinal), diagnostics, "target", cancellationToken);
                        files = files.Select(d => d with
                        {
                            BasePackages = oldAssociations.GetValueOrDefault(d.Path) ?? [],
                            TargetPackages = newAssociations.GetValueOrDefault(d.Path) ?? []
                        }).ToList();
                    }
                }
                sections["packages"] = Section(status, packages.Select(static d => d.Kind));
            }
            if (scopes.Contains("sizes"))
            {
                bool registry = !contentIdentity;
                Measure("registryCompressedLayerBytes", baseline.IsDaemon ? null : SumLayers(baseline),
                    target.IsDaemon ? null : SumLayers(target));
                Measure("totalLayerFilePayloadBytes", before?.Analysis.FileBytes, after?.Analysis.FileBytes);
                Measure("effectiveLiveContentBytes", LiveBytes(before), LiveBytes(after));
                Measure("hiddenContentBytes", before?.Analysis.HiddenBytes, after?.Analysis.HiddenBytes);
                long? estimate = registry ? AdditionalBytes(baseline, target) : null;
                Measure("targetOnlyUniqueRegistryBlobBytes", registry ? 0 : null, estimate, true);
                bool partial = sizes.Any(static s => s.Coverage != "available") ||
                    before?.Files.DiagnosticCount > 0 || after?.Files.DiagnosticCount > 0;
                sections["sizes"] = Section(partial ? "partial" : "available",
                    sizes.Where(static s => s.DeltaBytes is not null and not 0).Select(static _ => "changed"));
                if (partial)
                {
                    diagnostics.Add(new("sizeCoverageIncomplete", "comparison", "sizes", null,
                        "Some size measurements are unavailable. Docker exports do not preserve registry compression or download estimates."));
                }
            }
            bool allComplete = sections.Values.All(static s => s.Coverage is "available" or "notRequested" or "notDetected");
            bool equal = sections.Values.All(static s => s.Differences == 0);
            return new(Input(baseline, options.BaseImage), Input(target, options.TargetImage),
                new(CompareOptions.AllScopes.Where(scopes.Contains).ToArray(), options.IgnorePaths,
                    options.IncludeTimestamps, baseline.Configuration.Os.Equals("windows", StringComparison.OrdinalIgnoreCase) ? "windows" : "posix"),
                allComplete, allComplete ? equal : null, !allComplete ? 1 : equal ? 0 : 2,
                sections, diagnostics.OrderBy(static d => d.Section, StringComparer.Ordinal)
                    .ThenBy(static d => d.Operand, StringComparer.Ordinal).ThenBy(static d => d.Path, StringComparer.Ordinal)
                    .ThenBy(static d => d.Code, StringComparer.Ordinal).ToArray(),
                files, packages, coverage, beforePackages, afterPackages, config, layers, sizes, ignored);
        }
        finally
        {
            try { if (after is not null) { await after.Files.DisposeAsync(); } }
            finally { if (before is not null) { await before.Files.DisposeAsync(); } }
        }

        async Task<ExplorerSession?> LoadAsync(ResolvedImage source, string operand)
        {
            try
            {
                var session = await ExplorerSession.CreateAsync(null, new ExplorerSource
                {
                    Source = source,
                    Image = source.Name,
                    Resolved = source.Resolved,
                    Config = source.Configuration,
                    Platforms = [],
                    Platform = ExplorerPlatform.FromConfig(source.Configuration, source.SelectedPlatform)
                }, store!, null, cancellationToken);
                try
                {
                    if (contentIdentity) { await session.EnsureContentIdentitiesAsync(cancellationToken); }
                }
                catch
                {
                    await session.Files.DisposeAsync();
                    throw;
                }
                return session;
            }
            catch (Exception exception) when (exception is NotSupportedException or InvalidDataException or IOException)
            {
                diagnostics.Add(new("analysisUnavailable", operand, "analysis", null,
                    "Effective filesystem analysis is unavailable or invalid; metadata facts remain supported."));
                return null;
            }
        }

        string AnalysisCoverage(string section)
        {
            if (!compatible)
            {
                diagnostics.Add(new("incompatiblePlatforms", "comparison", section, null,
                    "Platform-dependent comparison requires compatible independently resolved platforms."));
                return "incompatible";
            }
            return before is null || after is null ? "unavailable" : "available";
        }

        void AddFileDiagnostics(ExplorerSession session, string operand)
        {
            foreach (var warning in session.Files.Diagnostics)
            {
                diagnostics.Add(new("scannerCoverageIncomplete", operand, "files", warning.Path,
                    "The image scanner reported unsupported or incomplete entry metadata."));
            }
        }

        string AddPackageCoverage(InstalledPackageMetadata metadata, InstalledPackageEcosystem ecosystem, string operand)
        {
            var counts = metadata.Coverage.GetValueOrDefault(ecosystem) ?? new(0, 0, 0);
            string state = metadata.UnavailableReason is not null ? "unavailable" :
                counts.Candidates == 0 ? "notDetected" : counts.Parsed == 0 ? "unavailable" :
                counts.Failed > 0 || counts.SkippedRecords > 0 ? "partial" : "available";
            coverage.Add(new(ecosystem.ToString().ToLowerInvariant(), operand, state, counts.Candidates, counts.Parsed, counts.Failed)
            {
                Evidence = metadata.MetadataSources.Where(p => p.Ecosystem == ecosystem)
                    .Select(static p => new ComparePackageEvidence(p.Path, p.Scope, p.Parsed)).ToArray(),
                SkippedRecords = counts.SkippedRecords
            });
            return state;
        }

        void Measure(string name, long? old, long? current, bool estimate = false) =>
            sizes.Add(new(name, old is null || current is null ? "unavailable" :
                name is "totalLayerFilePayloadBytes" or "effectiveLiveContentBytes" or "hiddenContentBytes" &&
                    (before?.Files.DiagnosticCount > 0 || after?.Files.DiagnosticCount > 0) ? "partial" : "available",
                old, current, old is null || current is null ? null : checked(current - old), estimate));
    }

    private static CompareSection Section(string coverage, IEnumerable<string> kinds)
    {
        string[] items = kinds.ToArray();
        return new(coverage, coverage is "available" or "notDetected" ? items.Length == 0 : null,
            items.Count(static k => k == "added"), items.Count(static k => k == "removed"), items.Count(static k => k == "changed"));
    }

    private static CompareInput Input(ResolvedImage image, string requested) =>
        new(requested, image.IsDaemon ? "docker" : "registry", image.Identity,
            image.IsDaemon ? "dockerImageId" : "registryManifestDigest",
            image.IsDaemon ? image.Identity : image.Resolved.Manifest.Config?.Digest, image.Platform)
        {
            Connection = image.Connection is null ? null : SafeConnection(image.Connection)
        };

    private static string SafeConnection(DockerConnection connection)
    {
        var endpoint = connection.Endpoint;
        if (endpoint.Scheme is "unix" or "npipe") { return connection.Context ?? endpoint.Scheme; }
        return connection.Context is null ? $"{endpoint.Scheme}://{endpoint.Host}:{endpoint.Port}" : connection.Context;
    }

    private static CompareFileFact? Fact(ImageFileSystemEntry? entry, ExplorerSession session)
    {
        if (entry is null) { return null; }
        bool content = session.IsDaemon;
        int layer = entry.ModifiedLayer?.Index ?? entry.IntroducedLayer.Index;
        return new(entry.Type, entry.Size, entry.Mode, entry.UserId, entry.GroupId,
            entry.LinkTarget, entry.ModifiedTime, entry.Windows, ImageComparison.Hash(entry, session.Analysis.LiveContents),
            new(layer, session.LayerIdentity(layer, content), entry.ContentLayerIndex,
                session.LayerIdentity(entry.ContentLayerIndex, content), content ? "uncompressedDiffId" : "registryBlobDigest"))
        {
            BoundLinkTarget = entry.ContentLinkTarget
        };
    }

    private static JsonElement? StringElement(string? value) =>
        value is null ? null : JsonSerializer.SerializeToElement(value, DredgeJsonContext.Default.String);

    private static long SumLayers(ResolvedImage image) => image.Resolved.Manifest.Layers.Sum(static l => l.Size);

    private static long AdditionalBytes(ResolvedImage baseline, ResolvedImage target)
    {
        HashSet<string> known = baseline.Resolved.Manifest.Layers.Select(static l => l.Digest!).ToHashSet(StringComparer.Ordinal);
        return target.Resolved.Manifest.Layers.Where(l => known.Add(l.Digest!)).Sum(static l => l.Size);
    }

    private static long? LiveBytes(ExplorerSession? session) =>
        session is null ? null : session.Entries.Where(ImageComparison.HasContent)
            .DistinctBy(static e => e.ContentId).Sum(static e => e.Size);

    private static async Task<Dictionary<string, IReadOnlyList<ComparePackageAssociation>>> AssociationsAsync(
        ExplorerSession session, HashSet<string> changed, List<CompareDiagnostic> diagnostics,
        string operand, CancellationToken cancellationToken)
    {
        Dictionary<string, List<ComparePackageAssociation>> associations = new(StringComparer.Ordinal);
        HashSet<(InstalledPackageEcosystem Ecosystem, string Scope, string Name)> ownershipSeen = [];
        string[] paths = session.Entries.Select(static e => e.Path).ToArray();
        var records = session.Packages.ScopedPackages.DistinctBy(static p => (p.Ecosystem, p.Scope, p.Name)).ToArray();
        var byName = records.GroupBy(static p => (p.Ecosystem, p.Name)).ToDictionary(static g => g.Key, static g => g.ToArray());
        var npmRoots = records.Where(static p => p.Ecosystem == InstalledPackageEcosystem.Npm)
            .GroupBy(static p => p.Scope, StringComparer.Ordinal).ToDictionary(static g => g.Key, static g => g.ToArray(), StringComparer.Ordinal);
        var pipMetadata = records.Where(static p => p.Ecosystem == InstalledPackageEcosystem.Pip)
            .GroupBy(static p => p.EvidencePath, StringComparer.Ordinal).ToDictionary(static g => g.Key, static g => g.ToArray(), StringComparer.Ordinal);
        string[] requests = paths.Where(static p =>
            p.StartsWith("var/lib/dpkg/info/", StringComparison.Ordinal) && p.EndsWith(".list", StringComparison.Ordinal) ||
            p.EndsWith(".dist-info/RECORD", StringComparison.OrdinalIgnoreCase) || p == "lib/apk/db/installed").ToArray();
        if (changed.Count == 0) { return new(StringComparer.Ordinal); }
        foreach (string path in changed.Order(StringComparer.Ordinal))
        {
            string parent = ImagePath.GetDirectoryName(path);
            while (parent.Length > 0)
            {
                if (npmRoots.TryGetValue(parent, out var owners))
                {
                    if (PackageFileLister.ListNpm([parent], [path]).Count > 0)
                    {
                        foreach (var owner in owners) { Add(owner, [path], "inferredPath"); }
                    }
                    break;
                }
                parent = ImagePath.GetDirectoryName(parent);
            }
        }
        await foreach (var result in session.Files.ReadFilesAsync(requests.Select(static p => (p,
            p == "lib/apk/db/installed" ? InstalledPackageReader.MaxDatabaseManifestBytes : InstalledPackageReader.MaxPackageManifestBytes)), cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (result.Error is not null)
            {
                diagnostics.Add(new("ownershipEvidenceUnavailable", operand, "packages", result.Path, "Recorded package/file ownership evidence is unavailable."));
                continue;
            }
            string content;
            try { content = new UTF8Encoding(false, true).GetString(result.Content!); }
            catch (DecoderFallbackException)
            {
                diagnostics.Add(new("ownershipEvidenceInvalid", operand, "packages", result.Path, "Recorded ownership metadata is not valid UTF-8."));
                continue;
            }
            if (result.Path == "lib/apk/db/installed")
            {
                foreach (var (name, owned) in PackageFileLister.ParseApkOwnership(content))
                {
                    foreach (var owner in byName.GetValueOrDefault((InstalledPackageEcosystem.Apk, name)) ?? [])
                    {
                        Add(owner, owned, "recordedMetadata");
                    }
                }
            }
            else if (result.Path.StartsWith("var/lib/dpkg/info/", StringComparison.Ordinal))
            {
                string name = result.Path["var/lib/dpkg/info/".Length..^".list".Length];
                string[] identity = name.Split(':', 2);
                foreach (var owner in byName.GetValueOrDefault((InstalledPackageEcosystem.Dpkg, identity[0])) ?? [])
                {
                    if (identity.Length == 1 || owner.Scope == "system/" + identity[1])
                    {
                        Add(owner, PackageFileLister.ParseDpkgList(content), "recordedMetadata");
                    }
                }
            }
            else
            {
                string metadata = ImagePath.GetDirectoryName(result.Path) + "/METADATA";
                string root = ImagePath.GetDirectoryName(ImagePath.GetDirectoryName(result.Path));
                foreach (var owner in pipMetadata.GetValueOrDefault(metadata) ?? [])
                {
                    Add(owner, PackageFileLister.ParsePipRecord(content, root), "recordedMetadata");
                }
            }
        }
        if (session.Packages.ScopedPackages.Any(static p => p.Ecosystem == InstalledPackageEcosystem.NuGet))
        {
            diagnostics.Add(new("ownershipNotSupported", operand, "packages", null,
                "NuGet dependency manifests do not establish deployed file ownership."));
        }
        foreach (var group in records.Where(static p => p.Ecosystem is InstalledPackageEcosystem.Dpkg or InstalledPackageEcosystem.Apk or InstalledPackageEcosystem.Pip)
            .Where(p => !ownershipSeen.Contains((p.Ecosystem, p.Scope, p.Name))).GroupBy(static p => p.Ecosystem))
        {
            diagnostics.Add(new("ownershipEvidenceNotDetected", operand, "packages", group.Key.ToString().ToLowerInvariant(),
                $"Recorded ownership metadata was not detected for {group.Count()} observed package scopes."));
        }
        return associations.ToDictionary(static p => p.Key, static p => (IReadOnlyList<ComparePackageAssociation>)p.Value
            .Distinct().OrderBy(static a => a.Ecosystem, StringComparer.Ordinal).ThenBy(static a => a.Scope, StringComparer.Ordinal)
            .ThenBy(static a => a.Name, StringComparer.Ordinal).ToArray(), StringComparer.Ordinal);

        void Add(ScopedInstalledPackage owner, IEnumerable<string> owned, string evidence)
        {
            ownershipSeen.Add((owner.Ecosystem, owner.Scope, owner.Name));
            foreach (string path in owned.Where(changed.Contains))
            {
                if (!associations.TryGetValue(path, out var list)) { associations[path] = list = []; }
                list.Add(new(owner.Ecosystem.ToString().ToLowerInvariant(), owner.Scope, owner.Name, evidence));
            }
        }
    }
}
