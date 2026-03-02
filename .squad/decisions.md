# Decisions

> Team decisions that all agents must respect. Append-only — never edit past entries.

<!-- Scribe merges decisions from .squad/decisions/inbox/ into this file. -->

### Decision: Upgrade from .NET 9 to .NET 10

**By:** Parker (DevOps)
**Date:** 2026-03-02

#### What changed

- **global.json**: SDK version `10.0.100`, rollForward changed to `latestFeature`, allowPrerelease set to `true` (only preview SDK available currently).
- **Main project**: Multi-targets `net9.0` and `net10.0` (dropped `net8.0`).
- **Test project**: Targets `net10.0` (was `net9.0`).
- **Analyzers project**: No change — stays on `netstandard2.0`.
- **Dockerfile**: SDK and runtime images updated to `10.0-noble`/`10.0-noble-chiseled`. Digest pins removed (no stable .NET 10 digests yet). Publish target changed to `net10.0`.
- **release.yml**: Publish target changed to `-f net10.0`.
- **README.md**: Updated prerequisite to .NET 10 runtime.
- **AGENTS.md**: Updated .NET version references.

#### Why

Project owner requested the upgrade. .NET 8 is no longer a target; .NET 9 remains as the lower bound for the multi-target build.

#### Action items for the team

- **All agents**: Update your history files to reflect `.NET 10` as the stack version and `net9.0`/`net10.0` as the target frameworks.
- **Parker (follow-up)**: Re-pin Dockerfile image digests once .NET 10 GA images are published. Revert `allowPrerelease` to `false` and `rollForward` to `latestPatch` once a stable .NET 10 SDK is available.
