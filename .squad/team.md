# Team Roster

> Dredge: A .NET CLI for executing read-only commands against container registry HTTP APIs.

## Coordinator

| Name | Role | Notes |
|------|------|-------|
| Squad | Coordinator | Routes work, enforces handoffs and reviewer gates. Does not generate domain artifacts. |

## Members

| Name | Role | Charter | Status |
|------|------|---------|--------|
| Dallas | Lead | `.squad/agents/dallas/charter.md` | ✅ Active |
| Ripley | Core Dev | `.squad/agents/ripley/charter.md` | ✅ Active |
| Lambert | Tester | `.squad/agents/lambert/charter.md` | ✅ Active |
| Parker | DevOps | `.squad/agents/parker/charter.md` | ✅ Active |
| Scribe | Session Logger | `.squad/agents/scribe/charter.md` | 📋 Silent |
| Ralph | Work Monitor | — | 🔄 Monitor |

## Coding Agent

<!-- copilot-auto-assign: false -->

| Name | Role | Charter | Status |
|------|------|---------|--------|
| @copilot | Coding Agent | — | 🤖 Coding Agent |

### Capabilities

**🟢 Good fit — auto-route when enabled:**
- Bug fixes with clear reproduction steps
- Test coverage (adding missing tests, fixing flaky tests)
- Lint/format fixes and code style cleanup
- Dependency updates and version bumps
- Small isolated features with clear specs
- Boilerplate/scaffolding generation
- Documentation fixes and README updates

**🟡 Needs review — route to @copilot but flag for squad member PR review:**
- Medium features with clear specs and acceptance criteria
- Refactoring with existing test coverage
- Adding new CLI commands following established patterns
- Migration scripts with well-defined schemas

**🔴 Not suitable — route to squad member instead:**
- Architecture decisions and system design
- Multi-system integration requiring coordination
- Ambiguous requirements needing clarification
- Security-critical changes (auth, encryption, access control)
- Performance-critical paths requiring benchmarking
- Changes requiring cross-team discussion

## Issue Source

- **Repository:** mthalman/dredge
- **Connected:** 2026-03-02T14:27:00Z
- **Filters:** All open issues

## Project Context

- **Owner:** Matt Thalman
- **Stack:** C#, .NET 9, System.CommandLine, xUnit v3, Moq, Spectre.Console, Newtonsoft.Json, Roslyn source generators
- **Description:** A CLI tool for executing read-only commands against container registry HTTP APIs (OCI Distribution Spec), published as both a standalone executable and a .NET global tool.
- **Created:** 2026-03-02T14:27:00Z
