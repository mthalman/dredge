# Resolve a platform-specific image

An image tag can identify a manifest list that contains images for multiple
operating systems or architectures. Commands that operate on one image must
select exactly one manifest from that list.

Dredge filters the manifest list by the platform values you provide.
Noninteractive commands require exactly one matching manifest. If no manifests
or multiple manifests match, Dredge reports an error and suggests running
`dredge manifest get` to inspect the available platforms.

The interactive `image explore` command lets you choose a platform when
several match. When comparing Windows registry images, the explorer also
supports selecting a compatible target version; see
[Comparing images](commands/images.md#comparing-images).

## Commands that resolve platforms

The following commands use platform resolution for registry images:

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

For [`docker://` image sources](commands/images.md#image-sources), Dredge checks
that the local image matches the requested platform. The Docker Engine client
cannot select another platform from an image index. Use a concrete matching
local image ID if the requested platform does not match.

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

Pass `--os-version=""` to ignore a saved `platform.osVersion` for one command
without changing the setting. For example, if `myimage:dev` is a Linux amd64
image in the selected Docker engine:

```console
dredge image inspect docker://myimage:dev --os linux --arch amd64 --os-version=""
```
