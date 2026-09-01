namespace Valleysoft.Dredge.Tests;

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Valleysoft.DockerRegistryClient.Models.Manifests;
using Valleysoft.DockerRegistryClient.Models.Manifests.Oci;
using Valleysoft.Dredge.Commands.Referrer;
using ReferrerGetCommand = Valleysoft.Dredge.Commands.Referrer.GetCommand;
using ReferrerInspectCommand = Valleysoft.Dredge.Commands.Referrer.InspectCommand;

public class ReferrerArtifactCommandTests
{
    private const string Registry = "registry.example";
    private const string Repository = "repo";
    private const string SubjectDigest = "sha256:subject";
    private const string ArtifactDigest = "sha256:artifact";

    [Fact]
    public async Task InspectCommand_WritesGenericSummaryWithoutReadingUnknownPayload()
    {
        OciDescriptor payload = CreatePayload("sha256:unknown", "application/example", 42);
        Mock<IDockerRegistryClient> client = CreateClient(CreateArtifact(payload));
        using StringWriter output = new();
        TestInspectCommand command = new(CreateFactory(client.Object), output)
        {
            Options = CreateInspectOptions()
        };

        await command.RunAsync();

        string text = output.ToString();
        Assert.Contains($"Artifact digest: {ArtifactDigest}", text);
        Assert.Contains($"Subject digest: {SubjectDigest}", text);
        Assert.Contains("[0] sha256:unknown", text);
        Assert.Contains("Media type: application/example", text);
        client.Verify(
            instance => instance.Blobs.GetAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task InspectCommand_AcceptsUnknownJsonArray()
    {
        const string content = """[{"value":1}]""";
        Mock<IDockerRegistryClient> client = CreateClient(
            CreateArtifact(CreatePayload("sha256:json", "application/json", content.Length)));
        SetupBlob(client, "sha256:json", content);
        using StringWriter output = new();
        TestInspectCommand command = new(CreateFactory(client.Object), output)
        {
            Options = CreateInspectOptions()
        };

        await command.RunAsync();

        string text = output.ToString();
        Assert.Contains("[0] sha256:json", text);
        Assert.DoesNotContain("Format:", text);
    }

    [Fact]
    public async Task InspectCommand_WritesSpdxSummary()
    {
        const string content = """
            {
              "spdxVersion": "SPDX-2.3",
              "name": "sample",
              "documentNamespace": "https://example.test/spdx",
              "creationInfo": {
                "created": "2026-08-31T00:00:00Z",
                "creators": ["Tool: test"]
              },
              "packages": [{}, {}],
              "files": [{}],
              "relationships": [{}, {}, {}]
            }
            """;
        Mock<IDockerRegistryClient> client = CreateClient(
            CreateArtifact(CreatePayload("sha256:spdx", "application/json", content.Length)));
        SetupBlob(client, "sha256:spdx", content);
        using StringWriter output = new();
        TestInspectCommand command = new(CreateFactory(client.Object), output)
        {
            Options = CreateInspectOptions()
        };

        await command.RunAsync();

        string text = output.ToString();
        Assert.Contains("Format: SPDX", text);
        Assert.Contains("SPDX version: SPDX-2.3", text);
        Assert.Contains("Packages: 2", text);
        Assert.Contains("Files: 1", text);
        Assert.Contains("Relationships: 3", text);
    }

    [Fact]
    public async Task InspectCommand_WritesCycloneDxJsonSummaryAndManifest()
    {
        const string content = """
            {
              "bomFormat": "CycloneDX",
              "specVersion": "1.6",
              "serialNumber": "urn:uuid:test",
              "version": 2,
              "metadata": {
                "timestamp": "2026-08-31T00:00:00Z",
                "component": { "type": "application", "name": "sample", "version": "1.0" }
              },
              "components": [{}, {}],
              "services": [{}],
              "vulnerabilities": [{}]
            }
            """;
        Mock<IDockerRegistryClient> client = CreateClient(
            CreateArtifact(CreatePayload(
                "sha256:cyclonedx",
                "application/vnd.cyclonedx+json",
                content.Length)));
        SetupBlob(client, "sha256:cyclonedx", content);
        using StringWriter output = new();
        TestInspectCommand command = new(CreateFactory(client.Object), output)
        {
            Options = CreateInspectOptions(ArtifactInspectOutput.Json)
        };

        await command.RunAsync();
        using JsonDocument document = JsonDocument.Parse(output.ToString());

        JsonElement root = document.RootElement;
        Assert.Equal(ArtifactDigest, root.GetProperty("artifactDigest").GetString());
        Assert.Equal(
            SubjectDigest,
            root.GetProperty("manifest").GetProperty("subject").GetProperty("digest").GetString());
        JsonElement payload = root.GetProperty("payloads")[0];
        Assert.Equal("CycloneDX", payload.GetProperty("format").GetString());
        Assert.Equal(2, payload.GetProperty("summary").GetProperty("componentCount").GetInt32());
        Assert.Equal(
            "sample",
            payload.GetProperty("summary").GetProperty("component").GetProperty("name").GetString());
    }

    [Fact]
    public async Task InspectCommand_DoesNotApplyArtifactTypeToEveryPayload()
    {
        const string content = """{"bomFormat":"CycloneDX","specVersion":"1.6"}""";
        OciImageManifest manifest = CreateArtifact(
            CreatePayload(
                "sha256:cyclonedx",
                "application/vnd.cyclonedx+json",
                content.Length),
            CreatePayload("sha256:binary", "application/octet-stream", 4));
        manifest.ArtifactType = "application/vnd.cyclonedx+json";
        Mock<IDockerRegistryClient> client = CreateClient(manifest);
        SetupBlob(client, "sha256:cyclonedx", content);
        using StringWriter output = new();
        TestInspectCommand command = new(CreateFactory(client.Object), output)
        {
            Options = CreateInspectOptions(ArtifactInspectOutput.Json)
        };

        await command.RunAsync();
        using JsonDocument document = JsonDocument.Parse(output.ToString());

        JsonElement payloads = document.RootElement.GetProperty("payloads");
        Assert.Equal("CycloneDX", payloads[0].GetProperty("format").GetString());
        Assert.False(payloads[1].TryGetProperty("format", out _));
        client.Verify(
            instance => instance.Blobs.GetAsync(
                Repository,
                "sha256:binary",
                It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task InspectCommand_UsesConfigMediaTypeForLegacyArtifact()
    {
        const string content = """{"bomFormat":"CycloneDX","specVersion":"1.6"}""";
        OciImageManifest manifest = CreateArtifact(
            CreatePayload("sha256:cyclonedx", "application/json", content.Length));
        manifest.ArtifactType = null;
        manifest.Config.MediaType = "application/vnd.cyclonedx+json";
        Mock<IDockerRegistryClient> client = CreateClient(manifest);
        SetupBlob(client, "sha256:cyclonedx", content);
        using StringWriter output = new();
        TestInspectCommand command = new(CreateFactory(client.Object), output)
        {
            Options = CreateInspectOptions(ArtifactInspectOutput.Json)
        };

        await command.RunAsync();
        using JsonDocument document = JsonDocument.Parse(output.ToString());

        JsonElement root = document.RootElement;
        Assert.Equal(
            "application/vnd.cyclonedx+json",
            root.GetProperty("artifactType").GetString());
        Assert.Equal(
            "CycloneDX",
            root.GetProperty("payloads")[0].GetProperty("format").GetString());
    }

    [Fact]
    public async Task InspectCommand_ParsesDsseWrappedInTotoStatement()
    {
        const string statement = """
            {
              "_type": "https://in-toto.io/Statement/v0.1",
              "subject": [{ "name": "sample", "digest": { "sha256": "abc123" } }],
              "predicateType": "https://slsa.dev/provenance/v1",
              "predicate": {
                "buildDefinition": { "buildType": "https://example.test/build" },
                "runDetails": { "builder": { "id": "https://example.test/builder" } }
              }
            }
            """;
        string encodedStatement = Convert.ToBase64String(Encoding.UTF8.GetBytes(statement))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
        string envelope = $$"""
            {
              "payloadType": "application/vnd.in-toto+json",
              "payload": "{{encodedStatement}}",
              "signatures": [{ "sig": "" }]
            }
            """;
        Mock<IDockerRegistryClient> client = CreateClient(
            CreateArtifact(CreatePayload(
                "sha256:attestation",
                "application/vnd.dsse.envelope.v1+json",
                envelope.Length)));
        SetupBlob(client, "sha256:attestation", envelope);
        using StringWriter output = new();
        TestInspectCommand command = new(CreateFactory(client.Object), output)
        {
            Options = CreateInspectOptions(ArtifactInspectOutput.Json)
        };

        await command.RunAsync();
        using JsonDocument document = JsonDocument.Parse(output.ToString());

        JsonElement summary =
            document.RootElement.GetProperty("payloads")[0].GetProperty("summary");
        Assert.Equal("application/vnd.in-toto+json", summary.GetProperty("payloadType").GetString());
        JsonElement parsedStatement = summary.GetProperty("statement");
        Assert.Equal(
            "https://in-toto.io/Statement/v0.1",
            parsedStatement.GetProperty("statementType").GetString());
        Assert.Equal(
            "https://slsa.dev/provenance/v1",
            parsedStatement.GetProperty("predicateType").GetString());
        Assert.Equal(
            "https://example.test/builder",
            parsedStatement.GetProperty("builderId").GetString());
        Assert.Equal(
            "https://example.test/build",
            parsedStatement.GetProperty("buildType").GetString());
        Assert.Equal("sample", parsedStatement.GetProperty("subjectNames")[0].GetString());
    }

    [Fact]
    public async Task InspectCommand_ParsesInTotoStatementV01()
    {
        const string statement = """
            {
              "_type": "https://in-toto.io/Statement/v0.1",
              "subject": [{ "name": "sample", "digest": { "sha256": "abc123" } }],
              "predicateType": "https://spdx.dev/Document",
              "predicate": {}
            }
            """;
        Mock<IDockerRegistryClient> client = CreateClient(
            CreateArtifact(CreatePayload(
                "sha256:statement",
                "application/vnd.in-toto+json",
                statement.Length)));
        SetupBlob(client, "sha256:statement", statement);
        using StringWriter output = new();
        TestInspectCommand command = new(CreateFactory(client.Object), output)
        {
            Options = CreateInspectOptions(ArtifactInspectOutput.Json)
        };

        await command.RunAsync();
        using JsonDocument document = JsonDocument.Parse(output.ToString());

        JsonElement payload = document.RootElement.GetProperty("payloads")[0];
        Assert.Equal("in-toto", payload.GetProperty("format").GetString());
        Assert.Equal(
            "https://in-toto.io/Statement/v0.1",
            payload.GetProperty("summary").GetProperty("statementType").GetString());
    }

    [Fact]
    public async Task InspectCommand_ParsesPredicateSpecificInTotoAttestation()
    {
        const string statement = """
            {
              "_type": "https://in-toto.io/Statement/v1",
              "subject": [{ "name": "sample", "digest": { "sha256": "abc123" } }],
              "predicateType": "https://slsa.dev/provenance/v1",
              "predicate": {}
            }
            """;
        string envelope = $$"""
            {
              "payloadType": "application/vnd.in-toto.provenance+json",
              "payload": "{{Convert.ToBase64String(Encoding.UTF8.GetBytes(statement))}}",
              "signatures": [{ "sig": "" }]
            }
            """;
        Mock<IDockerRegistryClient> client = CreateClient(
            CreateArtifact(CreatePayload(
                "sha256:provenance",
                "application/vnd.in-toto.provenance+dsse",
                envelope.Length)));
        SetupBlob(client, "sha256:provenance", envelope);
        using StringWriter output = new();
        TestInspectCommand command = new(CreateFactory(client.Object), output)
        {
            Options = CreateInspectOptions(ArtifactInspectOutput.Json)
        };

        await command.RunAsync();
        using JsonDocument document = JsonDocument.Parse(output.ToString());

        JsonElement payload = document.RootElement.GetProperty("payloads")[0];
        Assert.Equal("DSSE", payload.GetProperty("format").GetString());
        JsonElement summary = payload.GetProperty("summary");
        Assert.Equal(
            "application/vnd.in-toto.provenance+json",
            summary.GetProperty("payloadType").GetString());
        Assert.Equal(
            "https://slsa.dev/provenance/v1",
            summary.GetProperty("statement").GetProperty("predicateType").GetString());
    }

    [Fact]
    public async Task InspectCommand_RejectsLegacyPredicateSpecificDsseWithNonInTotoPayload()
    {
        string envelope = $$"""
            {
              "payloadType": "application/octet-stream",
              "payload": "{{Convert.ToBase64String([0, 1, 2])}}",
              "signatures": [{ "sig": "" }]
            }
            """;
        OciImageManifest manifest = CreateArtifact(
            CreatePayload("sha256:legacy-provenance", "application/json", envelope.Length));
        manifest.ArtifactType = null;
        manifest.Config.MediaType = "application/vnd.in-toto.provenance+dsse";
        Mock<IDockerRegistryClient> client = CreateClient(manifest);
        SetupBlob(client, "sha256:legacy-provenance", envelope);
        using StringWriter output = new();
        using StringWriter error = new();
        TestInspectCommand command = new(CreateFactory(client.Object), output, error)
        {
            Options = CreateInspectOptions(ArtifactInspectOutput.Json)
        };

        CommandExitException exception = await Assert.ThrowsAsync<CommandExitException>(command.RunAsync);

        Assert.Equal(1, exception.ExitCode);
        Assert.Contains(
            "must use an in-toto JSON payload type",
            error.ToString());
    }

    [Fact]
    public async Task InspectCommand_DoesNotMisclassifyGenericJsonLookalike()
    {
        const string content = """
            {
              "_type": "https://in-toto.io/Statement/v1",
              "predicateType": "https://example.test/predicate",
              "subject": [{ "digest": {} }],
              "payloadType": "application/example",
              "payload": "value"
            }
            """;
        Mock<IDockerRegistryClient> client = CreateClient(
            CreateArtifact(CreatePayload("sha256:lookalike", "application/json", content.Length)));
        SetupBlob(client, "sha256:lookalike", content);
        using StringWriter output = new();
        TestInspectCommand command = new(CreateFactory(client.Object), output)
        {
            Options = CreateInspectOptions(ArtifactInspectOutput.Json)
        };

        await command.RunAsync();
        using JsonDocument document = JsonDocument.Parse(output.ToString());

        JsonElement payload = document.RootElement.GetProperty("payloads")[0];
        Assert.False(payload.TryGetProperty("format", out _));
        Assert.False(payload.TryGetProperty("summary", out _));
    }

    [Fact]
    public async Task InspectCommand_FallsBackToGenericForMalformedDetectedDsse()
    {
        const string content = """
            {
              "payloadType": "application/octet-stream",
              "payload": "not base64!",
              "signatures": [{ "sig": "" }]
            }
            """;
        Mock<IDockerRegistryClient> client = CreateClient(
            CreateArtifact(CreatePayload("sha256:lookalike", "application/json", content.Length)));
        SetupBlob(client, "sha256:lookalike", content);
        using StringWriter output = new();
        TestInspectCommand command = new(CreateFactory(client.Object), output)
        {
            Options = CreateInspectOptions(ArtifactInspectOutput.Json)
        };

        await command.RunAsync();
        using JsonDocument document = JsonDocument.Parse(output.ToString());

        JsonElement payload = document.RootElement.GetProperty("payloads")[0];
        Assert.False(payload.TryGetProperty("format", out _));
        Assert.False(payload.TryGetProperty("summary", out _));
    }

    [Theory]
    [InlineData("application/jose+json", "JWS", false)]
    [InlineData("application/cose", "COSE", false)]
    [InlineData("application/jose+json", "JWS", true)]
    public async Task InspectCommand_RecognizesNotarySignature(
        string envelopeMediaType,
        string expectedEnvelopeFormat,
        bool useLegacyConfigMediaType)
    {
        OciImageManifest manifest = CreateArtifact(
            CreatePayload("sha256:signature", envelopeMediaType, 42));
        if (useLegacyConfigMediaType)
        {
            manifest.ArtifactType = null;
            manifest.Config.MediaType = "application/vnd.cncf.notary.signature";
        }
        else
        {
            manifest.ArtifactType = "application/vnd.cncf.notary.signature";
        }

        Mock<IDockerRegistryClient> client = CreateClient(manifest);
        using StringWriter output = new();
        TestInspectCommand command = new(CreateFactory(client.Object), output)
        {
            Options = CreateInspectOptions(ArtifactInspectOutput.Json)
        };

        await command.RunAsync();
        using JsonDocument document = JsonDocument.Parse(output.ToString());

        JsonElement payload = document.RootElement.GetProperty("payloads")[0];
        Assert.Equal("Notary signature", payload.GetProperty("format").GetString());
        Assert.Equal(
            expectedEnvelopeFormat,
            payload.GetProperty("summary").GetProperty("envelopeFormat").GetString());
    }

    [Fact]
    public async Task InspectCommand_SummarizesDsseWithUnknownBinaryPayload()
    {
        string envelope = $$"""
            {
              "payloadType": "application/octet-stream",
              "payload": "{{Convert.ToBase64String([0, 1, 2, 255])}}",
              "signatures": [{ "keyid": "test", "sig": "dmFsdWU=" }]
            }
            """;
        Mock<IDockerRegistryClient> client = CreateClient(
            CreateArtifact(CreatePayload(
                "sha256:dsse",
                "application/vnd.dsse.envelope.v1+json",
                envelope.Length)));
        SetupBlob(client, "sha256:dsse", envelope);
        using StringWriter output = new();
        TestInspectCommand command = new(CreateFactory(client.Object), output)
        {
            Options = CreateInspectOptions(ArtifactInspectOutput.Json)
        };

        await command.RunAsync();
        using JsonDocument document = JsonDocument.Parse(output.ToString());

        JsonElement payload = document.RootElement.GetProperty("payloads")[0];
        Assert.Equal("DSSE", payload.GetProperty("format").GetString());
        JsonElement summary = payload.GetProperty("summary");
        Assert.Equal("application/octet-stream", summary.GetProperty("payloadType").GetString());
        Assert.Equal(1, summary.GetProperty("signatureCount").GetInt32());
        Assert.False(summary.TryGetProperty("statement", out _));
    }

    [Fact]
    public async Task InspectCommand_RejectsArtifactForDifferentSubject()
    {
        OciImageManifest manifest = CreateArtifact(CreatePayload(
            "sha256:payload",
            "application/example",
            1));
        manifest.Subject!.Digest = "sha256:different";
        Mock<IDockerRegistryClient> client = CreateClient(manifest);
        using StringWriter output = new();
        using StringWriter error = new();
        TestInspectCommand command = new(CreateFactory(client.Object), output, error)
        {
            Options = CreateInspectOptions()
        };

        CommandExitException exception = await Assert.ThrowsAsync<CommandExitException>(command.RunAsync);

        Assert.Equal(1, exception.ExitCode);
        Assert.Contains("references subject 'sha256:different'", error.ToString());
    }

    [Fact]
    public async Task GetCommand_StreamsBinaryPayloadToStandardOutput()
    {
        byte[] content = [0, 1, 2, 127, 128, 255];
        OciDescriptor payload = CreatePayload("sha256:binary", "application/octet-stream", content.Length);
        Mock<IDockerRegistryClient> client = CreateClient(CreateArtifact(payload));
        client
            .Setup(instance => instance.Blobs.GetAsync(
                Repository,
                payload.Digest,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MemoryStream(content));
        using MemoryStream output = new();
        TestGetCommand command = new(CreateFactory(client.Object), output)
        {
            Options = CreateGetOptions()
        };

        await command.RunAsync();

        Assert.Equal(content, output.ToArray());
    }

    [Fact]
    public async Task GetCommand_ReadsEmbeddedPayloadData()
    {
        byte[] content = Encoding.UTF8.GetBytes("embedded");
        OciDescriptor payload = CreatePayload(
            $"sha256:{Convert.ToHexStringLower(SHA256.HashData(content))}",
            "text/plain",
            content.Length);
        payload.Data = Convert.ToBase64String(content);
        Mock<IDockerRegistryClient> client = CreateClient(CreateArtifact(payload));
        using MemoryStream output = new();
        TestGetCommand command = new(CreateFactory(client.Object), output)
        {
            Options = CreateGetOptions()
        };

        await command.RunAsync();

        Assert.Equal(content, output.ToArray());
        client.Verify(
            instance => instance.Blobs.GetAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GetCommand_RejectsEmbeddedPayloadDescriptorMismatch(bool mismatchDigest)
    {
        byte[] content = Encoding.UTF8.GetBytes("embedded");
        string digest = mismatchDigest
            ? $"sha256:{new string('0', 64)}"
            : $"sha256:{Convert.ToHexStringLower(SHA256.HashData(content))}";
        OciDescriptor payload = CreatePayload(
            digest,
            "text/plain",
            mismatchDigest ? content.Length : content.Length + 1);
        payload.Data = Convert.ToBase64String(content);
        Mock<IDockerRegistryClient> client = CreateClient(CreateArtifact(payload));
        using MemoryStream output = new();
        using StringWriter error = new();
        TestGetCommand command = new(CreateFactory(client.Object), output, error)
        {
            Options = CreateGetOptions()
        };

        CommandExitException exception = await Assert.ThrowsAsync<CommandExitException>(command.RunAsync);

        Assert.Equal(1, exception.ExitCode);
        Assert.Contains(
            mismatchDigest ? "does not match payload digest" : "descriptor declares",
            error.ToString());
        Assert.Empty(output.ToArray());
    }

    [Fact]
    public async Task GetCommand_SelectsPayloadByIndex()
    {
        byte[] content = Encoding.UTF8.GetBytes("second");
        OciDescriptor first = CreatePayload("sha256:first", "text/plain", 5);
        OciDescriptor second = CreatePayload("sha256:second", "text/plain", content.Length);
        Mock<IDockerRegistryClient> client = CreateClient(CreateArtifact(first, second));
        client
            .Setup(instance => instance.Blobs.GetAsync(
                Repository,
                second.Digest,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MemoryStream(content));
        using MemoryStream output = new();
        TestGetCommand command = new(CreateFactory(client.Object), output)
        {
            Options = CreateGetOptions("1")
        };

        await command.RunAsync();

        Assert.Equal(content, output.ToArray());
    }

    [Fact]
    public async Task GetCommand_SelectsPayloadByDigest()
    {
        byte[] content = Encoding.UTF8.GetBytes("second");
        OciDescriptor first = CreatePayload("sha256:first", "text/plain", 5);
        OciDescriptor second = CreatePayload("sha256:second", "text/plain", content.Length);
        Mock<IDockerRegistryClient> client = CreateClient(CreateArtifact(first, second));
        client
            .Setup(instance => instance.Blobs.GetAsync(
                Repository,
                second.Digest,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MemoryStream(content));
        using MemoryStream output = new();
        TestGetCommand command = new(CreateFactory(client.Object), output)
        {
            Options = CreateGetOptions(second.Digest)
        };

        await command.RunAsync();

        Assert.Equal(content, output.ToArray());
    }

    [Fact]
    public async Task GetCommand_RequiresSelectorForMultiplePayloads()
    {
        Mock<IDockerRegistryClient> client = CreateClient(CreateArtifact(
            CreatePayload("sha256:first", "text/plain", 5),
            CreatePayload("sha256:second", "text/plain", 6)));
        using MemoryStream output = new();
        using StringWriter error = new();
        TestGetCommand command = new(CreateFactory(client.Object), output, error)
        {
            Options = CreateGetOptions()
        };

        CommandExitException exception = await Assert.ThrowsAsync<CommandExitException>(command.RunAsync);

        Assert.Equal(1, exception.ExitCode);
        Assert.Contains("contains multiple payloads", error.ToString());
    }

    [Fact]
    public async Task GetCommand_WritesPayloadToFile()
    {
        byte[] content = Encoding.UTF8.GetBytes("file output");
        OciDescriptor payload = CreatePayload("sha256:file", "text/plain", content.Length);
        Mock<IDockerRegistryClient> client = CreateClient(CreateArtifact(payload));
        client
            .Setup(instance => instance.Blobs.GetAsync(
                Repository,
                payload.Digest,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MemoryStream(content));
        string path = Path.GetTempFileName();

        try
        {
            using MemoryStream output = new();
            TestGetCommand command = new(CreateFactory(client.Object), output)
            {
                Options = CreateGetOptions(outputPath: path)
            };

            await command.RunAsync();

            Assert.Equal(content, await File.ReadAllBytesAsync(
                path,
                TestContext.Current.CancellationToken));
            Assert.Empty(output.ToArray());
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task ResolveAsync_ResolvesTaggedSubjectDigest()
    {
        OciImageManifest manifest = CreateArtifact(CreatePayload(
            "sha256:payload",
            "application/example",
            1));
        Mock<IDockerRegistryClient> client = CreateClient(manifest);
        client
            .Setup(instance => instance.Manifests.GetDigestAsync(
                Repository,
                "tag",
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(SubjectDigest);

        ResolvedArtifact artifact = await ArtifactHelper.ResolveAsync(
            client.Object,
            ImageName.Parse($"{Registry}/{Repository}:tag"),
            ArtifactDigest,
            TestContext.Current.CancellationToken);

        Assert.Same(manifest, artifact.Manifest);
    }

    private static InspectOptions CreateInspectOptions(
        ArtifactInspectOutput output = ArtifactInspectOutput.Summary) =>
        new()
        {
            Image = $"{Registry}/{Repository}@{SubjectDigest}",
            ArtifactDigest = ArtifactDigest,
            OutputFormat = output
        };

    private static GetOptions CreateGetOptions(
        string? payload = null,
        string? outputPath = null) =>
        new()
        {
            Image = $"{Registry}/{Repository}@{SubjectDigest}",
            ArtifactDigest = ArtifactDigest,
            Payload = payload,
            OutputPath = outputPath
        };

    private static OciImageManifest CreateArtifact(params OciDescriptor[] payloads) =>
        new()
        {
            ArtifactType = "application/example",
            Config = CreatePayload("sha256:config", "application/vnd.oci.empty.v1+json", 2),
            Subject = CreatePayload(
                SubjectDigest,
                "application/vnd.oci.image.manifest.v1+json",
                100),
            Layers = payloads
        };

    private static OciDescriptor CreatePayload(string digest, string mediaType, long size) =>
        new()
        {
            Digest = digest,
            MediaType = mediaType,
            Size = size
        };

    private static Mock<IDockerRegistryClient> CreateClient(OciImageManifest manifest)
    {
        Mock<IDockerRegistryClient> client = new() { DefaultValue = DefaultValue.Mock };
        client
            .Setup(instance => instance.Manifests.GetAsync(
                Repository,
                ArtifactDigest,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ManifestInfo(
                "application/vnd.oci.image.manifest.v1+json",
                ArtifactDigest,
                manifest));
        return client;
    }

    private static void SetupBlob(
        Mock<IDockerRegistryClient> client,
        string digest,
        string content) =>
        client
            .Setup(instance => instance.Blobs.GetAsync(
                Repository,
                digest,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new MemoryStream(Encoding.UTF8.GetBytes(content)));

    private static IDockerRegistryClientFactory CreateFactory(IDockerRegistryClient client)
    {
        Mock<IDockerRegistryClientFactory> factory = new();
        factory.Setup(instance => instance.GetClientAsync(Registry)).ReturnsAsync(client);
        return factory.Object;
    }

    private sealed class TestInspectCommand : ReferrerInspectCommand
    {
        private readonly TextWriter error;

        public TestInspectCommand(
            IDockerRegistryClientFactory factory,
            TextWriter output,
            TextWriter? error = null)
            : base(factory, output)
        {
            this.error = error ?? TextWriter.Null;
        }

        public Task RunAsync() => ExecuteAsync(TestContext.Current.CancellationToken);

        protected override TextWriter Error => error;

        protected override void Exit(int exitCode) => throw new CommandExitException(exitCode);
    }

    private sealed class TestGetCommand : ReferrerGetCommand
    {
        private readonly TextWriter error;

        public TestGetCommand(
            IDockerRegistryClientFactory factory,
            Stream output,
            TextWriter? error = null)
            : base(factory, output)
        {
            this.error = error ?? TextWriter.Null;
        }

        public Task RunAsync() => ExecuteAsync(TestContext.Current.CancellationToken);

        protected override TextWriter Error => error;

        protected override void Exit(int exitCode) => throw new CommandExitException(exitCode);
    }

    private sealed class CommandExitException : Exception
    {
        public CommandExitException(int exitCode)
        {
            ExitCode = exitCode;
        }

        public int ExitCode { get; }
    }
}
