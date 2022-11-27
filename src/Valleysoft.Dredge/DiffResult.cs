using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using System.Runtime.Serialization;

namespace Valleysoft.Dredge;

[JsonConverter(typeof(StringEnumConverter))]
public enum DiffResult
{
    [EnumMember(Value = "Equal")]
    Equal,
    [EnumMember(Value = "Not Equal")]
    NotEqual,
    [EnumMember(Value = "Added")]
    Added,
    [EnumMember(Value = "Removed")]
    Removed
}
