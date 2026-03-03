# Manifest Commands

| Sub-command | Description |
|-------------|-------------|
| [`get`](#get) | Get a manifest |
| [`digest`](#digest) | Get the digest of a manifest |
| [`resolve`](#resolve) | Resolve a manifest to a platform-specific digest |

## Get

Returns the manifest of the specified image name.

```console
dredge manifest get <name>
```

Example:

```console
dredge manifest get ubuntu:22.04
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

## Digest

Returns the digest of the specified image name.

```console
dredge manifest digest <name>
```

Example:

```console
dredge manifest digest ubuntu:22.04
sha256:4b1d0c4a2d2aaf63b37111f34eb9fa89fa1bf53dd6e4ca954d47caebca4005c2
```

## Resolve

Resolves a manifest list to a platform-specific, fully-qualified image digest. This is useful for getting the digest of a specific platform from a multi-arch tag.

This command supports [platform resolution](../platform-resolution.md) via `--os`, `--arch`, and `--os-version` options.

```console
dredge manifest resolve <image> [--os <os>] [--arch <arch>] [--os-version <version>]
```

Example:

```console
dredge manifest resolve ubuntu:22.04 --os linux --arch amd64
library/ubuntu@sha256:817cfe4672284dcbfee885b1a66094fd907630d610cab329114d036716be49ba
```
