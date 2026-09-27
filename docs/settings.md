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
| `explore.theme` | `dark` | Explorer color theme: `dark` or `light` |
| `explore.mouse` | `true` | Whether the explorer uses the mouse: `true` or `false` |
| `explore.viewer.exePath` | Empty | Executable for `o`; empty uses the platform's default text viewer |
| `explore.viewer.args` | `"{0}"` | Arguments for the text viewer; `{0}` is the staged file path |
| `explore.viewer.terminal` | `false` | Set to `true` when a configured viewer uses the current terminal |
| `explore.keys.<action>` | Empty | Replacement key for an explorer action; empty uses the default |

An empty platform setting does not filter candidate manifests. Command-line
platform options take precedence over the corresponding settings. See
[Resolve a platform-specific image](platform-resolution.md).

`operations.timeout` accepts a .NET `TimeSpan` value. Set it to an empty string
or `null` to disable the timeout. Zero and negative values also disable the
timeout. Positive values are limited to about 24.8 days by the runtime.

For `image explore`, this limit covers the entire interactive session. On
timeout, the explorer closes and reports the timeout. Disable or increase the
limit before starting a longer exploration.

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

## Configure the explorer

The [`image explore`](commands/images.md#explore) command reads the `explore`
settings each time it starts.

Set `explore.theme` to `light` for light terminal backgrounds. When the
`NO_COLOR` environment variable is set, the explorer uses a monochrome theme.

Set `explore.mouse` to `false` to leave the mouse to the terminal, which is the
same as passing `--no-mouse`.

`y` copies the equivalent `dredge` command. On local Windows, it uses the
Windows clipboard. Elsewhere, including SSH sessions, it asks the terminal to
copy with OSC 52. The terminal must support and allow OSC 52; tmux must be
configured to pass it through. If the clipboard can't be reached, dredge
shows the command instead.

Press `o` while inspecting a file to open it in an external text viewer.
Configure the viewer executable with `explore.viewer.exePath` and its arguments
with `explore.viewer.args`. The arguments string uses `{0}` for the staged file
path; quote the placeholder when the viewer expects a filename argument.
When `exePath` is empty, dredge uses `less -X` on Unix-like systems or the
Windows `more` command. The Windows default passes the file to `more` through
standard input. Both built-in defaults ignore `explore.viewer.args`; set both
settings to use custom arguments. The built-in pagers leave their output visible
and wait for Enter after closing before the explorer screen returns. Terminal
viewers temporarily replace the explorer screen; configured windowed viewers
leave the explorer visible and interactive while the viewer runs. Set
`explore.viewer.terminal` to `true` for a configured terminal viewer such as
`vim`. The staged file is removed when the viewer process exits; configure the
viewer to remain running while it uses the file.

For example, to use Notepad++ on Windows:

```console
dredge settings set explore.viewer.exePath "C:\Program Files\Notepad++\notepad++.exe"
dredge settings set explore.viewer.args "\"{0}\""
```

Each single-character explorer key can be replaced with any printable ASCII
character. Two actions cannot share a key. The actions are `quit`, `help`,
`insights`, `search`, `wholeFilesystem`, `firstUserLayer`, `compare`,
`findingsOnly`, `previousLayer`, `nextLayer`, `toggleAdded`,
`toggleModified`, `toggleIdentical`, `toggleDeleted`, `platform`, `extract`,
`copyCommand`, `viewer`, `swapSides`, and `retry`. For example, to quit with
`Q` and use `q` to toggle the whole filesystem:

```console
dredge settings set explore.keys.quit Q
dredge settings set explore.keys.wholeFilesystem q
```

Arrow keys, `Tab`, `Enter`, `Esc`, and the `Alt` search shortcuts
cannot be remapped.

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
  },
  "explore": {
    "theme": "dark",
    "mouse": "true",
    "viewer": {
      "exePath": "",
      "args": "\"{0}\"",
      "terminal": "false"
    },
    "keys": {
      "quit": "<string>",
      "viewer": "<string>"
    }
  }
}
```

Use the [`settings` commands](commands/settings.md) to read or change individual
values.
