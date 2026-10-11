# Update .NET prerequisites for tools and source builds

**Version introduced:** 7.0.0

The .NET tool now requires .NET 10, and source builds require a compatible
.NET 10 SDK. Check your installation method before upgrading; container
images include their runtime and do not require host .NET. Downloaded release
executables no longer require .NET; see the Native AOT release executable
change for their requirements.

## Previous behavior

In v6.0.2, the `Valleysoft.Dredge` .NET tool package targeted `net8.0` and
`net9.0`. The source checkout selected a .NET 9 SDK through `global.json`.

## New behavior

The tool package targets only `net10.0`; it no longer contains `net8.0` or
`net9.0` targets. Source builds require the .NET 10 SDK selected by
`global.json`. A separate migration topic covers the removal of the .NET 9
target.

These requirements apply when installing, updating to, or building the new
version. Existing installations and explicitly pinned older versions do not
change merely because a new release is available.

## Type of breaking change

This is an installation and runtime compatibility change. Tool users with
only .NET 8 or .NET 9, and scripts that select `--framework net8.0` or
`--framework net9.0`, cannot use the new tool package. Source builders without
a compatible .NET 10 SDK cannot build the new checkout. Container images
include their runtime; the container host does not need .NET installed.

## Reason for change

[PR #236](https://github.com/mthalman/dredge/pull/236) moves source builds to
.NET 10 and drops the .NET 8 tool target. The separate removal of the .NET 9
tool target completes the transition to a .NET 10-only tool package.

## Recommended action

Choose the action for your installation method:

| Installation or build | Action before upgrading |
| --- | --- |
| .NET tool on a .NET 8 or .NET 9-only machine | Install the .NET 10 SDK and runtime, or retain a compatible older Dredge version until you can upgrade. |
| Tool installation script with `--framework net8.0` or `--framework net9.0` | Remove the explicit framework selection, or select `net10.0` and install the .NET 10 SDK/runtime. |
| .NET tool on a .NET 10 machine | Install or update the tool with the `net10.0` target. |
| Source build | Install a .NET 10 SDK compatible with the checkout's `global.json`. Having only a runtime is not sufficient to build. |
| Container image | Use the image's included runtime; no host .NET upgrade is needed. |

Use `dotnet --list-sdks` and `dotnet --list-runtimes` to inspect the machine.
Runtime roll-forward does not let an application targeting .NET 10 run on the
older .NET 9 or .NET 8 runtime.

## Affected APIs

The affected surfaces are installation and update of the `Valleysoft.Dredge`
.NET tool, framework selection in tool scripts, and source builds. This change
does not alter CLI command syntax or registry HTTP APIs.
