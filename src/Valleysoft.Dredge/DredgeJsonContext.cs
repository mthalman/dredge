using System.Text.Json.Serialization;
using Valleysoft.Dredge.Commands.Referrer;
using Valleysoft.DockerRegistryClient.Models.Images;
using Valleysoft.DockerRegistryClient.Models.Manifests.Oci;

namespace Valleysoft.Dredge;

[JsonSourceGenerationOptions(
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = true)]
[JsonSerializable(typeof(AppSettings))]
[JsonSerializable(typeof(CompareLayersResult))]
[JsonSerializable(typeof(CompareMetadataResult))]
[JsonSerializable(typeof(ImageFileSystemEntry[]))]
[JsonSerializable(typeof(LinuxOsInfo))]
[JsonSerializable(typeof(string[]))]
[JsonSerializable(typeof(CheckResult))]
[JsonSerializable(typeof(ArtifactInspection))]
[JsonSerializable(typeof(SpdxSummary))]
[JsonSerializable(typeof(CycloneDxSummary))]
[JsonSerializable(typeof(CycloneDxComponentSummary))]
[JsonSerializable(typeof(InTotoSummary))]
[JsonSerializable(typeof(DsseSummary))]
[JsonSerializable(typeof(NotarySignatureSummary))]
[JsonSerializable(typeof(OciImageIndex), TypeInfoPropertyName = "OciImageIndex")]
[JsonSerializable(typeof(OciImageManifest))]
[JsonSerializable(typeof(OciDescriptor))]
[JsonSerializable(typeof(Image))]
[JsonSerializable(typeof(ImageConfig))]
[JsonSerializable(typeof(Dictionary<string, string>))]
[JsonSerializable(typeof(Dictionary<string, object>))]
[JsonSerializable(typeof(LayerCacheEnvelope))]
[JsonSerializable(typeof(StoredLayerIndex))]
[JsonSerializable(typeof(LayerChanges))]
[JsonSerializable(typeof(ScannedEntry))]
[JsonSerializable(typeof(ImageFileSystemEntry))]
[JsonSerializable(typeof(ImageFileSystem.StoredFileSystem))]
[JsonSerializable(typeof(ImageFileSystem.StoredEntry))]
internal partial class DredgeJsonContext : JsonSerializerContext
{
}
