using System.Text.Json.Serialization;

namespace Valleysoft.Dredge;

[JsonSourceGenerationOptions(
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = true)]
[JsonSerializable(typeof(WindowsOsInfo))]
[JsonSerializable(typeof(LayerCacheEnvelope))]
internal partial class DredgeJsonNoNamingContext : JsonSerializerContext
{
}
