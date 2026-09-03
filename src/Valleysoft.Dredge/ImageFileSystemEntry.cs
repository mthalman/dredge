using Newtonsoft.Json;
using Newtonsoft.Json.Converters;

namespace Valleysoft.Dredge;

[JsonConverter(typeof(StringEnumConverter))]
public enum ImageFileType
{
    File,
    Directory,
    SymbolicLink,
    HardLink,
    Other
}

public sealed record ImageLayerReference(int Index, string Digest);

public sealed record ImageFileSystemEntry
{
    public required string Path { get; init; }
    public required ImageFileType Type { get; init; }
    public int Mode { get; init; }
    public int UserId { get; init; }
    public int GroupId { get; init; }
    public long Size { get; init; }
    public DateTime? ModifiedTime { get; init; }
    public string? LinkTarget { get; init; }
    public required ImageLayerReference IntroducedLayer { get; init; }
    public ImageLayerReference? ModifiedLayer { get; init; }
    public ImageLayerReference? DeletedLayer { get; init; }

    [JsonIgnore]
    internal int ContentLayerIndex { get; init; }

    [JsonIgnore]
    internal string? ContentPath { get; init; }

    [JsonIgnore]
    // The ordinal disambiguates duplicate paths in non-conforming layers so content reads
    // reopen the exact entry that produced the index metadata.
    internal int ContentEntryIndex { get; init; }

    [JsonIgnore]
    internal string? ContentLinkTarget { get; init; }

    [JsonIgnore]
    internal bool IsDeleted => DeletedLayer is not null;
}
