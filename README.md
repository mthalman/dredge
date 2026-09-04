<img src="dredge-logo.png" width="250" alt="Dredge">

# Dredge

Dredge is a .NET command-line tool for querying container registry HTTP APIs
defined by the [OCI Distribution Specification](https://github.com/opencontainers/distribution-spec).
Dredge does not modify registry content.

## Features

- Query raw JSON data for [manifests](docs/commands/manifests.md),
  [tags](docs/commands/tags.md), [repositories](docs/commands/repositories.md),
  and [referrers](docs/commands/referrers.md).
- Inspect and retrieve OCI artifacts or check for required artifact types in CI.
- Inspect an image's [configuration](docs/commands/images.md#inspect) and
  [operating system information](docs/commands/images.md#os).
- Browse, read, and selectively [extract files from Linux
  images](docs/commands/images.md#ls) with layer provenance.
- Compare [layers](docs/commands/images.md#compare-layers) or
  [files](docs/commands/images.md#compare-files) between images.
- [Generate a Dockerfile](docs/commands/images.md#dockerfile) from an image.
- [Save image layers](docs/commands/images.md#save-layers) as a merged
  filesystem or as separate directories.
- Select a platform from a multi-platform image through
  [platform resolution](docs/platform-resolution.md).

See the [Dredge documentation](docs/README.md) for the complete command
reference and configuration guides.

## Install Dredge

Choose one installation method.

### Release executable

Download from the [release page](https://github.com/mthalman/dredge/releases).
Select the executable for your operating system and architecture.

The release executable requires the
[.NET 10 runtime](https://dotnet.microsoft.com/download/dotnet/10.0).

### Container

```shell
docker run --rm ghcr.io/mthalman/dredge --help
```

When following command examples, replace `dredge` with
`docker run --rm ghcr.io/mthalman/dredge`.

### .NET global tool

```console
dotnet tool install -g Valleysoft.Dredge
```

## Query a registry

Run a read-only command against a public image:

```console
dredge manifest digest alpine:latest
sha256:...
```

If the registry requires credentials, see
[Authenticate to a registry](docs/authentication.md).
