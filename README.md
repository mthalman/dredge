# Dredge: A Docker Registry Client CLI

Dredge is a CLI built on .NET that provides a simple way to execute commands on a Docker registry's HTTP API. Currently, only read operations are supported.

## Install

Dredge is published as a [.NET tool](https://docs.microsoft.com/en-us/dotnet/core/tools/global-tools) and available as a [NuGet package](https://www.nuget.org/packages/Valleysoft.Dredge).

### Installing as a global tool

```console
> dotnet tool install -g Valleysoft.Dredge
```

## Usage

### Notes

> * Dredge relies on your credentials already being stored in your environment when targeting registries that require authentication. For those registries you need to run `docker login` (for Docker Hub) or `docker login <registry>` before running Dredge.

### Query Repositories

```console
> dredge repo list contoso.azurecr.io
[
  "myrepo/app"
  "myrepo/app2"
]
```

### Query Tags

```console
> dredge tag list contoso.azurecr.io/myrepo/app
[
  "1.0",
  "1.1"
]
```

### Query Manifest

```console
> dredge manifest get contoso.azurecr.io/myrepo/app:1.0
{
  "config": {
    "mediaType": "application/vnd.docker.container.image.v1+json",
    "size": 1508,
    "digest": "sha256:a5f12aa2470df1d32034c6707c8041158b652f38d2a9ae3d7ad7e7532d22ebe0",
    "urls": []
  },
  "layers": [
    {
      "mediaType": "application/vnd.docker.image.rootfs.diff.tar.gzip",
      "size": 2796860,
      "digest": "sha256:927c0c94c7c576fff0792aca7ec73d67a2f7f4cb3a6e53a84559337260b36964",
      "urls": []
    }
  ],
  "mediaType": "application/vnd.docker.distribution.manifest.v2+json",
  "schemaVersion": 2
}
```

### Query Digest

```console
> dredge manifest digest contoso.azurecr.io/myrepo/app:1.0
sha256:95202993700f8cd7aba8496c2d0e57be0666e80b4c441925fc6f9361fa81d10e
```
