using System.Text.Json;
using System.Text.Json.Serialization;
using Valleysoft.DockerRegistryClient.Models.Manifests.Oci;

namespace Valleysoft.Dredge.Commands.Referrer;

internal sealed record ArtifactInspection(
    string Image,
    string ArtifactDigest,
    string? ArtifactType,
    OciImageManifest Manifest,
    IReadOnlyList<ArtifactPayloadInspection> Payloads);

internal sealed record ArtifactPayloadInspection(
    int Index,
    string Digest,
    string MediaType,
    long Size,
    string? Format,
    object? Summary);

internal sealed record SpdxSummary(
    string? SpdxVersion,
    string? Name,
    string? DocumentNamespace,
    string? DataLicense,
    string? Created,
    IReadOnlyList<string> Creators,
    int PackageCount,
    int FileCount,
    int RelationshipCount);

internal sealed record CycloneDxSummary(
    string? SpecVersion,
    string? SerialNumber,
    int? Version,
    string? Timestamp,
    CycloneDxComponentSummary? Component,
    int ComponentCount,
    int ServiceCount,
    int VulnerabilityCount);

internal sealed record CycloneDxComponentSummary(
    string? Type,
    string? Name,
    string? Version);

internal sealed record InTotoSummary(
    string? StatementType,
    string? PredicateType,
    int SubjectCount,
    IReadOnlyList<string> SubjectNames,
    string? BuilderId,
    string? BuildType);

internal sealed record DsseSummary(
    string? PayloadType,
    int SignatureCount,
    InTotoSummary? Statement);

internal sealed record NotarySignatureSummary(string EnvelopeFormat);

internal static class ArtifactInspectionFactory
{
    private const string InTotoMediaType = "application/vnd.in-toto";
    private const string NotarySignatureMediaType = "application/vnd.cncf.notary.signature";
    private const string SpdxFormat = "SPDX";
    private const string CycloneDxFormat = "CycloneDX";
    private const string InTotoFormat = "in-toto";
    private const string DsseFormat = "DSSE";
    private const string NotarySignatureFormat = "Notary signature";
    private const string InTotoStatementTypeV01 = "https://in-toto.io/Statement/v0.1";
    private const string InTotoStatementTypeV1 = "https://in-toto.io/Statement/v1";

    public static async Task<ArtifactInspection> CreateAsync(
        IDockerRegistryClient client,
        ResolvedArtifact artifact,
        CancellationToken cancellationToken)
    {
        List<ArtifactPayloadInspection> payloads = [];
        string effectiveArtifactType =
            artifact.Manifest.ArtifactType ?? artifact.Manifest.Config.MediaType;

        for (int index = 0; index < artifact.Manifest.Layers.Length; index++)
        {
            OciDescriptor descriptor = artifact.Manifest.Layers[index];
            object? summary = null;
            string classificationMediaType = descriptor.MediaType;
            string? format = GetFormatByMediaType(classificationMediaType);
            if (artifact.Manifest.Layers.Length == 1 &&
                effectiveArtifactType.Equals(
                    NotarySignatureMediaType,
                    StringComparison.OrdinalIgnoreCase) &&
                GetNotaryEnvelopeFormat(descriptor.MediaType) is string envelopeFormat)
            {
                format = NotarySignatureFormat;
                summary = new NotarySignatureSummary(envelopeFormat);
            }
            else if (format is null &&
                artifact.Manifest.Layers.Length == 1 &&
                IsGenericJsonMediaType(descriptor.MediaType))
            {
                classificationMediaType = effectiveArtifactType;
                format = GetFormatByMediaType(classificationMediaType);
            }

            bool requireInTotoStatement =
                IsPredicateSpecificInTotoMediaType(classificationMediaType, "+dsse");

            if (summary is null &&
                (format is not null ||
                 IsJsonMediaType(descriptor.MediaType)))
            {
                await using Stream stream = await ArtifactHelper.OpenPayloadAsync(
                    client,
                    artifact.Image.Repo,
                    descriptor,
                    cancellationToken);
                (format, summary) = await ParseSummaryAsync(
                    format,
                    requireInTotoStatement,
                    stream,
                    cancellationToken);
            }

            payloads.Add(new ArtifactPayloadInspection(
                index,
                descriptor.Digest,
                descriptor.MediaType,
                descriptor.Size,
                format,
                summary));
        }

        return new ArtifactInspection(
            artifact.Image.ToString(),
            artifact.ManifestInfo.DockerContentDigest,
            effectiveArtifactType,
            artifact.Manifest,
            payloads);
    }

    private static string? GetFormatByMediaType(string mediaType)
    {
        if (mediaType.Equals("application/spdx+json", StringComparison.OrdinalIgnoreCase) ||
            mediaType.Equals("application/vnd.spdx+json", StringComparison.OrdinalIgnoreCase))
        {
            return SpdxFormat;
        }

        if (mediaType.Equals(
            "application/vnd.cyclonedx+json",
            StringComparison.OrdinalIgnoreCase))
        {
            return CycloneDxFormat;
        }

        if (IsInTotoStatementMediaType(mediaType))
        {
            return InTotoFormat;
        }

        if (mediaType.Equals(
            "application/vnd.dsse.envelope.v1+json",
            StringComparison.OrdinalIgnoreCase) ||
            IsPredicateSpecificInTotoMediaType(mediaType, "+dsse"))
        {
            return DsseFormat;
        }

        return null;
    }

    private static bool IsInTotoStatementMediaType(string mediaType) =>
        mediaType.Equals(
            $"{InTotoMediaType}+json",
            StringComparison.OrdinalIgnoreCase) ||
        IsPredicateSpecificInTotoMediaType(mediaType, "+json");

    private static bool IsPredicateSpecificInTotoMediaType(string mediaType, string suffix)
    {
        string prefix = $"{InTotoMediaType}.";
        return mediaType.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
            mediaType.EndsWith(suffix, StringComparison.OrdinalIgnoreCase) &&
            mediaType.Length > prefix.Length + suffix.Length;
    }

    private static bool IsGenericJsonMediaType(string mediaType) =>
        mediaType.Equals("application/json", StringComparison.OrdinalIgnoreCase);

    private static string? GetNotaryEnvelopeFormat(string mediaType)
    {
        if (mediaType.Equals("application/jose+json", StringComparison.OrdinalIgnoreCase))
        {
            return "JWS";
        }

        return mediaType.Equals("application/cose", StringComparison.OrdinalIgnoreCase)
            ? "COSE"
            : null;
    }

    private static bool IsJsonMediaType(string? mediaType) =>
        mediaType is not null &&
        (IsGenericJsonMediaType(mediaType) ||
         mediaType.EndsWith("+json", StringComparison.OrdinalIgnoreCase));

    private static async Task<(string? Format, object? Summary)> ParseSummaryAsync(
        string? advertisedFormat,
        bool requireInTotoStatement,
        Stream stream,
        CancellationToken cancellationToken)
    {
        try
        {
            using JsonDocument document = await JsonDocument.ParseAsync(
                stream,
                cancellationToken: cancellationToken);

            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                if (advertisedFormat is not null)
                {
                    throw new InvalidDataException(
                        $"Payload advertised as {advertisedFormat} does not contain a JSON object.");
                }

                return (null, null);
            }

            string? format = advertisedFormat ?? DetectFormat(document.RootElement);
            object? summary = format switch
            {
                SpdxFormat => ParseSpdx(document.RootElement),
                CycloneDxFormat => ParseCycloneDx(document.RootElement),
                InTotoFormat => ParseInToto(document.RootElement),
                DsseFormat => ParseDsse(document.RootElement, requireInTotoStatement),
                _ => null
            };

            return (format, summary);
        }
        catch (JsonException ex) when (advertisedFormat is not null)
        {
            throw new InvalidDataException(
                $"Payload advertised as {advertisedFormat} does not contain valid JSON.",
                ex);
        }
        catch (JsonException)
        {
            return (null, null);
        }
        catch (InvalidDataException) when (advertisedFormat is null)
        {
            return (null, null);
        }
    }

    private static string? DetectFormat(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (root.TryGetProperty("spdxVersion", out _))
        {
            return SpdxFormat;
        }

        if (string.Equals(
            GetString(root, "bomFormat"),
            "CycloneDX",
            StringComparison.OrdinalIgnoreCase))
        {
            return CycloneDxFormat;
        }

        if (IsInTotoStatement(root))
        {
            return InTotoFormat;
        }

        if (IsDsseEnvelope(root))
        {
            return DsseFormat;
        }

        return null;
    }

    private static SpdxSummary ParseSpdx(JsonElement root) =>
        new(
            GetString(root, "spdxVersion"),
            GetString(root, "name"),
            GetString(root, "documentNamespace"),
            GetString(root, "dataLicense"),
            GetNestedString(root, "creationInfo", "created"),
            GetNestedStringArray(root, "creationInfo", "creators"),
            GetArrayLength(root, "packages"),
            GetArrayLength(root, "files"),
            GetArrayLength(root, "relationships"));

    private static CycloneDxSummary ParseCycloneDx(JsonElement root)
    {
        CycloneDxComponentSummary? component = null;
        if (TryGetObject(root, "metadata", out JsonElement metadata) &&
            TryGetObject(metadata, "component", out JsonElement componentElement))
        {
            component = new CycloneDxComponentSummary(
                GetString(componentElement, "type"),
                GetString(componentElement, "name"),
                GetString(componentElement, "version"));
        }

        return new CycloneDxSummary(
            GetString(root, "specVersion"),
            GetString(root, "serialNumber"),
            GetInt32(root, "version"),
            GetNestedString(root, "metadata", "timestamp"),
            component,
            GetArrayLength(root, "components"),
            GetArrayLength(root, "services"),
            GetArrayLength(root, "vulnerabilities"));
    }

    private static InTotoSummary ParseInToto(JsonElement root)
    {
        if (!IsInTotoStatement(root))
        {
            throw new InvalidDataException(
                "The in-toto payload is not a valid v0.1 or v1 Statement.");
        }

        return new InTotoSummary(
            GetString(root, "_type"),
            GetString(root, "predicateType"),
            GetArrayLength(root, "subject"),
            GetObjectArrayStrings(root, "subject", "name"),
            GetNestedString(root, "predicate", "builder", "id") ??
                GetNestedString(root, "predicate", "runDetails", "builder", "id"),
            GetNestedString(root, "predicate", "buildType") ??
                GetNestedString(root, "predicate", "buildDefinition", "buildType"));
    }

    private static DsseSummary ParseDsse(JsonElement root, bool requireInTotoStatement)
    {
        byte[] payload = ValidateDsseEnvelope(root);
        string payloadType = GetString(root, "payloadType")!;
        InTotoSummary? statement = null;

        if (IsInTotoStatementMediaType(payloadType))
        {
            try
            {
                using JsonDocument statementDocument = JsonDocument.Parse(payload);
                statement = ParseInToto(statementDocument.RootElement);
            }
            catch (JsonException ex)
            {
                throw new InvalidDataException(
                    "The DSSE envelope payload does not contain a valid JSON statement.",
                    ex);
            }
        }
        else if (requireInTotoStatement)
        {
            throw new InvalidDataException(
                $"A predicate-specific in-toto DSSE envelope must use an in-toto JSON payload type, not '{payloadType}'.");
        }

        return new DsseSummary(
            payloadType,
            GetArrayLength(root, "signatures"),
            statement);
    }

    private static byte[] ValidateDsseEnvelope(JsonElement root)
    {
        if (!HasStringProperty(root, "payloadType") ||
            !HasStringProperty(root, "payload") ||
            !root.TryGetProperty("signatures", out JsonElement signatures) ||
            signatures.ValueKind != JsonValueKind.Array ||
            signatures.GetArrayLength() == 0)
        {
            throw new InvalidDataException(
                "The DSSE envelope must define string payloadType and payload fields " +
                "and at least one signature.");
        }

        byte[] payload = DecodeBase64(GetString(root, "payload")!, "payload");
        foreach (JsonElement signature in signatures.EnumerateArray())
        {
            if (signature.ValueKind != JsonValueKind.Object ||
                !HasStringProperty(signature, "sig"))
            {
                throw new InvalidDataException(
                    "Each DSSE signature must define a string 'sig' field.");
            }

            DecodeBase64(GetString(signature, "sig")!, "signature");
            if (signature.TryGetProperty("keyid", out JsonElement keyId) &&
                keyId.ValueKind != JsonValueKind.String)
            {
                throw new InvalidDataException(
                    "A DSSE signature's optional 'keyid' field must be a string.");
            }
        }

        return payload;
    }

    private static bool IsInTotoStatement(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        string? statementType = GetString(root, "_type");
        if ((!string.Equals(statementType, InTotoStatementTypeV01, StringComparison.Ordinal) &&
                !string.Equals(statementType, InTotoStatementTypeV1, StringComparison.Ordinal)) ||
            string.IsNullOrEmpty(GetString(root, "predicateType")) ||
            !root.TryGetProperty("subject", out JsonElement subjects) ||
            subjects.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        return subjects.EnumerateArray().All(subject =>
        {
            if (subject.ValueKind != JsonValueKind.Object ||
                !subject.TryGetProperty("digest", out JsonElement digest) ||
                digest.ValueKind != JsonValueKind.Object ||
                !digest.EnumerateObject().Any())
            {
                return false;
            }

            return digest.EnumerateObject().All(
                property => property.Value.ValueKind == JsonValueKind.String);
        });
    }

    private static bool IsDsseEnvelope(JsonElement root) =>
        HasStringProperty(root, "payloadType") &&
        HasStringProperty(root, "payload") &&
        root.TryGetProperty("signatures", out JsonElement signatures) &&
        signatures.ValueKind == JsonValueKind.Array &&
        signatures.GetArrayLength() > 0;

    private static bool HasStringProperty(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out JsonElement property) &&
        property.ValueKind == JsonValueKind.String;

    private static byte[] DecodeBase64(string value, string field)
    {
        string normalized = value.Replace('-', '+').Replace('_', '/');
        normalized = (normalized.Length % 4) switch
        {
            0 => normalized,
            2 => normalized + "==",
            3 => normalized + "=",
            _ => throw new InvalidDataException(
                $"The DSSE envelope {field} is not valid base64.")
        };

        try
        {
            return Convert.FromBase64String(normalized);
        }
        catch (FormatException ex)
        {
            throw new InvalidDataException(
                $"The DSSE envelope {field} is not valid base64.",
                ex);
        }
    }

    private static string? GetString(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out JsonElement property) &&
        property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;

    private static int? GetInt32(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out JsonElement property) &&
        property.TryGetInt32(out int value)
            ? value
            : null;

    private static string? GetNestedString(
        JsonElement element,
        string objectName,
        string propertyName) =>
        TryGetObject(element, objectName, out JsonElement nested)
            ? GetString(nested, propertyName)
            : null;

    private static string? GetNestedString(
        JsonElement element,
        string firstObjectName,
        string secondObjectName,
        string propertyName) =>
        TryGetObject(element, firstObjectName, out JsonElement first) &&
        TryGetObject(first, secondObjectName, out JsonElement second)
            ? GetString(second, propertyName)
            : null;

    private static string? GetNestedString(
        JsonElement element,
        string firstObjectName,
        string secondObjectName,
        string thirdObjectName,
        string propertyName) =>
        TryGetObject(element, firstObjectName, out JsonElement first) &&
        TryGetObject(first, secondObjectName, out JsonElement second) &&
        TryGetObject(second, thirdObjectName, out JsonElement third)
            ? GetString(third, propertyName)
            : null;

    private static IReadOnlyList<string> GetNestedStringArray(
        JsonElement element,
        string objectName,
        string propertyName) =>
        TryGetObject(element, objectName, out JsonElement nested)
            ? GetStringArray(nested, propertyName)
            : [];

    private static IReadOnlyList<string> GetStringArray(
        JsonElement element,
        string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out JsonElement property) ||
            property.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return property.EnumerateArray()
            .Where(item => item.ValueKind == JsonValueKind.String)
            .Select(item => item.GetString()!)
            .ToArray();
    }

    private static IReadOnlyList<string> GetObjectArrayStrings(
        JsonElement element,
        string arrayName,
        string propertyName)
    {
        if (!element.TryGetProperty(arrayName, out JsonElement array) ||
            array.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return array.EnumerateArray()
            .Where(item => item.ValueKind == JsonValueKind.Object)
            .Select(item => GetString(item, propertyName))
            .Where(value => value is not null)
            .Select(value => value!)
            .ToArray();
    }

    private static int GetArrayLength(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out JsonElement property) &&
        property.ValueKind == JsonValueKind.Array
            ? property.GetArrayLength()
            : 0;

    private static bool TryGetObject(
        JsonElement element,
        string propertyName,
        out JsonElement value)
    {
        if (element.TryGetProperty(propertyName, out value) &&
            value.ValueKind == JsonValueKind.Object)
        {
            return true;
        }

        value = default;
        return false;
    }
}

internal static class ArtifactInspectionJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };
}
