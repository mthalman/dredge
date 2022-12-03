# Dredge: A Container Registry Client CLI

Dredge is a CLI built on .NET that provides a simple way to execute commands on a container registry's HTTP API. Currently, only read operations are supported.

## Features

* Access to raw JSON data from the registry's HTTP API.
* Extended, derived data such as [image configuration](docs/images.md#inspect-image-configuration), [OS information](docs/images.md#image-os-information), and comparison of [layers](docs/images.md#compare-image-layers) and [files](docs/images.md#compare-image-files).

### Notes

> Dredge relies on your credentials already being stored in your environment when targeting registries that require authentication. For those registries you need to run `docker login` (for Docker Hub) or `docker login <registry>` before running Dredge.

### Commands

* [`image`](docs/images.md)
* [`manifest`](docs/manifests.md)
* [`repo`](docs/repositories.md)
* [`settings`](docs/settings.md)
* [`tag`](docs/tags.md)

## Install

Dredge is available as a standalone executable or as a [.NET tool](https://docs.microsoft.com/en-us/dotnet/core/tools/global-tools).

### Installing as a standalone executable

Download the desired executable from the [release page](https://github.com/mthalman/dredge/releases).

The executable does not require the .NET runtime to be installed.

### Installing as a .NET global tool

```console
> dotnet tool install -g Valleysoft.Dredge
```
