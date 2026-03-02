# Project Context

- **Owner:** Matt Thalman
- **Project:** Dredge — a .NET CLI for executing read-only commands against container registry HTTP APIs (OCI Distribution Spec)
- **Stack:** C#, .NET 10, System.CommandLine, xUnit v3, Moq, Spectre.Console, Newtonsoft.Json, Roslyn source generators
- **Created:** 2026-03-02T14:27:00Z

## Key Test Patterns

- Tests live in `Valleysoft.Dredge.Tests` targeting `net10.0`
- Use Moq to mock `IDockerRegistryClient` and `IDockerRegistryClientFactory`
- Compare rendered output against expected text files in `TestData/` subdirectories
- `TestHelper.Normalize()` strips `\r` and trailing whitespace for cross-platform assertions
- Build from `src/`: `dotnet build -c Release --no-restore`
- Test from `src/`: `dotnet test --no-restore -v normal -c Release`
- Single test: `dotnet test --no-restore --filter "FullyQualifiedName~Valleysoft.Dredge.Tests.SomeTest.Method"`

## Learnings

📌 Team update (2026-03-02T15:29:00Z): Upgraded to .NET 10; multi-targets net9.0/net10.0 (dropped net8.0). Test project now targets net10.0. — decided by Parker

