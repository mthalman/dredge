# Project Context

- **Owner:** Matt Thalman
- **Project:** Dredge — a .NET CLI for executing read-only commands against container registry HTTP APIs (OCI Distribution Spec)
- **Stack:** C#, .NET 9, System.CommandLine, xUnit v3, Moq, Spectre.Console, Newtonsoft.Json, Roslyn source generators
- **Created:** 2026-03-02T14:27:00Z

## Key Architecture

- Entry point: `Program.cs` registers top-level commands (image, manifest, referrer, repo, tag, settings)
- Command pattern: `CommandWithOptions<TOptions>` / `RegistryCommandBase<TOptions>` + options class inheriting `OptionsBase`
- Registry abstraction: `IDockerRegistryClient` / `IDockerRegistryClientFactory` with credential resolution (env vars → Docker credential store)
- Manifest resolution: `ManifestHelper.GetResolvedManifestAsync` resolves manifest lists to platform-specific manifests
- Settings source gen: `SettingsSourceGenerator` auto-generates `SetProperty`/`GetProperty` from `[JsonProperty]` attributes
- Multi-targets: `net8.0` and `net9.0` (main project), `net9.0` only (tests)
- IDE0290 (primary constructors) is suppressed — use traditional constructors

## Learnings

<!-- Append new learnings below. Each entry is something lasting about the project. -->
