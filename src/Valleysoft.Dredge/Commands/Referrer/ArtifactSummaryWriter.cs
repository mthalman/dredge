namespace Valleysoft.Dredge.Commands.Referrer;

internal static class ArtifactSummaryWriter
{
    public static void Write(TextWriter writer, ArtifactInspection inspection)
    {
        writer.WriteLine($"Image: {inspection.Image}");
        writer.WriteLine($"Artifact digest: {inspection.ArtifactDigest}");
        writer.WriteLine($"Artifact type: {inspection.ArtifactType ?? "(not specified)"}");
        writer.WriteLine($"Manifest media type: {inspection.Manifest.MediaType}");
        writer.WriteLine($"Subject digest: {inspection.Manifest.Subject?.Digest}");
        writer.WriteLine(
            $"Config: {inspection.Manifest.Config.Digest} " +
            $"({inspection.Manifest.Config.MediaType}, {inspection.Manifest.Config.Size} bytes)");

        if (inspection.Manifest.Annotations.Count > 0)
        {
            writer.WriteLine("Annotations:");
            foreach ((string key, string value) in inspection.Manifest.Annotations.OrderBy(item => item.Key))
            {
                writer.WriteLine($"  {key}: {value}");
            }
        }

        writer.WriteLine("Payloads:");
        if (inspection.Payloads.Count == 0)
        {
            writer.WriteLine("  (none)");
            return;
        }

        foreach (ArtifactPayloadInspection payload in inspection.Payloads)
        {
            writer.WriteLine($"  [{payload.Index}] {payload.Digest}");
            writer.WriteLine($"      Media type: {payload.MediaType}");
            writer.WriteLine($"      Size: {payload.Size} bytes");
            if (payload.Format is not null)
            {
                writer.WriteLine($"      Format: {payload.Format}");
                WriteFormatSummary(writer, payload.Summary);
            }
        }
    }

    private static void WriteFormatSummary(TextWriter writer, object? summary)
    {
        switch (summary)
        {
            case SpdxSummary spdx:
                WriteValue(writer, "SPDX version", spdx.SpdxVersion);
                WriteValue(writer, "Document name", spdx.Name);
                WriteValue(writer, "Document namespace", spdx.DocumentNamespace);
                WriteValue(writer, "Data license", spdx.DataLicense);
                WriteValue(writer, "Created", spdx.Created);
                foreach (string creator in spdx.Creators)
                {
                    writer.WriteLine($"      Creator: {creator}");
                }
                writer.WriteLine($"      Packages: {spdx.PackageCount}");
                writer.WriteLine($"      Files: {spdx.FileCount}");
                writer.WriteLine($"      Relationships: {spdx.RelationshipCount}");
                break;

            case CycloneDxSummary cycloneDx:
                WriteValue(writer, "Spec version", cycloneDx.SpecVersion);
                WriteValue(writer, "Serial number", cycloneDx.SerialNumber);
                if (cycloneDx.Version is not null)
                {
                    writer.WriteLine($"      Document version: {cycloneDx.Version}");
                }
                WriteValue(writer, "Timestamp", cycloneDx.Timestamp);
                if (cycloneDx.Component is not null)
                {
                    string component = string.Join(
                        " ",
                        new[]
                        {
                            cycloneDx.Component.Type,
                            cycloneDx.Component.Name,
                            cycloneDx.Component.Version
                        }.Where(value => !string.IsNullOrEmpty(value)));
                    WriteValue(writer, "Metadata component", component);
                }
                writer.WriteLine($"      Components: {cycloneDx.ComponentCount}");
                writer.WriteLine($"      Services: {cycloneDx.ServiceCount}");
                writer.WriteLine($"      Vulnerabilities: {cycloneDx.VulnerabilityCount}");
                break;

            case InTotoSummary inToto:
                WriteInTotoSummary(writer, inToto);
                break;

            case DsseSummary dsse:
                WriteValue(writer, "Payload type", dsse.PayloadType);
                writer.WriteLine($"      Signatures: {dsse.SignatureCount}");
                if (dsse.Statement is not null)
                {
                    WriteInTotoSummary(writer, dsse.Statement);
                }
                break;

            case NotarySignatureSummary notary:
                WriteValue(writer, "Envelope format", notary.EnvelopeFormat);
                break;
        }
    }

    private static void WriteInTotoSummary(TextWriter writer, InTotoSummary inToto)
    {
        WriteValue(writer, "Statement type", inToto.StatementType);
        WriteValue(writer, "Predicate type", inToto.PredicateType);
        WriteValue(writer, "Builder ID", inToto.BuilderId);
        WriteValue(writer, "Build type", inToto.BuildType);
        writer.WriteLine($"      Subjects: {inToto.SubjectCount}");
        foreach (string subjectName in inToto.SubjectNames)
        {
            writer.WriteLine($"        {subjectName}");
        }
    }

    private static void WriteValue(TextWriter writer, string label, string? value)
    {
        if (!string.IsNullOrEmpty(value))
        {
            writer.WriteLine($"      {label}: {value}");
        }
    }
}
