# Project Context

- **Owner:** Matt Thalman
- **Project:** Dredge — a .NET CLI for executing read-only commands against container registry HTTP APIs (OCI Distribution Spec)
- **Stack:** C#, .NET 10, System.CommandLine, xUnit v3, Moq, Spectre.Console, Newtonsoft.Json, Roslyn source generators
- **Created:** 2026-03-02T14:27:00Z

## Key Build/Deploy Info

- Build from `src/`: `dotnet restore && dotnet build -c Release --no-restore`
- Test from `src/`: `dotnet test --no-restore -v normal -c Release`
- Main project multi-targets `net9.0` and `net10.0`; tests target `net10.0` only
- SDK version pinned in `global.json` (.NET 10)
- Published as NuGet global tool: `Valleysoft.Dredge`
- Docker image: `ghcr.io/mthalman/dredge`
- Version tracked in `version.txt`

## Learnings

<!-- Append new learnings below. Each entry is something lasting about the project. -->
- .NET 10 upgrade (2026): Changed global.json rollForward to `latestFeature` and allowPrerelease to `true` because only preview SDK was available. Dockerfile digest pins were removed since .NET 10 images don't have stable digests yet — re-pin once GA images land.
