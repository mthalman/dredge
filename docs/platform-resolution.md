# Resolve a platform-specific image

An image tag can identify a manifest list that contains images for multiple
operating systems or architectures. Commands that operate on one image must
select exactly one manifest from that list.

Dredge filters the manifest list by the platform values you provide. Resolution
succeeds only when exactly one manifest matches. If no manifests or multiple
manifests match, Dredge reports an error and suggests running
`dredge manifest get` to inspect the available platforms.

## Commands that resolve platforms

The following commands use platform resolution:

- [`manifest resolve`](commands/manifests.md#resolve)
- Every [`image`](commands/images.md) subcommand

## Select a platform

Pass one or more platform options to a command:

- `--os`: Operating system, such as `linux` or `windows`.
- `--os-version`: Operating system version, such as `10.0.20348.1129`.
- `--arch`: Architecture, such as `amd64`, `arm`, or `arm64`.

For example:

```console
dredge manifest resolve alpine:latest --os linux --arch amd64
```

You do not need to specify every value. Provide enough values to leave one
matching manifest.

## Set default platform values

To reuse platform values across commands, save them in the
[Dredge settings file](settings.md):

```json
{
  "platform": {
    "os": "<os-name>",
    "osVersion": "<os-version>",
    "arch": "<architecture>"
  }
}
```

Set each value with [`dredge settings set`](commands/settings.md#set):

```console
dredge settings set platform.os linux
dredge settings set platform.arch amd64
```

## Precedence

For each platform value, a command-line option takes precedence over the
corresponding saved setting. If you omit an option, Dredge uses its saved
setting. If both values are empty, Dredge does not filter on that field.

Pass an explicitly empty OS version to suppress its saved default:

```console
dredge image inspect alpine:latest --os linux --arch amd64 --os-version=""
```

Omitting `--os-version` still uses the saved version. An explicitly empty value
disables OS-version filtering for both registry and Docker Engine images.
A bare `--os-version` without a value remains an error; use `--os-version=""`
to request an empty value explicitly. The empty option can precede the image
reference. If you repeat `--os-version`, its last occurrence takes precedence,
including an explicitly empty value.

## Docker Engine images

For [`docker://` references](commands/images.md#image-sources), Dredge validates
the requested platform against the resolved daemon image configuration.
Command-line options still take precedence over saved platform settings.

The source diagnostics report the platform actually analyzed and its immutable
image ID. Missing platforms and unsupported multi-platform resolution produce
explicit errors; Dredge does not pull a platform, re-resolve a tag during analysis,
or substitute another image. Where the Engine API/client cannot resolve a
multi-platform tag to the requested immutable platform image, use a supported
platform-specific image ID.

The host operating system does not determine the image platform. For example,
Dredge running on Windows can inspect Linux images in Docker Desktop's
WSL-backed engine. Windows filesystem analysis remains unsupported.

The explorer carries the selected platform, including its variant, when
resolving base images or opening a comparison target. Once the explorer has
resolved a platform, it does not fill absent fields from saved defaults. For
example, a Linux image without an OS version in either its configuration or
selected manifest descriptor stays versionless even when a Windows OS version
is saved in settings.

Cross-source comparisons treat
ARM64's omitted variant and its default `v8` variant as equivalent, without
merging distinct ARM variants or OS versions.

For other architectures, an omitted configuration variant does not constrain
the explorer to variantless manifests. When resolving a base image or comparison
target, the explorer prefers the exact platform and otherwise accepts a unique
compatible platform. Multiple compatible variants require an explicit choice;
the explorer does not silently choose between known variants such as ARM `v6`
and `v7`.

If a registry configuration omits its variant or OS version, the explorer retains
the corresponding field from the selected manifest descriptor for base verification,
copied commands, and the final comparison. A descriptor does not override an
explicit, conflicting configuration value.

For Docker OCI exports, Dredge also retains optional platform fields supplied by
the selected descriptor or pinned image inspection when the configuration omits
them. Explicit configuration fields must agree with the inspected and selected
descriptor platform. This preserves the analyzed variant and OS version without
rewriting the exported configuration JSON.
Shared OCI manifest or index blobs are evaluated in each descriptor's platform
context. If one manifest resolves to multiple distinct effective platforms,
Dredge reports ambiguity instead of selecting the first descriptor. Equivalent
contexts and a uniquely selected platform do not cause ambiguity. For ARM64,
an omitted variant and explicit `v8` are equivalent during this selection.

Copied `cat`, `ls`, and file-comparison commands include the analyzed platform
options, even for single-manifest registry images. These options preserve the
explorer's platform selection when saved defaults select a different image.
For an image without an OS version, copied commands include `--os-version=""`
so a saved version does not change the analyzed platform.
Commands involving a daemon also preserve its resolved endpoint.
