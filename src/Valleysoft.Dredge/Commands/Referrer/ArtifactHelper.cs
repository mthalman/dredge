using System.Security.Cryptography;
using Valleysoft.DockerRegistryClient.Models.Manifests;
using Valleysoft.DockerRegistryClient.Models.Manifests.Oci;

namespace Valleysoft.Dredge.Commands.Referrer;

internal static class ArtifactHelper
{
    public static async Task<ResolvedArtifact> ResolveAsync(
        IDockerRegistryClient client,
        ImageName imageName,
        string artifactDigest,
        CancellationToken cancellationToken)
    {
        string subjectDigest = imageName.Digest ??
            await client.Manifests.GetDigestAsync(imageName.Repo, imageName.Tag!, cancellationToken);
        ManifestInfo manifestInfo =
            await client.Manifests.GetAsync(imageName.Repo, artifactDigest, cancellationToken);

        if (manifestInfo.Manifest is not OciImageManifest manifest)
        {
            throw new NotSupportedException(
                $"Manifest '{artifactDigest}' is not an OCI artifact manifest.");
        }

        if (manifest.Subject is null)
        {
            throw new InvalidOperationException(
                $"Artifact manifest '{artifactDigest}' does not define a subject.");
        }

        if (!string.Equals(manifest.Subject.Digest, subjectDigest, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Artifact manifest '{artifactDigest}' references subject '{manifest.Subject.Digest}', " +
                $"not '{subjectDigest}'.");
        }

        return new ResolvedArtifact(imageName, manifestInfo, manifest);
    }

    public static OciDescriptor SelectPayload(
        IReadOnlyList<OciDescriptor> payloads,
        string? selector)
    {
        if (payloads.Count == 0)
        {
            throw new InvalidOperationException("The artifact manifest does not contain any payloads.");
        }

        if (selector is null)
        {
            if (payloads.Count == 1)
            {
                return payloads[0];
            }

            throw new InvalidOperationException(
                "The artifact contains multiple payloads. Use 'referrer inspect' to list them, " +
                "then specify one with '--payload <index-or-digest>'.");
        }

        if (int.TryParse(selector, out int index))
        {
            if (index >= 0 && index < payloads.Count)
            {
                return payloads[index];
            }

            throw new ArgumentOutOfRangeException(
                nameof(selector),
                selector,
                $"Payload index must be between 0 and {payloads.Count - 1}.");
        }

        OciDescriptor? payload = payloads.FirstOrDefault(
            candidate => string.Equals(candidate.Digest, selector, StringComparison.OrdinalIgnoreCase));
        return payload ?? throw new ArgumentException(
            $"The artifact does not contain a payload with digest '{selector}'.",
            nameof(selector));
    }

    public static Task<Stream> OpenPayloadAsync(
        IDockerRegistryClient client,
        string repository,
        OciDescriptor payload,
        CancellationToken cancellationToken)
    {
        if (payload.Data is not null)
        {
            byte[] content;
            try
            {
                content = Convert.FromBase64String(payload.Data);
            }
            catch (FormatException ex)
            {
                throw new InvalidDataException(
                    $"Embedded data for payload '{payload.Digest}' is not valid base64.",
                    ex);
            }

            if (content.LongLength != payload.Size)
            {
                throw new InvalidDataException(
                    $"Embedded data for payload '{payload.Digest}' has size {content.LongLength}, " +
                    $"but its descriptor declares {payload.Size}.");
            }

            VerifyDigest(content, payload.Digest);
            return Task.FromResult<Stream>(new MemoryStream(content, writable: false));
        }

        return client.Blobs.GetAsync(repository, payload.Digest, cancellationToken);
    }

    private static void VerifyDigest(byte[] content, string digest)
    {
        int separatorIndex = digest.IndexOf(':');
        if (separatorIndex < 1 || separatorIndex == digest.Length - 1)
        {
            throw new InvalidDataException($"Payload digest '{digest}' is not valid.");
        }

        string algorithm = digest[..separatorIndex];
        byte[]? hash = algorithm.ToLowerInvariant() switch
        {
            "sha256" => SHA256.HashData(content),
            "sha512" => SHA512.HashData(content),
            _ => null
        };

        if (hash is not null &&
            !Convert.ToHexString(hash).Equals(
                digest[(separatorIndex + 1)..],
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                $"Embedded data does not match payload digest '{digest}'.");
        }
    }
}

internal sealed record ResolvedArtifact(
    ImageName Image,
    ManifestInfo ManifestInfo,
    OciImageManifest Manifest);
