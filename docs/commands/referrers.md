# Referrers

Sub-commands:

* [`list`](#query-referrers) - Lists the referrers to a manifest

## Query Referrers

Returns the referrers to the specified manifest.

```console
> dredge referrer list mcr.microsoft.com/dotnet/core/sdk:latest
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
