# Use hyphenated CLI output format names

**Version introduced:** 7.0.0

Image comparison commands now require `side-by-side` instead of `SideBySide`
and `external-tool` instead of `ExternalTool` for `--output`. Update scripts
that pass these values; commands that omit `--output` keep their defaults.

## Previous behavior

`dredge image compare layers --output SideBySide` and
`dredge image compare files --output ExternalTool` accepted .NET enum names in
v6.0.2. The `image compare metadata` command added after v6.0.2 also accepted
`SideBySide` before the output option change.

## New behavior

Output options accept the following named values, case-insensitively. Help and
completion show the canonical lowercase spellings:

| Command | Accepted `--output` values |
| --- | --- |
| `dredge image compare layers` | `side-by-side`, `inline`, `json` |
| `dredge image compare metadata` | `side-by-side`, `inline`, `json` |
| `dredge image compare files` | `external-tool` |
| `dredge image ls` | `text`, `json` |
| `dredge referrer check` | `summary`, `json` |
| `dredge referrer inspect` | `summary`, `json` |

`SideBySide` and `ExternalTool`, including unhyphenated variants such as
`sidebyside` and `externaltool`, now fail argument parsing. Capitalized
single-word names such as `Inline`, `Json`, `Text`, and `Summary` remain
accepted for their respective commands. `SIDE-BY-SIDE` and `EXTERNAL-TOOL`
also work: the required change is the hyphens, not capitalization.

Defaults remain side-by-side for layer and metadata comparisons and the external
tool for file comparisons. Omitting `--output` does not require a migration.

## Type of breaking change

This is a command-line argument compatibility change. Scripts or wrappers that
pass either unhyphenated multiword enum name now fail before execution. The
format-name change does not itself change the selected output format.

## Reason for change

[PR #323](https://github.com/mthalman/dredge/pull/323) gives output options
consistent CLI-style names in help, completion, and parsing instead of exposing
.NET enum member names.

## Recommended action

Replace `SideBySide` with `side-by-side` in layer and metadata comparisons, and
replace `ExternalTool` with `external-tool` in file comparisons. Update stored
command lines, scripts, and wrapper-generated arguments:

```sh
dredge image compare layers ubuntu:22.04 ubuntu:24.04 --output side-by-side
dredge image compare metadata ubuntu:22.04 ubuntu:24.04 --output side-by-side
dredge image compare files ubuntu:22.04 ubuntu:24.04 --output external-tool
```

Use the accepted names in the table rather than unhyphenated multiword names
or numeric enum values. File comparison still requires a configured external
comparison tool.
Layer comparisons that find differences now return exit code `2`, independently
of the spelling change.

## Affected APIs

The affected arguments are `--output` on `image compare layers`,
`image compare metadata`, and `image compare files`. The other output options
in the table use the same case-insensitive parser but retain their single-word
spellings. Registry HTTP APIs are unchanged.
