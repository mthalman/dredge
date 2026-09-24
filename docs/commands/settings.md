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
It also removes the legacy extracted-layer cache and comparison-tool output
under Dredge's temporary directory. It leaves settings and unrelated files
untouched.

```console
dredge settings clear-cache
```

The command reports the bytes deleted. Active blobs and staging files are
skipped with a diagnostic; run the command again after other Dredge operations
finish to remove them. Coordination files and the cache directory remain.

Close external comparison tools before cleanup. Dredge versions before 7.0
do not participate in the new cache coordination; do not clear their temporary
output while they are running.
