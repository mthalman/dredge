# Contributing

Contributions are welcome. Submit a pull request or open an issue as necessary.

## Developer Prerequisites

* [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)
* [Docker](https://docs.docker.com/get-docker/) (optional, for container builds)

## Build and Test

All commands are run from the `src/` directory:

```shell
dotnet restore
dotnet build -c Release --no-restore
dotnet test --no-restore -v normal -c Release
```

To run a single test class:

```shell
dotnet test --no-restore --filter "ClassName=Valleysoft.Dredge.Tests.CompareLayersCommandTests"
```
