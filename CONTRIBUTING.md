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

## Build versions

[MinVer](https://github.com/adamralph/minver) derives the application and NuGet
package versions from Git tags prefixed with `v`. A build at `v7.2.3` produces
version `7.2.3`; commits after a release get a prerelease version. Use a checkout
with full history and tags rather than setting `Version` manually.

To inspect the version after restoring dependencies, run from the repository
root:

```shell
dotnet msbuild src/Valleysoft.Dredge -nologo -target:MinVer -getProperty:Version
```

For a local container build, pass that output as
`--build-arg MinVerVersionOverride=<calculated-version>` with `src` as the build
context. The Dockerfile requires this value because the context excludes Git
metadata. The shared release automation validates the release tag and source;
builds rely on MinVer for versioning.

## Label pull requests

Release notes and versions come from pull request titles and labels through
[release-automation v1.0.1][release-author-guide]. This repository uses the
toolkit defaults without a configuration override.

Apply exactly one version label: `semver:major` for breaking changes,
`semver:minor` for backward-compatible features, or `semver:patch` for fixes,
documentation, dependencies, and other maintenance. Add at most one category
label: `enhancement`, `bug`, `documentation`, or `dependencies`. Dependency
changes that break compatibility still need `semver:major`.

Use `skip-changelog` only to intentionally exclude a non-breaking change from
release notes. Never combine it with `semver:major`. The existing
`breaking-change` label is not a release-automation label; it does not replace
`semver:major` or the required migration fragment.

These label-count rules are contributor conventions, not general policy
checks. Missing version labels default to a patch bump; conflicting version
labels select the largest bump. Review labels before merging.

## Document breaking changes

A PR labeled `semver:major` must add a new file directly under `.changes`,
named `+short-kebab-slug.breaking.md`. Start with one H3 title, then include
these H4 sections in this order:

- Previous behavior
- New behavior
- Type of breaking change
- Reason for change
- Recommended action
- Affected APIs

Use the pinned [fragment template][fragment-template] for a complete example.
Every section needs actionable content, not placeholders such as `TODO` or
`N/A`. Describe affected CLI commands or behavior if there is no API change.
Use Markdown rather than raw HTML outside code examples. Retain fragments
after release; editing an existing fragment does not satisfy the requirement
to add one for a new breaking change.

Automation generates versioned guides under `docs/migrations` and maintains
`.github/migration-guides.json`. Do not create placeholder state or edit the
generated automation branch directly. A human must mark generated draft PRs
ready for review to trigger CI, including after automation updates.

For publication and recovery, see [Releasing Dredge](docs/releasing.md).

[release-author-guide]: https://github.com/mthalman/release-automation/blob/90551757fe8b061d4dff1a4cab12f10e58f07201/docs/author-guide.md
[fragment-template]: https://github.com/mthalman/release-automation/blob/90551757fe8b061d4dff1a4cab12f10e58f07201/docs/fragment-template.md
