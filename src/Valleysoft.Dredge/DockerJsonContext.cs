using System.Text.Json.Serialization;
using Valleysoft.DockerRegistryClient.Models.Manifests.Docker;

namespace Valleysoft.Dredge;

[JsonSourceGenerationOptions(
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = true)]
[JsonSerializable(typeof(DockerManifest))]
[JsonSerializable(typeof(ManifestList))]
internal partial class DockerJsonContext : JsonSerializerContext
{
}
