# Contributing

Contributions are welcome. Open an issue to report a problem or submit a pull
request with a proposed change.

## Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)
- [Docker](https://docs.docker.com/get-docker/) if you need to build the
  container image

## Build and test

From the repository's `src` directory, restore dependencies, build the
solution, and run the test suite:

```shell
dotnet restore
dotnet build -c Release --no-restore
dotnet test --no-restore -v normal -c Release
```

To run one test class:

```shell
dotnet test --no-restore --filter "ClassName=Valleysoft.Dredge.Tests.CompareLayersCommandTests"
```

To run tests whose fully qualified names contain a specific value:

```shell
dotnet test --no-restore --filter "FullyQualifiedName~Valleysoft.Dredge.Tests.CompareLayersCommandTests.Verify"
```
