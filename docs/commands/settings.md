# Settings Commands

| Sub-command | Description |
|-------------|-------------|
| [`open`](#open) | Open the settings file |
| [`get`](#get) | Get a setting value |
| [`set`](#set) | Set a setting value |
| [`clear-cache`](#clear-cache) | Delete cached layer data |

## Open

Opens the Dredge [settings file](../settings.md) in the default associated program. If no program is associated, outputs the file path.

```console
dredge settings open
```

## Get

Gets the value of a setting. Setting names use dot notation for hierarchical JSON paths.

```console
dredge settings get <name>
```

Example:

```console
dredge settings get fileCompareTool.exePath
```

## Set

Sets the value of a setting. Setting names use dot notation for hierarchical JSON paths.

```console
dredge settings set <name> <value>
```

Example:

```console
dredge settings set platform.os linux
```

## Clear Cache

Deletes the local cache of layer data stored in the temporary directory. This cache is created by commands like [`image compare files`](images.md#compare-files) and [`image save-layers`](images.md#save-layers).

```console
dredge settings clear-cache
```
