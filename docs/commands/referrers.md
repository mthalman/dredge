# Referrer Commands

| Sub-command | Description |
|-------------|-------------|
| [`list`](#list) | List the referrers to a manifest |

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
