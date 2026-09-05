using System.Text.Json.Serialization;

namespace Valleysoft.Dredge;

[JsonConverter(typeof(JsonStringEnumConverter<CompareDiff>))]
public enum CompareDiff
{
    [JsonStringEnumMemberName("equal")]
    Equal,
    [JsonStringEnumMemberName("notEqual")]
    NotEqual,
    [JsonStringEnumMemberName("added")]
    Added,
    [JsonStringEnumMemberName("removed")]
    Removed
}
