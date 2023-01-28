# Manifests

Sub-commands:

* [`get`](#query-manifest) - Gets a manifest
* [`digest`](#query-digest) - Gets the digest of a manifest
* [`resolve`](#resolve-manifest) - Resolves a manifest

## Query Manifest

Returns the manifest of the specified name.

```console
> dredge manifest get ubuntu:22.04
{
  "manifests": [
    {
      "mediaType": "application/vnd.docker.distribution.manifest.v2+json",
      "size": 529,
      "digest": "sha256:817cfe4672284dcbfee885b1a66094fd907630d610cab329114d036716be49ba",
      "platform": {
        "architecture": "amd64",
        "os": "linux",
        "os.version": null,
        "os.features": [],
        "variant": null,
        "features": []
      }
    },
--- <cut> ---
    {
      "mediaType": "application/vnd.docker.distribution.manifest.v2+json",
      "size": 529,
      "digest": "sha256:75f39282185d9d952d5d19491a0c98ed9f798b0251c6d9a026e5b71cc2bf4de3",
      "platform": {
        "architecture": "s390x",
        "os": "linux",
        "os.version": null,
        "os.features": [],
        "variant": null,
        "features": []
      }
    }
  ],
  "mediaType": "application/vnd.docker.distribution.manifest.list.v2+json",
  "schemaVersion": 2
}
```

## Query Digest

Returns the digest of the specified name.

```console
> dredge manifest digest ubuntu:22.04
sha256:4b1d0c4a2d2aaf63b37111f34eb9fa89fa1bf53dd6e4ca954d47caebca4005c2
```

## Resolve Manifest

Resolves a manifest to a target platform's fully-qualified image digest. This is useful when you want to get the image digest of a specific platform from a multi-arch tag.

```console
> dredge manifest resolve ubuntu:22.04 --os linux --arch amd64
library/ubuntu@sha256:817cfe4672284dcbfee885b1a66094fd907630d610cab329114d036716be49ba
```
