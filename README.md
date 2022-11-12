# Dredge: A Container Registry Client CLI

Dredge is a CLI built on .NET that provides a simple way to execute commands on a container registry's HTTP API. Currently, only read operations are supported.

## Install

Dredge is available as a standalone executable or as a [.NET tool](https://docs.microsoft.com/en-us/dotnet/core/tools/global-tools).

### Installing as a standalone executable

Download the desired executable from the [release page](https://github.com/mthalman/dredge/releases).

The executable does not require the .NET runtime to be installed.

### Installing as a .NET global tool

```console
> dotnet tool install -g Valleysoft.Dredge
```

Requires a minimum version of .NET Core 3.1. To access all available features, use .NET 7.

## Features

### Notes

> * Dredge relies on your credentials already being stored in your environment when targeting registries that require authentication. For those registries you need to run `docker login` (for Docker Hub) or `docker login <registry>` before running Dredge.

### Query Repositories

Returns the list of repositories from the specified registry.

> Not supported for Docker Hub.

```console
> dredge repo list mcr.microsoft.com
[
  "acc/samples/acc-perl",
  "acc/samples/attestation-inproc",
  "acc/samples/attestation-outproc",
  "acc/samples/attested-tls-inproc",
  "acc/samples/attested-tls-outproc",
--- <cut> ---
  "windows/servercore/iis",
  "windows/servercore/iis/insider",
  "windows/servercore/insider",
  "windowsprotocoltestsuites",
  "wwllab/skills/skills-extractor-cognitive-search"
]
```

### Query Tags

Returns the tags associated with the specified repository.

```console
> dredge tag list ubuntu
[
  "10.04",
  "12.04",
  "12.04.5",
  "12.10",
  "13.04",
--- <cut> ---
  "zesty-20170703",
  "zesty-20170913",
  "zesty-20170915",
  "zesty-20171114",
  "zesty-20171122"
]
```

### Query Manifest

Returns the manifest of the specified name.

```console
> dredge manifest get ubuntu:22.04
{
  "manifests": [
    {
      "mediaType": "application/vnd.docker.distribution.manifest.v2+json",
      "size": 529,
      "digest": "sha256:817cfe4672284dcbfee885b1a66094fd907630d610cab329114d036716be49ba",
      "platform": {
        "architecture": "amd64",
        "os": "linux",
        "os.version": null,
        "os.features": [],
        "variant": null,
        "features": []
      }
    },
--- <cut> ---
    {
      "mediaType": "application/vnd.docker.distribution.manifest.v2+json",
      "size": 529,
      "digest": "sha256:75f39282185d9d952d5d19491a0c98ed9f798b0251c6d9a026e5b71cc2bf4de3",
      "platform": {
        "architecture": "s390x",
        "os": "linux",
        "os.version": null,
        "os.features": [],
        "variant": null,
        "features": []
      }
    }
  ],
  "mediaType": "application/vnd.docker.distribution.manifest.list.v2+json",
  "schemaVersion": 2
}
```

### Query Digest

Returns the digest of the specified name.

```console
> dredge manifest digest ubuntu:22.04
sha256:4b1d0c4a2d2aaf63b37111f34eb9fa89fa1bf53dd6e4ca954d47caebca4005c2
```

### Resolve Manifest

Resolves a manifest to a target platform's fully-qualified image digest. This is useful when you want to get the image digest of a specific platform from a multi-arch tag.

```console
> dredge manifest resolve ubuntu:22.04 --os linux --arch amd64
library/ubuntu@sha256:817cfe4672284dcbfee885b1a66094fd907630d610cab329114d036716be49ba
```

### Inspect Image Configuration

Returns the image configuration of the specified image name.

```console
> dredge image inspect amd64/ubuntu:22.04
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

### Image OS Information

Returns information about the OS of the specified image name. Supports both Linux and Windows images.

#### Linux

```console
> dredge image os amd64/ubuntu:22.04
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

#### Windows

```console
> dredge image os mcr.microsoft.com/windows/nanoserver:ltsc2022-amd64
{
  "Type": "Nano Server",
  "Version": "10.0.20348.1249"
}
```
