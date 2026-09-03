# Referrer commands

| Sub-command | Description |
|-------------|-------------|
| [`list`](#list) | List the referrers to a manifest |
| [`check`](#check) | Check for required OCI referrer artifact types |
| [`inspect`](#inspect) | Inspect an OCI artifact referenced by an image |
| [`get`](#get) | Retrieve an OCI artifact payload |

## List

Returns the referrers to the specified manifest. This uses the [OCI Referrers API](https://github.com/opencontainers/distribution-spec/blob/main/spec.md#listing-referrers).

```console
dredge referrer list <name> [--artifact-type <type>]
```

| Option | Description |
|--------|-------------|
| `--artifact-type` | Filter results by artifact media type |

Example:

```console
dredge referrer list mcr.microsoft.com/dotnet/core/sdk:latest
{
  "manifests": [
    {
      "mediaType": "application/vnd.oci.image.manifest.v1+json",
      "digest": "sha256:551e9aa2046071e51b1611a7e85f85af3d2cc6841935cc176a931de4194ecdc1",
      "size": 788,
      "urls": [],
      "annotations": {
        "org.opencontainers.image.created": "2024-08-13T14:20:19Z",
        "vnd.microsoft.artifact.lifecycle.end-of-life.date": "2022-12-13"
      },
      "artifactType": "application/vnd.microsoft.artifact.lifecycle"
    }
  ],
  "annotations": {},
  "schemaVersion": 2,
  "mediaType": "application/vnd.oci.image.index.v1+json"
}
```

## Check

Checks that an image has every required OCI referrer artifact type. Tagged
references are resolved to their manifest digest, and all pages of referrers
are checked.

Artifact types use exact, case-sensitive matching. This command checks only
for matching referrer descriptors; it does not inspect artifact payloads or
verify signatures.

```console
dredge referrer check <name> --artifact-type <type> [--artifact-type <type>]... [--output <format>]
```

| Option | Description |
|--------|-------------|
| `--artifact-type` | Required artifact media type. Specify once for each required type |
| `--output` | Output format: `summary` (default) or `json` |

The summary reports every requested artifact type and the digest of each
matching referrer:

```console
dredge referrer check mcr.microsoft.com/dotnet/sdk:10.0.300 \
  --artifact-type application/vnd.cncf.notary.signature \
  --artifact-type application/spdx+json
PASS application/vnd.cncf.notary.signature
  sha256:ed1eda051b8d154c19e0499ae5dadf3022e1c07f38fef2b6652beaeac2ce5069
FAIL application/spdx+json
```

JSON output includes the overall result, each artifact type's status, and the
complete OCI descriptor for every matching referrer:

```console
dredge referrer check mcr.microsoft.com/dotnet/sdk:10.0.300 \
  --artifact-type application/vnd.cncf.notary.signature \
  --artifact-type application/spdx+json \
  --output json
{
  "succeeded": false,
  "results": [
    {
      "artifactType": "application/vnd.cncf.notary.signature",
      "found": true,
      "referrers": [
        {
          "mediaType": "application/vnd.oci.image.manifest.v1+json",
          "digest": "sha256:ed1eda051b8d154c19e0499ae5dadf3022e1c07f38fef2b6652beaeac2ce5069",
          "size": 990,
          "urls": [],
          "annotations": {
            "io.cncf.notary.x509chain.thumbprint#S256": "[\"5461c8da6c44fc19706a093ae02cf04548a6c13242ae983d954be429666e6440\",\"9b1894f223d934cbd6575af3c6e1f6096b9221a7da132185f5a5cdc92235b5dc\",\"23ffe2b8bdb9a1711515d4cffda04bc7f793d513c76c243f1020507d8669b7db\"]",
            "org.opencontainers.image.created": "2026-05-20T21:19:22.3944247Z"
          },
          "artifactType": "application/vnd.cncf.notary.signature"
        }
      ]
    },
    {
      "artifactType": "application/spdx+json",
      "found": false,
      "referrers": []
    }
  ]
}
```

| Exit code | Meaning |
|----------:|---------|
| `0` | Every required artifact type exists |
| `1` | A configuration, registry, or execution error occurred |
| `2` | At least one required artifact type is missing |

## Inspect

Displays metadata and payload information for an OCI artifact referenced by an
image.

```console
dredge referrer inspect <name> <artifact-digest> [--output <format>]
```

| Option | Description |
|--------|-------------|
| `--output` | Output format: `summary` (default) or `json` |

The summary always includes the artifact type, subject, annotations, config,
and each payload's zero-based index, digest, media type, and size. SPDX,
CycloneDX, in-toto, DSSE, and Notary Project signature payloads also include
format-specific details. Other artifact types retain the generic summary and
remain retrievable.

Use JSON output for automation or to access the complete artifact manifest:

```console
dredge referrer inspect registry.example/repo:tag sha256:abc123 --output json
```

For example, inspect a Notary Project signature for the Windows CSSC Python
image:

```console
dredge referrer inspect mcr.microsoft.com/windows-cssc/python:3.13-windows-ltsc2019 sha256:852619682b24fac4bf675a911d0c0d8b1b64e6cd742873137ad6998000f8a847
Image: mcr.microsoft.com/windows-cssc/python:3.13-windows-ltsc2019
Artifact digest: sha256:852619682b24fac4bf675a911d0c0d8b1b64e6cd742873137ad6998000f8a847
Artifact type: application/vnd.cncf.notary.signature
Manifest media type: application/vnd.oci.image.manifest.v1+json
Subject digest: sha256:6f9f8b9a4fea0766f34c7c93deac4468ea2ed8ad26284b21a6a8ccca13a4ad1d
Config: sha256:44136fa355b3678a1146ad16f7e8649e94fb4fc21fe77e8310c060f61caaff8a (application/vnd.oci.empty.v1+json, 2 bytes)
Annotations:
  io.cncf.notary.x509chain.thumbprint#S256: ["303d73d0e9b4efe97e04bd58ad55423669f2a77c36e44408cc516388afbb18eb","9b1894f223d934cbd6575af3c6e1f6096b9221a7da132185f5a5cdc92235b5dc","23ffe2b8bdb9a1711515d4cffda04bc7f793d513c76c243f1020507d8669b7db"]
  org.opencontainers.image.created: 2026-08-13T09:51:15Z
Payloads:
  [0] sha256:dd27519d8486110d4e3c652fa1807862e8209fd7ae83fa9f8cfdc0effbd449d0
      Media type: application/cose
      Size: 10995 bytes
      Format: Notary signature
      Envelope format: COSE
```

## Get

Streams an artifact payload without modifying registry content.

```console
dredge referrer get <name> <artifact-digest> [--payload <index-or-digest>] [--output <path>]
```

| Option | Description |
|--------|-------------|
| `--payload` | Payload index or digest. Optional for a single-payload artifact and required when multiple payloads exist |
| `--output` | Write the payload to this file instead of standard output |

Without `--output`, Dredge writes the exact payload bytes to standard output,
which supports binary data and shell redirection. Use `inspect` to find the
index or digest for artifacts with multiple payloads.

```console
dredge referrer get registry.example/repo:tag sha256:abc123 --payload 0 --output sbom.json
```
