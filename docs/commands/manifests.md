# Manifest commands

| Sub-command | Description |
|-------------|-------------|
| [`get`](#get) | Get a manifest |
| [`digest`](#digest) | Get the digest of a manifest |
| [`resolve`](#resolve) | Resolve a manifest to a platform-specific digest |
| [`delete`](#delete) | Delete a manifest and all tags pointing to it |

## Get

Returns the manifest of the specified image name.

```console
dredge manifest get <image>
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
dredge manifest digest <image>
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

## Delete

Deletes one manifest by digest, or resolves an explicit tag to a digest and
deletes that manifest. **All tags in the repository pointing to the deleted
manifest are affected**, not just the tag supplied on the command line.
To remove only a tag association, use [`tag delete`](tags.md#delete).

The registry must enable deletion and your credentials must have delete
permission. See [authentication](../authentication.md#deletion-permissions).

```console
dredge manifest delete <image>:<tag> [--yes]
dredge manifest delete <image>@<digest> [--yes]
```

| Option | Description |
|--------|-------------|
| `--yes`, `-y` | Skip confirmation; required when standard input is redirected |

An explicit tag or digest is required. A bare repository name does not imply
`:latest`. The command accepts one reference and has no platform-selection
or recursive-deletion options.

Without `--yes`, Dredge inspects the top-level manifest and displays its
resolved digest and the deletion warning on stderr. Dredge does not enumerate
the other tags that point to that digest. Press `y` to confirm, or `n` or Enter
to cancel; the default is No. The confirmed digest is used for deletion even
if the tag changes while you are answering the prompt.
Canceled operations return a nonzero exit code.

For scripts and redirected stdin, pass `--yes`; piped confirmation is not
accepted. This option skips confirmation only. Missing manifests, disabled
deletion, authentication failures, and permission errors still fail.

Example (prompts before deleting):

```console
dredge manifest delete registry.example.com/team/app:old
```

Example using an explicit digest (deletes immediately without prompting):

```console
dredge manifest delete registry.example.com/team/app@sha256:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef --yes
Deleted manifest 'registry.example.com/team/app@sha256:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef'.
```

### Manifest lists and storage cleanup

A Docker manifest list or OCI image index is deleted as a whole. Dredge does
not select a platform, and persisted platform settings do not affect deletion.
The confirmation identifies the list/index and warns that all platforms become
unavailable through its associated tags and index digest.

Dredge does **not** delete the child manifests, configuration blobs, layers,
or attached artifacts. To delete a particular platform manifest, explicitly
target its digest; doing so may break other indexes that reference it.
The registry client maintains referrers fallback metadata where needed when
deleting a subject-bearing manifest.

There is no cascade-to-layers option. Physical storage reclamation depends on
registry retention policies and garbage collection. Child manifests left
behind after index deletion can still reference layers, so deleting an index
does not necessarily make those layers eligible for garbage collection.
