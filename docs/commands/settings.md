# Settings commands

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
dredge settings get <setting>
```

Example:

```console
dredge settings get fileCompareTool.exePath
```

## Set

Sets the value of a setting. Setting names use dot notation for hierarchical JSON paths.

```console
dredge settings set <setting> <value>
```

Example:

```console
dredge settings set platform.os linux
```

## Clear cache

Deletes compressed layer blobs, filesystem indexes, and merged views from the
configured [persistent layer cache](../settings.md#configure-the-layer-cache).
It also removes the legacy extracted-layer cache and the retained inputs from
[`image compare files`](images.md#compare-files) under Dredge's temporary
directory. Settings and unrelated files remain unchanged.

Before running this command, close all external comparison tools. Wait for any
comparisons still preparing their inputs to finish. Dredge keeps comparison
inputs until explicit cleanup, even after the GUI launcher exits.

```console
dredge settings clear-cache
```

The command reports the bytes deleted. Active blobs and staging files are
skipped with a diagnostic; run the command again after other Dredge operations
finish to remove them. Coordination files and the cache directory remain.

Dredge versions before 7.0 do not participate in the new cache coordination.
Do not clear their temporary output while they are running.
