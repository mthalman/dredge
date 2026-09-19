### Update .NET prerequisites for tools, executables, and source builds

The .NET tool drops .NET 8 and .NET 9 support and requires .NET 10. Downloaded
executables also require the .NET 10 runtime, and source builds require a
compatible .NET 10 SDK. Check your installation method before upgrading;
container images include their runtime and do not require host .NET.

#### Previous behavior

In v6.0.2, the `Valleysoft.Dredge` .NET tool package targeted `net8.0` and
`net9.0`. Downloadable release executables targeted `net9.0` and were
framework-dependent (`--no-self-contained`). The source checkout selected a
.NET 9 SDK through `global.json`.

#### New behavior

The tool package targets only `net10.0`; it no longer contains `net8.0` or
`net9.0` targets. Downloadable release executables target `net10.0` and remain
framework-dependent, so they require an appropriate installed .NET 10 runtime.
Source builds require the .NET 10 SDK selected by `global.json`. The application
and test projects target only `net10.0`.

These requirements apply when installing, updating to, or building the new
version. Existing installations and explicitly pinned older versions do not
change merely because a new release is available.

#### Type of breaking change

This is an installation and runtime compatibility change. Tool users with only
.NET 8 or .NET 9 and scripts that select `--framework net8.0` or
`--framework net9.0` cannot use the new tool package. Users of downloaded
executables without a .NET 10 runtime cannot run the new executables. Source
builders without a compatible .NET 10 SDK cannot build the new checkout.

Both .NET tool installations and downloaded executables require .NET 10.
Container images include their runtime; the container host does not need .NET
installed.

#### Reason for change

[PR #236](https://github.com/mthalman/dredge/pull/236) moved builds and release
executables to .NET 10 and dropped the .NET 8 tool target. Removing the .NET 9
target aligns the tool with the executables and container image on .NET 10 LTS
and removes the additional .NET 9 build and test target.

#### Recommended action

Choose the action for your installation method:

| Installation or build | Action before upgrading |
| --- | --- |
| .NET tool on a machine with only .NET 8 or .NET 9 | Install the .NET 10 SDK and matching runtime, or retain a compatible older Dredge version until you can upgrade. |
| Tool installation script with `--framework net8.0` or `--framework net9.0` | Install the .NET 10 SDK and runtime, then remove the explicit framework selection or select `net10.0`. |
| Downloaded release executable | Install the .NET 10 runtime for the executable's OS and architecture, or retain a compatible older Dredge version until you can upgrade. |
| Source build | Install a .NET 10 SDK compatible with the checkout's `global.json`. Change scripts that select `-f net9.0` to use `-f net10.0`. Having only a runtime is not sufficient to build. |
| Container image | Use the image's included runtime; no host .NET upgrade is needed. |

Use `dotnet --list-sdks` and `dotnet --list-runtimes` to inspect the machine.
Runtime roll-forward does not let an application targeting .NET 10 run on the
older .NET 9 or .NET 8 runtime.

#### Affected APIs

The affected surfaces are installation and update of the `Valleysoft.Dredge`
.NET tool, framework selection in tool scripts, execution of downloaded release
binaries, and source builds. This change does not alter CLI command syntax or
registry HTTP APIs.
