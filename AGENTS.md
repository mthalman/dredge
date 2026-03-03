# Dredge

Dredge is a .NET CLI tool for executing read-only commands against container registry HTTP APIs (OCI Distribution Spec). It's published as both a standalone executable and a .NET global tool (`Valleysoft.Dredge`).

## Build and Test

All commands run from the `src/` directory:

```shell
# Restore, build, test
dotnet restore
dotnet build -c Release --no-restore
dotnet test --no-restore -v normal -c Release

# Run a single test by fully qualified name
dotnet test --no-restore --filter "FullyQualifiedName~Valleysoft.Dredge.Tests.CompareLayersCommandTests.Verify"

# Run a single test class
dotnet test --no-restore --filter "ClassName=Valleysoft.Dredge.Tests.CompareLayersCommandTests"
```

The solution targets .NET 10 SDK (see `global.json`). The main project multi-targets `net9.0` and `net10.0`; the test project targets `net10.0` only.

## Architecture

### Project structure

- **Valleysoft.Dredge** — The CLI application. Entry point is `Program.cs` which registers top-level commands (`image`, `manifest`, `referrer`, `repo`, `tag`, `settings`) using `System.CommandLine`.
- **Valleysoft.Dredge.Tests** — xUnit v3 tests using Moq for mocking. Test data lives in `TestData/` subdirectories with JSON inputs and expected output text files.
- **Valleysoft.Dredge.Analyzers** — A Roslyn incremental source generator that auto-generates `SetProperty`/`GetProperty` methods on any class whose name ends with `Settings`. It reads `[JsonProperty]` attributes to build a switch statement for property access by JSON path.

### Command pattern

Every CLI command follows a consistent pattern:

1. A **command class** inherits `CommandWithOptions<TOptions>` (or `RegistryCommandBase<TOptions>` for commands that talk to a registry). The constructor wires up the command name/description and calls `SetAction` to bind execution.
2. An **options class** inherits `OptionsBase` (or `PlatformOptionsBase` for commands needing `--os`, `--arch`, `--os-version` platform filtering). Options are declared as `Argument<T>` or `Option<T>` in the constructor via `Add()`, and values are extracted in the `GetValues()` override.
3. The command implements `ExecuteAsync()` which typically calls `CommandHelper.ExecuteCommandAsync()` to get centralized error handling (auth errors produce a `docker login` hint).

To add a new command: create an options class, create a command class, and register it as a subcommand in the appropriate parent command (e.g., `ImageCommand`).

### Registry client abstraction

Registry access is abstracted through `IDockerRegistryClient` and `IDockerRegistryClientFactory`. `DockerRegistryClientFactory` handles credential resolution in priority order: `DREDGE_TOKEN` env var → `DREDGE_USERNAME`/`DREDGE_PASSWORD` env vars → Docker credential store. This abstraction enables test mocking.

### Manifest resolution

`ManifestHelper.GetResolvedManifestAsync` resolves manifest lists to a single platform-specific manifest using platform options (CLI flags) with fallback to persisted settings (`AppSettings.Platform`).

### Settings source generation

Settings classes (`AppSettings`, `FileCompareToolSettings`, `PlatformSettings`) are declared as `partial` with `[JsonProperty]` attributes. The `SettingsSourceGenerator` in the Analyzers project auto-generates `SetProperty` and `GetProperty` methods based on these attributes. When adding new settings properties, just add a `[JsonProperty]` attribute and the generator handles the rest.

## Conventions

- C# 12, nullable reference types enabled, implicit usings enabled.
- `IDE0290` (primary constructor suggestion) is suppressed — the codebase uses traditional constructors.
- JSON serialization uses Newtonsoft.Json (not System.Text.Json). Shared settings are in `JsonHelper.Settings`.
- Console output uses `Spectre.Console` for rich rendering (tables, colors, markup).
- `ImageName.Parse()` is the standard way to parse image reference strings (`image`, `image:tag`, `registry/image@digest`).
- Test assertions compare rendered output against expected text files, using `TestHelper.Normalize()` to strip `\r` and trailing whitespace.
