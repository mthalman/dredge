### Publish release executables as Native AOT binaries

#### Previous behavior

In v6.0.2, downloadable release executables targeted `net9.0` and were
framework-dependent single-file applications. They required an installed .NET
runtime for the executable's operating system and architecture, and the Linux
glibc executables ran on the Linux distributions supported by that runtime.

#### New behavior

The eight downloadable release executables are Native AOT binaries built on
their target operating systems. They do not require an installed .NET runtime.

The Linux glibc executables (`linux-x64` and `linux-arm64`) are built on
Ubuntu 24.04 and require glibc 2.39 or later, such as Ubuntu 24.04, Debian 13,
RHEL 10, or later. Linux executables still load OpenSSL, CA certificates, and
ICU from the system. The macOS executables are not signed with an Apple
Developer ID or notarized. Debug symbol files are not published.

The .NET tool package and container image are unchanged.

#### Type of breaking change

This is an installation and platform compatibility change. The new glibc
executables do not start on Linux distributions with glibc earlier than 2.39,
such as Ubuntu 22.04, Debian 12, or RHEL 9, even when the .NET 10 runtime is
installed.

#### Reason for change

Native AOT executables start without a .NET runtime installation, so users can
download and run a single file. [Issue #342](https://github.com/mthalman/dredge/issues/342)
tracks this change.

#### Recommended action

Choose the action for your installation method:

| Installation method | Action before upgrading |
| --- | --- |
| Release executable on Linux with glibc 2.39 or later | Download the new executable. Keep ICU, OpenSSL, and CA certificates installed, or set `DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1` if ICU is unavailable. |
| Release executable on Linux with glibc earlier than 2.39 | Install the `Valleysoft.Dredge` .NET tool or use the `ghcr.io/mthalman/dredge` container image, or retain a compatible older Dredge version. |
| Release executable on macOS | After verifying the checksum, remove the quarantine attribute from a browser download with `xattr -d com.apple.quarantine <file>`. |
| Release executable on Windows or musl-based Linux | Download the new executable. No .NET runtime is required. |
| .NET tool or container image | No action is required. |

Run `ldd --version` to check the installed glibc version. See the
[installation guide](https://github.com/mthalman/dredge/blob/main/docs/installation.md)
for platform requirements and checksum verification.

#### Affected APIs

The affected surfaces are the downloadable release executables and their
operating system requirements. This change does not alter CLI command syntax,
the .NET tool package, the container image, or registry HTTP APIs.
