### Update .NET prerequisites for tools, executables, and source builds

The .NET tool drops .NET 8 support but retains .NET 9 support. Downloaded
executables now require the .NET 10 runtime, and source builds require a
compatible .NET 10 SDK. Check your installation method before upgrading;
container images include their runtime and do not require host .NET.

#### Previous behavior

In v6.0.2, the `Valleysoft.Dredge` .NET tool package targeted `net8.0` and
`net9.0`. Downloadable release executables targeted `net9.0` and were
framework-dependent (`--no-self-contained`). The source checkout selected a
.NET 9 SDK through `global.json`.

#### New behavior

The tool package targets `net9.0` and `net10.0`; it no longer contains a
`net8.0` target. Downloadable release executables target `net10.0` and remain
framework-dependent, so they require an appropriate installed .NET 10 runtime.
Source builds require the .NET 10 SDK selected by `global.json`.

These requirements apply when installing, updating to, or building the new
version. Existing installations and explicitly pinned older versions do not
change merely because a new release is available.

#### Type of breaking change

This is an installation and runtime compatibility change. Tool users with only
.NET 8 and scripts that select `--framework net8.0` cannot use the new tool
package. Users of downloaded executables with only .NET 9 cannot run the new
executables. Source builders without a compatible .NET 10 SDK cannot build the
new checkout.

.NET 9 SDK/runtime tool users remain supported through the package's `net9.0`
target. The .NET 10 requirement for downloaded executables does not apply to
every tool installation. Container images include their runtime; the container
host does not need .NET installed.

#### Reason for change

[PR #236](https://github.com/mthalman/dredge/pull/236) moves builds and release
executables to .NET 10 and drops the .NET 8 tool target while retaining the
.NET 9 tool target.

#### Recommended action

Choose the action for your installation method:

| Installation or build | Action before upgrading |
| --- | --- |
| .NET tool on a .NET 8-only machine | Install a .NET 9 or .NET 10 SDK and matching runtime, or retain a compatible older Dredge version until you can upgrade. |
| Tool installation script with `--framework net8.0` | Remove the explicit framework selection, or select `net9.0` or `net10.0` to match the installed SDK/runtime. |
| .NET tool on a .NET 9 SDK/runtime machine | Keep using the supported `net9.0` target; .NET 10 is not required for this installation method. |
| Downloaded release executable | Install the .NET 10 runtime for the executable's OS and architecture, or use the .NET tool's `net9.0` target if you must stay on .NET 9. |
| Source build | Install a .NET 10 SDK compatible with the checkout's `global.json`. Having only a runtime is not sufficient to build. |
| Container image | Use the image's included runtime; no host .NET upgrade is needed. |

Use `dotnet --list-sdks` and `dotnet --list-runtimes` to inspect the machine.
Runtime roll-forward does not let an application targeting .NET 10 run on the
older .NET 9 or .NET 8 runtime.

#### Affected APIs

The affected surfaces are installation and update of the `Valleysoft.Dredge`
.NET tool, framework selection in tool scripts, execution of downloaded release
binaries, and source builds. This change does not alter CLI command syntax or
registry HTTP APIs.
