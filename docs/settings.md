# Configure Dredge

Dredge stores persistent configuration in `settings.json`. Dredge creates the
file when a command first loads or changes settings.

The default path depends on the operating system:

- Windows: `%LOCALAPPDATA%\Valleysoft.Dredge\settings.json`
- Linux: `$HOME/.local/share/Valleysoft.Dredge/settings.json`
- macOS: `$HOME/Library/Application Support/Valleysoft.Dredge/settings.json`

Run `dredge settings open` to open the file in its associated application. If
Dredge cannot open the file, the command prints its path.

## Available settings

Setting names use dot notation with `dredge settings get` and
`dredge settings set`.

| Setting | Default | Purpose |
|---------|---------|---------|
| `fileCompareTool.exePath` | Empty | Executable that `image compare files` starts |
| `fileCompareTool.args` | Empty | Arguments passed to the comparison executable |
| `operations.timeout` | `00:30:00` | Maximum duration of a Dredge operation |
| `platform.os` | Empty | Operating system used for platform resolution |
| `platform.osVersion` | Empty | Operating system version used for platform resolution |
| `platform.arch` | Empty | Architecture used for platform resolution |
| `cache.path` | Empty | Persistent layer cache location; empty uses the platform default |
| `cache.maxBytes` | `5368709120` | Maximum retained cache bytes (5 GiB); `0` disables retention |

An empty platform setting does not filter candidate manifests. Command-line
platform options take precedence over the corresponding settings. See
[Resolve a platform-specific image](platform-resolution.md).

`operations.timeout` accepts a .NET `TimeSpan` value. Set it to an empty string
or `null` to disable the timeout. Zero and negative values also disable the
timeout. Positive values are limited to about 24.8 days by the runtime.

## Configure the layer cache

Dredge shares compressed layer blobs, filesystem indexes, and merged filesystem
views between commands and processes. Complete blobs are digest-verified before
publication. Corrupt or incompatible cached data is rebuilt, with a diagnostic
on standard error.

The default cache root depends on the operating system:

| Operating system | Default cache root |
|------------------|--------------------|
| Windows | `%LOCALAPPDATA%\Valleysoft.Dredge\cache` |
| Linux | `$XDG_CACHE_HOME/Valleysoft.Dredge`, or `$HOME/.cache/Valleysoft.Dredge` when `XDG_CACHE_HOME` is unset or relative |
| macOS | `$HOME/Library/Caches/Valleysoft.Dredge` |

Set `DREDGE_CACHE_DIR` to override `cache.path`, for example in CI. Otherwise,
set the location through the settings command:

```console
dredge settings set cache.path /home/me/dredge-cache
dredge settings set cache.maxBytes 1073741824
```

Relative locations resolve against the current working directory. Choose a
dedicated directory on a local filesystem with file locking and atomic rename
support. The cache can contain **private image contents**. Dredge creates private
directories (0700 on Unix, current-user access on Windows); an existing directory
with broader access is rejected rather than silently changing its permissions.
Do not share the cache between users or expose it as a CI artifact.
Layer-extraction scratch directories also stay within this private cache
location, not the shared system temporary directory.

`cache.maxBytes` accepts a nonnegative integer byte count. Dredge evicts
least-recently-used blobs before indexes and merged views. The limit covers
retained data, not active downloads, leased blobs, or extraction scratch space:
an operation can temporarily use more space. Trimming runs when entries are
published and when an operation releases its cached blobs. Small coordination
files remain so concurrent processes can use the same locks.

Setting the limit to `0` disables retention after operations complete. Commands
still use private temporary disk storage to avoid downloading a layer twice
during one operation. Comparison-tool output is not reusable cache data and is
not included in this limit.

Use [`dredge settings clear-cache`](commands/settings.md#clear-cache) to remove
cached data. Changing `cache.path` does not move or delete the previous cache.
The cache does not support offline image resolution: commands still resolve
manifests and request image configuration from the registry.

## Configure the file comparison tool

The `image compare files` command requires both `fileCompareTool` settings.
Set `exePath` to a program that compares two directories. In `args`, use `{0}`
for the extracted base image path and `{1}` for the extracted target image
path.

For example:

```console
dredge settings set fileCompareTool.exePath "C:\Program Files\Beyond Compare 4\BCompare.exe"
dredge settings set fileCompareTool.args "{0} {1}"
```

Quote the placeholders in `fileCompareTool.args` if the comparison program
requires quoted paths.

## Settings file schema

```json
{
  "fileCompareTool": {
    "exePath": "<string>",
    "args": "<string>"
  },
  "operations": {
    "timeout": "00:30:00"
  },
  "platform": {
    "os": "<string>",
    "osVersion": "<string>",
    "arch": "<string>"
  },
  "cache": {
    "path": "<string>",
    "maxBytes": "5368709120"
  }
}
```

Use the [`settings` commands](commands/settings.md) to read or change individual
values.
