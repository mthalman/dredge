# Image commands

All image commands support [platform resolution](../platform-resolution.md) via `--os`, `--arch`, and `--os-version` options.

| Sub-command | Description |
|-------------|-------------|
| [`inspect`](#inspect) | Inspect an image configuration |
| [`os`](#os) | Show OS information |
| [`explore`](#explore) | Interactively explore image layers, files, and insights |
| [`ls`](#ls) | List image filesystem entries and layer provenance |
| [`cat`](#cat) | Write an image file to standard output |
| [`extract`](#extract) | Extract an image file or directory |
| [`compare metadata`](#compare-metadata) | Compare image configuration and platform metadata |
| [`compare layers`](#compare-layers) | Compare the layers of two images |
| [`compare files`](#compare-files) | Compare the file contents of two images |
| [`save-layers`](#save-layers) | Save image layers to disk |
| [`dockerfile`](#dockerfile) | Generate a Dockerfile from an image |

## Image sources

Image commands accept an explicit source prefix:

| Reference | Source |
|---|---|
| `alpine:latest` | Registry, preserving existing behavior |
| `registry://alpine:latest` | Registry, explicitly selected |
| `docker://myimage:dev` | Image already stored in Docker Engine |
| `docker://sha256:<full-image-id>` | Immutable Docker Engine image ID |

`docker://` does not pull missing images. A registry and a daemon can contain
different images with the same tag; Dredge never switches between them
automatically. Prefixes use `://` to avoid changing valid references such as
`docker:latest` or `registry:2`.

Only `sha256:` followed by exactly 64 lowercase hexadecimal characters is
treated as an image ID. Other valid references use normal image-name parsing,
including tags in a repository named `sha256`, such as `docker://sha256:dev`
or `docker://sha256:abc`.

Each comparison operand selects its source independently:

```console
dredge image compare layers docker://myimage:before docker://myimage:after
dredge image compare metadata registry://example.com/team/myimage:release docker://myimage:dev
dredge image explore docker://myimage:dev --compare registry://example.com/team/myimage:release
```

The reverse mixed-source order also works. In the explorer, a comparison given
as a tag alone refers to the current repository and source. Registry tag
browsing is not available for daemon images. If either comparison operand fails
to load, Dredge cancels the other acquisition and cleans up acquired snapshots.

### Docker connections

Docker Engine acquisition uses the .NET client directly; it does not require
an installed Docker CLI. Select a connection with `--docker-context <name>` or
`--docker-host <endpoint>`. These options are mutually exclusive and apply to
all daemon operands in one command.

Connection selection follows this order:

1. An explicit `--docker-context` or `--docker-host`.
2. `DOCKER_CONTEXT`.
3. `DOCKER_HOST`.
4. The active context in Docker's configuration.
5. The platform default socket or named pipe.

`DOCKER_CONFIG` selects Docker's configuration directory. Dredge resolves and
holds the selected connection for the operation. Invalid contexts, unsupported
transports, and unsupported TLS configurations produce errors rather than
falling back to another daemon. SSH contexts are not supported; use a supported
endpoint or a separately configured tunnel instead.

Copied explorer commands use `--docker-host` with the resolved endpoint, including
when the original selection came from a context or environment variable.
Changing Docker's active context does not redirect the copied command to another
daemon.

Unix sockets, Windows named pipes, and explicit non-TLS HTTP/TCP endpoints are
supported. Unix endpoints must name an absolute socket path, such as
`unix:///var/run/docker.sock`; relative paths such as `unix:docker.sock` are
rejected. HTTPS endpoints and Docker TLS configuration are rejected rather than
ignoring certificates or downgrading the connection. Prefer a local socket or
named pipe; a non-TLS endpoint must only be used over a trusted, protected
transport.

For Docker Desktop's Linux engine on Windows:

```console
dredge image explore docker://myimage:dev --docker-context desktop-linux
```

WSL does not have a separate image-source prefix. A Docker Engine inside WSL,
Docker Desktop, or a rootless engine is selected by its connection, not by
changing `docker://`. Dredge must have permission to access that endpoint.

### Identity and metadata

Dredge resolves daemon tags to full image IDs before acquiring content.
Retagging cannot redirect an in-progress operation. If the resolved content
becomes unavailable, Dredge fails instead of resolving the tag again.

Daemon source, selected platform, and immutable image ID are reported on
standard error, leaving JSON, Dockerfile, and file-content output on standard
output unchanged. Docker Engine's image ID is the daemon's immutable acquisition
identity. Depending on the engine's image store, it can identify a configuration
or a manifest. Dredge validates the exported configuration separately and does
not present daemon IDs as original registry manifest digests.

Daemon exports do not establish original registry manifest digests, annotations,
platform inventories, or compressed transfer sizes. Metadata comparisons
involving a daemon compare configuration metadata and report that distribution
metadata is unavailable. Layer comparisons involving a daemon use uncompressed
content digests (rootfs diff IDs). They do not compare a registry compressed
blob digest with a daemon raw-tar digest as though they identified the same
representation. Mixed-source layer comparisons verify registry layer content
against its declared diff IDs before reporting equality. Registry-only layer
comparisons retain their distribution-metadata behavior. Unavailable compressed
sizes are not reported as zero.
For content-based comparisons, Dredge uses SHA-256 identities after verifying
each declared diff ID. Identical decoded layers therefore compare equal even
when one image declares SHA-256 diff IDs and the other declares SHA-512.
The original configuration bytes and declared diff IDs remain unchanged.
When daemon acquisition has already verified decoded content, comparisons reuse
its canonical SHA-256 hashes without copying those layers into the content store
or hashing them again. Metadata-only acquisition does not provide this proof.
For registry layers declaring SHA-512 diff IDs, mixed-source verification
computes the declared SHA-512 hash and canonical SHA-256 hash in one decoded
pass. Both results are reused while the verified blob remains pinned.

Acquisition streams an export once to temporary storage and reads its members
through bounded ranges, without creating a second extracted copy of the export.
Within one source factory, acquisitions that resolve to the same daemon,
immutable image ID, platform, and content requirements share an export while
it remains in use. Layer-content and Linux-filesystem acquisitions share the
same verified export; metadata-only acquisitions remain separate. This includes
an explorer target that matches its baseline
and both operands of a comparison. Each acquisition still resolves and validates
its own reference and keeps an independent lease. Releasing the last lease
removes the shared entry and deletes the export; opening the image afterward
acquires a new export.
Snapshot directories and export files are accessible only to the current user.
Default snapshots use the user's local application-data directory even when
that directory does not exist yet; they do not fall back to the working directory.
The verified spool is retained through one shared read handle, so replacing its
pathname cannot redirect subsequent reads. Windows sharing denies writes while
that handle is open; Unix spools are made read-only after writing. Snapshots are
Dredge-owned working files, not a security boundary against a process that can
override the current user's filesystem protections.
Allow disk space for the export in addition to Dredge's normal cache.
Cold daemon filesystem and explorer indexes scan the verified archive members
directly instead of copying each member into the blob cache. Raw tar scans use
sequential buffering rather than one filesystem read per tar header. Raw layer
extraction uses the same sequential buffering. Metadata-only acquisitions hash
stored layer bytes with bounded sequential reads without decompressing them.
When a filesystem acquisition
starts a new verified export and no valid persisted index exists,
acquisition computes needed indexes and decoded-content proofs in one pass
over each raw layer or one inflation of each gzip layer.
A layer-limited filesystem operation prepares only its requested prefix;
all layers still undergo content-integrity verification. A warm index still
requires content verification, but its validated metadata is retained for
reuse rather than loaded a second time.
Daemon full-filesystem views use the verified configuration digest as their cache
key. `image ls` without `--layer` and root `image extract` can reuse a validated
complete view even when individual layer indexes have been evicted, without
preparing unused indexes or per-file proofs. During acquisition, root extraction
indexes only layers that supply surviving file content, including older content
retained by hard links. Missing indexes are built in the same pass that verifies
these layers, rather than rescanning them afterward. Acquisition still verifies
every layer's content, and restoration checks
the view against the verified layer identities. View prevalidation checks entries
without constructing a discarded filesystem. The loaded view is retained through
acquisition so the consumer does not read its metadata again.
Content-only
acquisitions do not eagerly scan tar entries; a filesystem caller sharing an
earlier content-only export builds any missing index afterward. Persisted indexes
and content-hash-validated reads remain available; operations that need a
complete cached blob can still publish one on demand.
For `image cat` without `--layer` and `image extract` of a non-root path,
acquisition verifies layer content without preparing indexes. Path resolution
then indexes layers from newest to oldest until it can resolve the requested
file. Directory extraction indexes all layers needed to include older children.
These operations do not parse unrelated earlier layers merely to acquire the
image. Raw decoded-content verification uses bounded sequential reads and
computes SHA-256 and any required SHA-512 hash together in one pass.
Daemon layer saving and file comparisons extract directly from the export,
without first publishing another complete layer copy to the blob cache or
preparing unused filesystem indexes. Daemon explorer-baseline verification
also avoids preparing indexes that it does not consume.
When acquisition has verified the requested layer size and decoded diff ID,
indexing reuses that proof rather than hashing the complete layer again;
individual file hashes are still computed. Selected files in raw daemon layers
are hash-validated and read directly from bounded export handles, without
staging their contents to scratch files. Reads remain restricted to the
validated file extents, and open handles survive release of the export snapshot.
Fresh index scans retain their immutable index as the file-validation proof in
the live export, without copying every entry into a second proof table. Selecting
a freshly scanned file checks its exact coordinates against that index instead
of hashing its contents again.
Successful raw and gzip file validation is reused for repeated selections in the same
live export, keyed by layer digest, encoding, offset, size, and file hash. Persisted
indexes alone provide no such proof. Changed index coordinates, failed validation,
and a new export require validation again.
Gzip proofs additionally require the same validated compressed-prefix length;
reading the preview still performs the decompression needed to reach its bytes.
Batch selections reuse per-file proofs only when their shared prefix is a proven
extent that covers every selected file. Fully proven selections skip the
validation buffer and content-wrapper allocation.
Raw and gzip daemon selections use the export even when a complete cached blob exists,
avoiding a whole-cache digest pass for a small selection. Gzip daemon reads also
validate and rewind bounded export prefixes directly instead of copying the
compressed prefix to scratch. Healthy archive-only selections do not hold the
cache-publication lock, so independent selections can validate concurrently;
invalid-index fallback still locks and rechecks the complete cache. Registry
range staging is unchanged.
Mixed daemon/registry comparisons verify an unpinned persisted registry blob's
encoded digest and decoded diff ID in one pass. A corrupt encoded cache entry
is still discarded and downloaded again; valid encoded content with an invalid
diff ID or gzip payload still fails rather than being downloaded unnecessarily.
Repeated content-proof lookups also reuse the current cache scope's existing
blob pin without reopening the cache-publication lock; another scope still
acquires its own pin.
The explorer retains the displayed comparison target and at most one
inactive target, in addition to the baseline, for both registry and daemon
sources. In-flight readers and queued updates keep their snapshots alive until
they finish. Eviction releases the filesystem, any daemon export or target-owned
registry client, and target-owned cache pins. Layers still used by the baseline
or another target remain pinned. Unused cached blobs become eligible for normal
cache trimming.
Registry manifest and platform snapshots and daemon image IDs remain fixed for
the explorer session. Revisiting an evicted target rebuilds its filesystem from
the retained snapshot or immutable daemon ID, without resolving a mutable tag
again. If that daemon ID is no longer available or the daemon has changed, the
comparison fails instead of refreshing the tag. Reopen the explorer to refresh a tag.
Configuration and stored blob identities are validated before analysis;
content-based operations additionally validate decoded layer diff IDs.
Linux daemon `os` verifies only the base layer's configured diff ID directly
against its export member, then parses that member without publishing a cached
blob. Raw SHA-256 layers can reuse the acquisition's encoded-digest proof;
gzip layers and SHA-512 diff IDs retain the required decoded-content hashing.
Decoded-content verification supports SHA-256 and SHA-512 diff IDs in both
registry images and raw or gzip Docker exports.
Repeated layers and overlapping images reuse successful content verification
while their validated blobs remain pinned in the same content store.
When indexing a cold registry layer for a filesystem or explorer, Dredge hashes
the complete decoded layer, including headers, padding, and bytes after the
last tar entry. Subsequent diff-ID verification can reuse that scan while the
blob remains pinned. A persisted index alone is not proof of the decoded
content and does not skip verification.
Mixed-source explorer acquisition, base verification, and comparisons share
that store. Mixed-base verification indexes the required registry prefix first
so content verification can reuse the cold scan; it does not index the remaining
target layers before they are needed. Independent registry layers verify with
bounded concurrency.
Daemon acquisition verifies up to four distinct archive members concurrently;
repeated members share verification, while each layer descriptor is checked.
For raw layers declaring SHA-512 diff IDs, acquisition computes SHA-256 and
SHA-512 in one read of each member.
The existing filesystem, package inventory, and provenance
analyses apply to daemon images.

Daemon `inspect`, `dockerfile`, and `compare metadata` do not require decoding
layers, so an OCI zstd-layer export can still be inspected and its configuration
compared. Filesystem operations and verified content-layer comparisons support
raw and gzip tar layers, not zstd. Linux `os` identification also needs readable
layer content and verifies the decoded base layer against its declared diff ID
before reporting OS information. Missing or empty optional history does not
prevent acquisition.
Windows `os` identification and Dockerfile base-image tags use the selected
platform's OS version when the exported configuration omits `os.version`.
The configuration JSON remains unchanged.

### Command and platform support

| Commands | Registry | Linux daemon | Windows daemon |
|---|---|---|---|
| `inspect`, `dockerfile` | Existing support | Supported | Supported |
| `os` | Linux and Windows | Supported | Metadata identification; errors if the OS cannot be identified |
| `compare metadata`, `compare layers` | Existing support | Supported, including mixed sources | Supported without filesystem analysis |
| `explore`, `ls`, `cat`, `extract`, `compare files` | Existing platform limits | Supported | Not supported |
| `save-layers` | Existing support | Supported | No Windows filesystem extraction support |

Windows OS identification and Dockerfile base reconstruction can require
registry metadata from Microsoft's registry, just as registry-backed Windows
images do. This is metadata lookup, not a fallback pull of the selected image.
Unsupported Windows filesystem operations fail after pinned image inspection,
before exporting the image's layers.

Daemon platform filters are checked against the actual image configuration.
Dredge does not pull another platform or silently substitute the daemon's
default platform. Engine/client limitations can require a concrete
platform-specific image ID rather than a multi-platform tag.

Manifest, repository, tag, referrer, and deletion commands retain their registry
semantics; they do not acquire daemon images. Native Podman/containerd providers
are not included.

## Inspect

Returns the image configuration of the specified image.

```console
dredge image inspect <image> [--os <os>] [--arch <arch>] [--os-version <version>]
```

Example:

```console
dredge image inspect amd64/ubuntu:22.04
{
  "architecture": "amd64",
  "config": {
    "Hostname": "",
    "Domainname": "",
    "User": "",
    "AttachStdin": false,
    "AttachStdout": false,
--- <cut> ---
  "os": "linux",
  "rootfs": {
    "type": "layers",
    "diff_ids": [
      "sha256:f4a670ac65b68f8757aea863ac0de19e627c0ea57165abad8094eae512ca7dad"
    ]
  }
}
```

## OS

Returns information about the OS of the specified image. Supports both Linux and Windows images.

```console
dredge image os <image> [--os <os>] [--arch <arch>] [--os-version <version>]
```

### Linux example

```console
dredge image os amd64/ubuntu:22.04
{
  "PRETTY_NAME": "Ubuntu 22.04.1 LTS",
  "NAME": "Ubuntu",
  "ID": "ubuntu",
  "ID_LIKE": [
    "debian"
  ],
  "VERSION": "22.04.1 LTS (Jammy Jellyfish)",
  "VERSION_ID": "22.04",
  "VERSION_CODENAME": "jammy",
  "HOME_URL": "https://www.ubuntu.com/",
  "SUPPORT_URL": "https://help.ubuntu.com/",
  "BUG_REPORT_URL": "https://bugs.launchpad.net/ubuntu/",
  "PRIVACY_POLICY_URL": "https://www.ubuntu.com/legal/terms-and-policies/privacy-policy"
}
```

### Windows example

```console
dredge image os mcr.microsoft.com/windows/nanoserver:ltsc2022-amd64
{
  "Type": "Nano Server",
  "Version": "10.0.20348.1249"
}
```

## Explore

Opens a full-screen, interactive explorer for an image. You can browse
its layers, see which files each layer adds, changes, or deletes, find wasted
space, and compare it with another version of the image.

```console
dredge image explore <image> [--layer <n>] [--compare <image-or-tag>] [--base-image <image> ...] [--no-mouse] [--os <os>] [--arch <arch>] [--os-version <version>]
```

- `--layer <n>` starts with layer `n` selected.
- `--compare <image-or-tag>` starts by comparing the image with another one.
  A bare tag, such as `--compare 2.0`, refers to the same repository.
- `--base-image <image>` marks which layers come from a base image. Repeat it
  to indicate several images comprising an image hierarchy.
  If you leave it out, the explorer uses the base image recorded in the
  image's annotations, when there is one.
- `--no-mouse` turns off mouse support so you can select text in the terminal
  normally.

### Browsing layers and files

The explorer opens with a list of layers on one side and the files they
change on the other. Select a layer to see its files, press `Tab` to move
between the two panes, and press `a` to see the whole filesystem instead of
just that layer's changes. Press `Enter` to preview a file, `o` to open it in
your text viewer, and `Esc` to go back.

### Searching and finding wasted space

Press `/` to search for a path across every layer. Press `i` to see an
efficiency score along with the files that waste space, such as files that
are overwritten or deleted by a later layer. Selecting a result takes you to
its files, and `Esc` brings you back to the list.

### Packages

Press `p` to see the packages installed as of the selected layer, including
those inherited from earlier layers. Dredge recognizes npm, dpkg, apk, pip,
and NuGet packages. Type `/` to filter the list, or press `Enter` to see a
package's details.

Package inventory and package comparison are unavailable for Windows images.

### Comparing images

Press `c`, enter a tag or image reference, and choose whether to compare
**Files** or **Packages**. The image you opened is the baseline, and `s`
swaps the two sides. Press `Enter` on a changed file to see a diff, or on a
changed package to see which of its files changed.

In a file diff, `Alt+V` switches between inline and side-by-side views.

### File history

While previewing a file, press `Alt+H` to see every layer that touched it.
Select a version to preview it, or press `Alt+D` to compare it with the
version before.

Windows historical previews show Windows attributes instead of Unix
permissions and ownership. Historical and cross-image diffs report attribute and raw-metadata
fingerprint changes even when file contents are identical; they do not
interpret ACL changes. "No text differences" means the decoded text matches,
not necessarily the file bytes: encoding signatures and line endings can differ.

### Extracting and copying

Press `x` to extract the selected file or directory to your machine. Press
`Ctrl+C` to copy the equivalent `dredge image` command so you can run it
later or share it. On Linux, macOS, and over SSH, copying depends on your
terminal supporting OSC 52 clipboard access.

Windows extraction is a content-only export with the limitations described
under [Windows filesystem support](#windows-filesystem-support).

### Keys and mouse

Press `?` at any time to see the available keys.

| Key | Action |
|-----|--------|
| `↑` `↓` `PgUp` `PgDn` `Home` `End` | Move |
| `Tab` | Switch pane |
| `[` `]` | Previous or next layer, or next difference when comparing |
| `←` `→` | Collapse or expand a directory |
| `Enter` | Open the selected item |
| `Esc` | Go back |
| `a` | Show the whole filesystem |
| `+` `~` `=` `-` | Show or hide added, modified, unchanged, or deleted files |
| `/` | Search or filter |
| `i` | Efficiency insights |
| `p` | Packages |
| `c` | Compare with another image |
| `s` | Swap compared images |
| `Alt+V` | Switch diff layout |
| `Alt+H` | File history |
| `x` | Extract |
| `Ctrl+C` | Copy the `dredge` command |
| `o` | Open in text viewer |
| `q` | Quit |

You can also use the mouse: click to select, double-click to open, and scroll
with the wheel.

The theme, mouse support, and text viewer can be changed in the
[`explore` settings](../settings.md).

## Ls

Lists the effective filesystem entries in an image without extracting the
complete image.

```console
dredge image ls <image> [path] [-l|--long] [--provenance] [--recursive] [--show-deleted] [--layer <index>] [--output <text|json>] [--os <os>] [--arch <arch>] [--os-version <version>]
```

The command lists direct children of the image root or selected directory by
default. Use `--recursive` to list all descendants. If `path` identifies a
file or link, the command lists only that entry. Paths may start with `/`.

Text output uses terminal-width name columns relative to the listed directory
by default, similar to `ls`. Redirected output uses one name per line without
terminal padding. Use `-l` or `--long` to show file type and mode, UID, GID,
size, UTC modification time (marked with `Z`), and symbolic-link target. Use
`--provenance` independently or with `--long` to show the layer index and an
abbreviated digest for the layer that introduced, modified, or deleted each
path. Text output labels these values `i`, `m`, and `d`, respectively.

For Windows entries, long output shows `win:` followed by hexadecimal Windows
file attributes and `?` for unavailable UID/GID values. It does not interpret
the tar mode as Windows permissions. JSON adds a `windows` object containing
available attributes, mountpoint/security-descriptor indicators, a raw-metadata
fingerprint, and any entry-specific limitation. Legacy `mode`, `userId`, and
`groupId` JSON fields describe the archive, not Windows security.

For example, list the periodic task directories in Alpine. Piping the output
shows the redirected, one-name-per-line format:

```console
$ dredge image ls alpine:3.22.1 /etc/periodic | cat
15min
daily
hourly
monthly
weekly
```

This multi-layer .NET image shows that `/etc/apk/world` was introduced by
layer 0 and modified by layer 1:

```console
$ dredge image ls mcr.microsoft.com/dotnet/runtime-deps:10.0.8-alpine3.23-amd64 /etc/apk/world -l --provenance
-rw-r--r-- 0 0 127 2026-05-12 05:31Z etc/apk/world  i=0:6a0ac1617861 m=1:243c8d038cfe
```

Ordinary and opaque OCI whiteouts remove entries from the effective
filesystem. Removed entries are hidden by default. Use `--show-deleted` to
include them; combine it with `--provenance` to show the layer that removed
them.

Use `--layer <index>` to list the filesystem state after applying layers
through that zero-based index. Without this option, the command lists the final
image state. The index must refer to a layer in the image.

Use `--output json` for camel-cased
machine-readable output; text detail options do not alter JSON:

```console
$ dredge image ls alpine:3.22.1 /etc/alpine-release --output json
[
  {
    "path": "etc/alpine-release",
    "type": "File",
    "mode": 420,
    "userId": 0,
    "groupId": 0,
    "size": 7,
    "modifiedTime": "2025-07-15T10:41:41Z",
    "introducedLayer": {
      "index": 0,
      "digest": "sha256:9824c27679d3b27c5e1cb00a73adb6f4f8d556994111c12db3c5d61a0c843df8"
    }
  }
]
```

## Cat

Writes the effective contents of one image file to standard output.
Standard output contains only file bytes, so the command is safe to use in a
pipeline or redirect to a binary file.

```console
dredge image cat <image> <path> [--layer <index>] [--os <os>] [--arch <arch>] [--os-version <version>]
```

Use `--layer <index>` to read the file contents from the filesystem state after
applying layers through that zero-based index. Without this option, the command
reads from the final image state. The index must refer to a layer in the image.

The command follows symbolic links with Linux path semantics and follows hard
links to their image-layer content. Link resolution cannot escape the image
root and fails for dangling links or link loops. The command rejects
directories, deleted paths, and unsupported file types.

Windows images use Windows path and link semantics instead; see
[Windows filesystem support](#windows-filesystem-support).

For example:

```console
$ dredge image cat alpine:3.22.1 /etc/alpine-release
3.22.1
```

## Extract

Extracts one effective file or a directory subtree from an image. For a
directory, Dredge reads the effective entries from all layers but writes only
the selected subtree.

```console
dredge image extract <image> <path> <output-path> [--os <os>] [--arch <arch>] [--os-version <version>]
```

`output-path` must not exist. Dredge validates archive paths and destination
containment before writing. Destination name collisions are rejected without
overwriting existing entries. Use `/` as `path` to extract the complete
effective image filesystem.

For example:

```console
$ dredge image extract alpine:3.22.1 /etc/alpine-release alpine-release
$ cat alpine-release
3.22.1
```

For Linux images, Dredge preserves modification times and, on Unix hosts, available file modes.
It reports UID and GID through `image ls` but does not apply image
ownership to the host. Extraction preserves safe hard and symbolic links when
possible. Symbolic links retain their original targets, including dangling
targets and targets outside the selected subtree. Hard links whose targets are
also selected remain hard links; otherwise, Dredge materializes their effective
content. Archive entries and extraction destinations cannot escape their
respective roots.

The `explore`, `ls`, `cat`, and `extract` commands support gzip-compressed tar
layers for Linux and the Windows representations described below.

### Windows filesystem support

Paths are case-insensitive. You can use `C:\Windows\System32`,
`Windows\System32`, or `/Windows/System32`; listings show paths relative to
the image root.

Keep these limitations in mind:

- Supported images use gzip-compressed tar layers. Unsupported formats report
  an error.
- This is a filesystem view, not a complete Windows installation inventory.
  Registry hives, VM data, installed software, and full Windows security
  metadata are not inspected. Reported sizes cover supported file contents,
  not total disk usage.
- Extraction is content-only: it exports supported files and directories,
  preserves available modification times, and copies hard-linked file contents.
  It does not restore permissions, alternate data streams, or Windows links.
  Subtrees containing unsupported entries or symbolic links/junctions are
  rejected before output is created. Choose an output path that does not exist.

### Shared layer cache

`explore`, `ls`, `cat`, `extract`, `save-layers`, `compare files`, and Linux `os` share a
persistent cache to reduce repeated layer downloads. Registry access is still
required, even when image layers are cached.

See [Configure the layer cache](../settings.md#configure-the-layer-cache) for
cache locations, storage limits, and cleanup.

## Compare metadata

Compares image configuration, manifest descriptors, and platform metadata
without downloading image layers.

```console
dredge image compare metadata <base> <target> [--output <format>] [--no-color] [--os <os>] [--arch <arch>] [--os-version <version>]
```

| Option | Description |
|--------|-------------|
| `--output` | Output format: `side-by-side` (default), `inline`, or `json` |
| `--no-color` | Disable color output and use text-based diff indicators instead |

The comparison includes:

- Available platforms and their descriptors
- Manifest media types, annotations, config descriptors, and layer descriptors
- Image creation, author, operating system, architecture, and root filesystem metadata
- Entrypoint, command, environment variables, labels, exposed ports, volumes, user, and working directory
- Image history, including creation time, command, author, comment, and empty-layer status

For a multi-platform reference, Dredge compares the complete platform index.
The platform options select the per-platform manifests and image configurations
for the deeper comparison. Dredge uses the configured
[platform resolution](../platform-resolution.md) defaults when platform options
are omitted.

Example:

```console
dredge image compare metadata --output inline mcr.microsoft.com/dotnet/runtime:9.0.0 mcr.microsoft.com/dotnet/runtime:10.0.0 --os linux --arch amd64
```

```diff
- Config.environment["DOTNET_VERSION"] = "9.0.0"
+ Config.environment["DOTNET_VERSION"] = "10.0.0"
```

The command returns the following exit codes:

| Exit code | Meaning |
|----------:|---------|
| `0` | The comparison completed, whether the metadata is equal or different |
| `1` | The command failed before completing the comparison |

## Compare layers

Compares the layers of two images.
Images without optional configuration history still compare every manifest
layer. `--history` adds instructions when available; missing history does not
make different layers equal.

```console
dredge image compare layers <base> <target> [--output <format>] [--history] [--compressed-size] [--no-color] [--os <os>] [--arch <arch>] [--os-version <version>]
```

| Option | Description |
|--------|-------------|
| `--output` | Output format: `side-by-side` (default), `inline`, or `json` |
| `--history` | Include the layer history (Dockerfile instructions) |
| `--compressed-size` | Show compressed layer sizes |
| `--no-color` | Disable color output and use text-based diff indicators instead |

The command returns the following exit codes:

| Exit code | Meaning |
|----------:|---------|
| `0` | The images have equal layers |
| `1` | The command failed before completing the comparison |
| `2` | The comparison completed and found layer differences |

See [Exit codes](../exit-codes.md) for the general exit-code convention.

### Inline output example

```diff
dredge image compare layers --output inline amd64/node:19.1-alpine amd64/node:19.2-alpine
  sha256:ca7dd9ec2225f2385955c43b2379305acd51543c28cf1d4e94522b3d94cce3ce
- sha256:4487691952c066cb3964b94606825bc96c698377909c7d74c889fd12e24e36a7
+ sha256:bfebca31f7556839677aca8626941ec4be0d5e2a1a59f1bd991807828de37167
- sha256:206c50ffab466a0ed68db742d6d2015abcedd0a0b2500babb1938ce2a272b425
+ sha256:cc0056ab0c4160f34cd7046016f9aa6d1d14c206f61768b34efa69c45c38a0cb
- sha256:f6d4361cf153f2e83958f504356eef6e3d041eb3c4d23da466480ee2dfe656ae
+ sha256:6e25476b6324255c964f6b86e587d867e79046e94933123d0f1312dbddfe87b7
```

### Side-by-side output with history example

```console
dredge image compare layers --history --no-color mcr.microsoft.com/dotnet/runtime:6.0.5-jammy-amd64 mcr.microsoft.com/dotnet/runtime:6.0.6-jammy-amd64
┌──────────────────────────────────────────────────────────────────────────┬───────────┬─────────────────────────────────────────────────────────────────────────┐
│ mcr.microsoft.com/dotnet/runtime:6.0.5-jammy-amd64                       │  Compare  │ mcr.microsoft.com/dotnet/runtime:6.0.6-jammy-amd64                      │
├──────────────────────────────────────────────────────────────────────────┼───────────┼─────────────────────────────────────────────────────────────────────────┤
│ sha256:405f018f9d1d0f351c196b841a7c7f226fb8ea448acd6339a9ed8741600275a2  │   Equal   │ sha256:405f018f9d1d0f351c196b841a7c7f226fb8ea448acd6339a9ed8741600275a2 │
│ /bin/sh -c #(nop) ADD                                                    │           │ /bin/sh -c #(nop) ADD                                                   │
│ file:11157b07dde10107f3f6f2b892c869ea83868475d5825167b5f466a7e410eb05 in │           │ file:11157b07dde10107f3f6f2b892c869ea83868475d5825167b5f466a7e410eb05   │
│ /                                                                        │           │ in /                                                                    │
│                                                                          │           │                                                                         │
│ <empty layer>                                                            │   Equal   │ <empty layer>                                                           │
│ /bin/sh -c #(nop)  CMD ["bash"]                                          │           │ /bin/sh -c #(nop)  CMD ["bash"]                                         │
│                                                                          │           │                                                                         │
│ sha256:7f5199084fb2409a567d45cbe1eebb7ad2bb92d2f2eeac1f9d7d1521b6529da5  │ Not Equal │ sha256:1d6b7ed86f8a0efb7b44af3ac71d881ea686c7e26f2bf9b509ffcee50d503a44 │
│ /bin/sh -c apt-get update     && apt-get install -y                      │           │ /bin/sh -c apt-get update     && apt-get install -y                     │
│ --no-install-recommends         ca-certificates                 libc6    │           │ --no-install-recommends         ca-certificates                 libc6   │
│ libgcc1         libgssapi-krb5-2         libicu70         libssl3        │           │ libgcc1         libgssapi-krb5-2         libicu70         libssl3       │
│ libstdc++6         zlib1g     && rm -rf /var/lib/apt/lists/*             │           │ libstdc++6         zlib1g     && rm -rf /var/lib/apt/lists/*            │
│                                                                          │           │                                                                         │
│ <empty layer>                                                            │   Equal   │ <empty layer>                                                           │
│ /bin/sh -c #(nop)  ENV ASPNETCORE_URLS=http://+:80                       │           │ /bin/sh -c #(nop)  ENV ASPNETCORE_URLS=http://+:80                      │
│ DOTNET_RUNNING_IN_CONTAINER=true                                         │           │ DOTNET_RUNNING_IN_CONTAINER=true                                        │
│                                                                          │           │                                                                         │
│ <empty layer>                                                            │ Not Equal │ <empty layer>                                                           │
│ /bin/sh -c #(nop)  ENV DOTNET_VERSION=6.0.5                              │           │ /bin/sh -c #(nop)  ENV DOTNET_VERSION=6.0.6                             │
│                                                                          │           │                                                                         │
│ sha256:ae2c6691208b45534916003bf6e5607998bab42aa923dc5f1e21fc244f0a9832  │ Not Equal │ sha256:18a715d5177a41204dd062b5760565bd282526e22063ab158b4180833f5a5156 │
│ /bin/sh -c #(nop) COPY                                                   │           │ /bin/sh -c #(nop) COPY                                                  │
│ dir:49b45e3ccadd0521a7513b91e6cb00a52ff23f9e8004ce74c832042e93fe7e33 in  │           │ dir:fb7195f4bc42fce62a7104cc5ef211701a1267b4666b445b59f649b0f86ecaa6 in │
│ /usr/share/dotnet                                                        │           │ /usr/share/dotnet                                                       │
│                                                                          │           │                                                                         │
│ sha256:114810c4073fb2a42557832ebfa76ec9a0f3ddcd13edf20b9f6d690f0d0be720  │ Not Equal │ sha256:6adc839fa9c17fc4a0f1965aa58b446f6531b4b926995080404321a223ce82b2 │
│ /bin/sh -c ln -s /usr/share/dotnet/dotnet /usr/bin/dotnet                │           │ /bin/sh -c ln -s /usr/share/dotnet/dotnet /usr/bin/dotnet               │
└──────────────────────────────────────────────────────────────────────────┴───────────┴─────────────────────────────────────────────────────────────────────────┘
```

## Compare files

Compares the files in two images. Dredge downloads each image, applies its
layers to a temporary directory, and starts the configured external comparison
tool.

Before running this command, [configure the file comparison
tool](../settings.md#configure-the-file-comparison-tool).

```console
dredge image compare files <base> <target> [--base-layer-index <n>] [--target-layer-index <n>] [--output <type>] [--os <os>] [--arch <arch>] [--os-version <version>]
```

| Option | Description |
|--------|-------------|
| `--base-layer-index` | Apply base-image layers from index `0` through this zero-based index |
| `--target-layer-index` | Apply target-image layers from index `0` through this zero-based index |
| `--output` | Output type. The supported and default value is `external-tool` |

Example — compare two images:

```console
dredge image compare files amd64/node:19.1-alpine amd64/node:19.2-alpine
```

Example — compare only the first two layers:

```console
dredge image compare files amd64/node:19.1-alpine amd64/node:19.2-alpine --base-layer-index 1 --target-layer-index 1
```

Example — compare layers within a single image (difference between the 2nd and 3rd layer):

```console
dredge image compare files amd64/node:19.1-alpine amd64/node:19.1-alpine --base-layer-index 1 --target-layer-index 2
```

## Save layers

Saves the extracted layers of an image to disk.

```console
dredge image save-layers <image> <output-path> [--no-squash] [--layer-index <n>] [--force] [--os <os>] [--arch <arch>] [--os-version <version>]
```

Set `output-path` to a new path or an existing regular directory. By default,
an existing directory must be empty. Use `--force` to write to a non-empty
directory.

| Option | Description |
|--------|-------------|
| `--no-squash` | Save each selected layer in a separate directory instead of merging the layers |
| `--layer-index` | Select a zero-based layer index. With squashing, Dredge applies layers `0` through this index. With `--no-squash`, Dredge saves only this layer |
| `--force` | Allow writes to a non-empty output directory. Squashed output may overwrite, replace, or delete entries, including entries targeted by OCI whiteouts. With `--no-squash`, Dredge replaces each matching `layer<index>-<digest>` directory |

Example:

```console
dredge image save-layers amd64/node:19.2-alpine out/layers/node
```

## Dockerfile

Generates a Dockerfile that represents an image.

```console
dredge image dockerfile <image> [--no-format] [--no-color] [--os <os>] [--arch <arch>] [--os-version <version>]
```

| Option | Description |
|--------|-------------|
| `--no-format` | Disable heuristic line-break formatting |
| `--no-color` | Disable syntax coloring |
