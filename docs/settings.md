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
| `platform.os` | Empty | Operating system used for platform resolution |
| `platform.osVersion` | Empty | Operating system version used for platform resolution |
| `platform.arch` | Empty | Architecture used for platform resolution |

An empty platform setting does not filter candidate manifests. Command-line
platform options take precedence over the corresponding settings. See
[Resolve a platform-specific image](platform-resolution.md).

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
  "platform": {
    "os": "<string>",
    "osVersion": "<string>",
    "arch": "<string>"
  }
}
```

Use the [`settings` commands](commands/settings.md) to read or change individual
values.
