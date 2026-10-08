# Install a release executable

Each [GitHub Release](https://github.com/mthalman/dredge/releases) includes a
Native AOT executable for each supported platform. The executables are
self-contained native binaries, so they do not require .NET to be installed.

## Choose an executable

| Platform | Release asset |
| --- | --- |
| Windows x64 | `dredge-<version>-win-x64.exe` |
| Windows Arm64 | `dredge-<version>-win-arm64.exe` |
| macOS x64 (Intel) | `dredge-<version>-osx-x64` |
| macOS Arm64 (Apple silicon) | `dredge-<version>-osx-arm64` |
| Linux x64 (glibc) | `dredge-<version>-linux-x64` |
| Linux Arm64 (glibc) | `dredge-<version>-linux-arm64` |
| Linux x64 (musl, such as Alpine) | `dredge-<version>-linux-musl-x64` |
| Linux Arm64 (musl, such as Alpine) | `dredge-<version>-linux-musl-arm64` |

Each executable has a matching `.sha256sum` file.

## Check platform requirements

The executables support the operating system versions that
[.NET 10 supports](https://github.com/dotnet/core/blob/main/release-notes/10.0/supported-os.md),
with this additional Linux requirement:

- **glibc executables** are built on Ubuntu 24.04 and are supported on glibc
  2.39 or later. For example, use Ubuntu 24.04, Debian 13, RHEL 10, or later.
  Run `ldd --version` to check the installed glibc version.
- **musl executables** are built on Alpine Linux and run on musl-based
  distributions.

Older glibc distributions are unsupported even if a particular build starts
with their installed library versions. On those distributions, use the
[.NET tool](../README.md#net-global-tool) or the
[container image](../README.md#container) instead.

Linux executables load these system libraries at run time:

- OpenSSL (`libssl`) and CA certificates, for HTTPS connections to registries.
- ICU (`libicu` on most distributions, `icu-libs` on Alpine), for globalization.
  If ICU is unavailable, set `DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1`.

## Verify the download

Download the executable and its `.sha256sum` file to the same directory, then
verify the checksum.

On Linux:

```shell
sha256sum --check dredge-<version>-linux-x64.sha256sum
```

On macOS:

```shell
shasum -a 256 --check dredge-<version>-osx-arm64.sha256sum
```

On Windows PowerShell, compare the computed hash with the first value in the
checksum file:

```powershell
(Get-FileHash .\dredge-<version>-win-x64.exe -Algorithm SHA256).Hash.ToLower()
(Get-Content .\dredge-<version>-win-x64.exe.sha256sum).Split(' ')[0]
```

## Install the executable

Rename the executable to `dredge` (or `dredge.exe` on Windows) and move it to
a directory on your `PATH`.

On Linux and macOS, make the file executable:

```shell
chmod +x dredge
```

The macOS executables are not signed with an Apple Developer ID or notarized.
If you download one with a browser, macOS can block it from running. After you
verify the checksum, remove the quarantine attribute:

```shell
xattr -d com.apple.quarantine dredge
```

Debug symbol files are not published with the release executables.
