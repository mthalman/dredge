using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using System.Runtime.Serialization;

namespace Valleysoft.Dredge;

[JsonConverter(typeof(StringEnumConverter))]
public enum CompareDiff
{
    [EnumMember(Value = "equal")]
    Equal,
    [EnumMember(Value = "notEqual")]
    NotEqual,
    [EnumMember(Value = "added")]
    Added,
    [EnumMember(Value = "removed")]
    Removed
}
