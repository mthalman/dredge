# Remove the .NET 9 tool target

**Version introduced:** 7.0.0

## Previous behavior

The `Valleysoft.Dredge` .NET tool package targeted both `net9.0` and
`net10.0`. Tool users on a .NET 9 SDK/runtime machine could install and run
the tool using the `net9.0` target, and the main and test projects
multi-targeted `net9.0` and `net10.0`.

## New behavior

The tool package targets only `net10.0`; the `net9.0` target has been
removed. The main and test projects now target `net10.0` exclusively.
Downloaded release executables and source builds already required .NET 10 and
are unaffected by this change.

## Type of breaking change

This is an installation and runtime compatibility change. Tool users who
relied on the package's `net9.0` target, including scripts that select
`--framework net9.0`, can no longer install or run the tool without a .NET 10
SDK and runtime.

## Reason for change

Aligning the .NET tool with the release executables and container image on
.NET 10 LTS simplifies the build matrix by removing the additional .NET 9
build and test target.

## Recommended action

Install the .NET 10 SDK and runtime before installing or updating the tool.
Change scripts that select `--framework net9.0` to use `--framework net10.0`
or omit the explicit framework selection.

## Affected APIs

The affected surface is installation and update of the `Valleysoft.Dredge`
.NET tool via `--framework net9.0`. This change does not alter CLI command
syntax or registry HTTP APIs.
