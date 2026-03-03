# Project Context

- **Owner:** Matt Thalman
- **Project:** Dredge — a .NET CLI for executing read-only commands against container registry HTTP APIs (OCI Distribution Spec)
- **Stack:** C#, .NET 10, System.CommandLine, xUnit v3, Moq, Spectre.Console, Newtonsoft.Json, Roslyn source generators
- **Created:** 2026-03-02T14:27:00Z

## Key Architecture

- Entry point: `Program.cs` registers top-level commands (image, manifest, referrer, repo, tag, settings)
- Command pattern: `CommandWithOptions<TOptions>` / `RegistryCommandBase<TOptions>` + options class inheriting `OptionsBase`
- Registry abstraction: `IDockerRegistryClient` / `IDockerRegistryClientFactory` with credential resolution (env vars → Docker credential store)
- Manifest resolution: `ManifestHelper.GetResolvedManifestAsync` resolves manifest lists to platform-specific manifests
- Settings source gen: `SettingsSourceGenerator` auto-generates `SetProperty`/`GetProperty` from `[JsonProperty]` attributes
- Multi-targets: `net9.0` and `net10.0` (main project), `net10.0` only (tests)
- IDE0290 suppressed — use traditional constructors
- JSON: Newtonsoft.Json with `JsonHelper.Settings`
- Output: Spectre.Console for rich rendering

## Learnings

📌 Team update (2026-03-02T15:29:00Z): Upgraded to .NET 10; multi-targets net9.0/net10.0 (dropped net8.0). Dockerfile updated. Test project now targets net10.0. — decided by Parker

