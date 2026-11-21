using System.Text.Json;
using System.Text.Json.Serialization;

namespace Valleysoft.Dredge;

public sealed record CompareInput(
    string Requested, string Source, string Identity, string IdentityKind,
    string? ConfigIdentity, string Platform)
{
    public string ConfigIdentityKind => Source == "docker" ? "dockerImageId" : "registryConfigDigest";
    public string? Connection { get; init; }
}

public sealed record CompareNormalization(
    IReadOnlyList<string> Scopes, IReadOnlyList<string> IgnorePaths,
    bool IncludeTimestamps, string PathSemantics);

public sealed record CompareDiagnostic(
    string Code, string Operand, string Section, string? Path, string Message);

public sealed record CompareSection(
    string Coverage, [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] bool? AreEqual, int Added, int Removed, int Changed)
{
    public int Differences => Added + Removed + Changed;
}

public sealed record CompareProvenance(
    int EntryLayer, string? EntryIdentity, int ContentLayer, string? ContentIdentity,
    string IdentityKind);

public sealed record CompareFileFact(
    ImageFileType Type, long Size, int Mode, int UserId, int GroupId,
    string? LinkTarget, DateTime? ModifiedTime, WindowsFileMetadata? Windows,
    string? ContentHash, CompareProvenance Provenance)
{
    public string? BoundLinkTarget { get; init; }
}

public sealed record ComparePackageAssociation(
    string Ecosystem, string Scope, string Name, string Evidence);

public sealed record CompareFileDifference(
    string Path, string Kind, IReadOnlyList<string> Reasons,
    CompareFileFact? Base, CompareFileFact? Target)
{
    public IReadOnlyList<ComparePackageAssociation> BasePackages { get; init; } = [];
    public IReadOnlyList<ComparePackageAssociation> TargetPackages { get; init; } = [];
}

public sealed record ComparePackageFact(
    string Ecosystem, string Scope, string Name, IReadOnlyList<string> Versions,
    IReadOnlyList<string> EvidencePaths);

public sealed record ComparePackageCoverage(
    string Ecosystem, string Operand, string Coverage, int Candidates, int Parsed, int Failed)
{
    public IReadOnlyList<ComparePackageEvidence> Evidence { get; init; } = [];
    public int SkippedRecords { get; init; }
}

public sealed record ComparePackageEvidence(string Path, string Scope, bool Parsed);

public sealed record ComparePackageDifference(
    string Ecosystem, string Scope, string Name, string Kind,
    ComparePackageFact? Base, ComparePackageFact? Target, bool Definitive);

public sealed record CompareValueDifference(
    string Path, string Kind, JsonElement? Base, JsonElement? Target);

public sealed record CompareSize(
    string Name, string Coverage, long? BaseBytes, long? TargetBytes, long? DeltaBytes,
    bool Estimate);

public sealed record CompareResult(
    CompareInput Base, CompareInput Target, CompareNormalization Options,
    bool Complete, [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] bool? AreEqual, int ExitCode,
    IReadOnlyDictionary<string, CompareSection> Sections,
    IReadOnlyList<CompareDiagnostic> Diagnostics,
    IReadOnlyList<CompareFileDifference> Files,
    IReadOnlyList<ComparePackageDifference> Packages,
    IReadOnlyList<ComparePackageCoverage> PackageCoverage,
    IReadOnlyList<ComparePackageFact> BasePackages,
    IReadOnlyList<ComparePackageFact> TargetPackages,
    IReadOnlyList<CompareValueDifference> Config,
    IReadOnlyList<CompareValueDifference> Layers,
    IReadOnlyList<CompareSize> Sizes,
    int IgnoredFileDifferences)
{
    public int SchemaVersion => 1;
    [JsonIgnore]
    public int DifferenceCount => Sections.Values.Sum(static section => section.Differences);
}
