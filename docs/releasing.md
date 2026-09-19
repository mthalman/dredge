# Releasing Dredge

Dredge uses [release-automation v1.0.1][installation] to prepare release notes
and migration guides. A human pushes the prepared tag to start publication.
The release workflow runs tests, builds eight framework-dependent .NET 10
executables, uploads them with SHA-256 checksums to the draft, publishes
`Valleysoft.Dredge` to NuGet, pushes the container image, and finally publishes
the existing GitHub Release.

The shared prepare/finalize actions validate the release tag and source against
the prepared draft. MinVer derives build versions from `v`-prefixed Git tags,
without an additional expected-version property or build-version override.
All build checkouts fetch full history and tags.

Container builds calculate the version through the same MinVer MSBuild target
before building the image. They
pass `MinVerVersionOverride` into Docker because the `src` build context has no
Git metadata. PR containers use MinVer's calculated development version and are
not pushed. There is no manually maintained version file. Manual release
dispatch and standalone container publishing are no longer supported.

## Configure the repository

Before the first release:

- Allow the pinned workflows and actions in **Settings > Actions > General**.
  Enable **Allow GitHub Actions to create and approve pull requests**. The
  toolkit creates draft PRs; it does not approve or merge them.
- Ensure these labels exist: `semver:major`, `semver:minor`, `semver:patch`,
  `skip-changelog`, `enhancement`, `bug`, `documentation`, and `dependencies`.
  This installation uses default configuration and paths.
- For the first draft, review changes merged since the last published release.
  Historical labels such as `breaking-change` do not select a major bump.
  Correct release labels and add any missing breaking fragments through review
  before accepting the draft's version and migration guidance.
- Configure `NUGET_ORG_API_KEY` with permission to publish `Valleysoft.Dredge`.
  Ensure the workflow's `GITHUB_TOKEN` can publish `ghcr.io/mthalman/dredge`.
- Use repository rules and review controls to restrict release tags and
  publication. Do not delete, move, or recreate release tags to repair failures.

The publication jobs use `GITHUB_TOKEN` by default. Prepare needs visibility
of unpublished drafts. GitHub may also require workflow-modification
authorization when publishing an older prepared commit after workflow files
change on `main`; `GITHUB_TOKEN` cannot receive that permission.

If your deployment needs it, configure the optional `RELEASE_GITHUB_TOKEN`
secret through your normal credential review process. It is used only for
prepare, draft asset upload, and finalize. The token needs repository Contents
(write), draft visibility, and Workflows (write) when GitHub requires it.
See the pinned [credential requirements][credentials] for token types and
limitations. Do not broaden credentials merely to bypass a failed gate.

## Prepare and publish a release

1. Review PR titles, [release labels and breaking fragments][contributing]
   before merging to `main`. The **Release draft** workflow runs on each
   push to `main`; you can also dispatch it manually. It always selects the
   latest actual default-branch commit, not the dispatch branch.
2. If drafting opens a PR on `automation/migration-guides`, review the generated
   topics, indexes, and state. A first run without breaking changes can still
   need a PR for the migration index. The run fails at **Wait for merged
   migration guides** and leaves any existing release draft unchanged.
3. Have a human mark the documentation PR ready for review. Confirm **CI** and
   **Docker** run, review the results, then merge. Each automation update
   returns the PR to draft and requires another human readiness action.
4. Wait for a successful drafting run after the exact generated files are
   merged. If the merge does not trigger one, dispatch **Release draft**.
   Installing or upgrading the workflows requires a fresh successful run;
   an old draft without preparation metadata is not publishable.
5. Inspect the prepared draft:

   ```shell
   gh api repos/mthalman/dredge/releases --paginate --jq '.[] | select(.draft) | {id, tag_name, target_commitish, html_url}'
   ```

   Expect exactly one draft with an exact `vMAJOR.MINOR.PATCH` tag and a full
   source commit SHA. Review its version, source, notes, migration links,
   passing CI, and required approvals. Keep its version-only title,
   preparation metadata, and migration-links block intact.
6. Create and push that **new** tag at the draft's exact `target_commitish`,
   not at an assumed current `main` tip. Substitute the reviewed values:

   ```shell
   git fetch origin main --tags
   git tag <prepared-tag> <prepared-commit-sha>
   git push origin refs/tags/<prepared-tag>
   ```

   Use a human push. A tag created with a workflow's `GITHUB_TOKEN` normally
   does not trigger another push workflow. Both lightweight and annotated
   tags are supported.
7. Observe **Release**. It must validate preparation, pass tests, build and
   upload executables, publish NuGet and containers, and finalize successfully.
   Confirm the GitHub Release is published with the reviewed notes and all
   eight executables and checksums. Confirm the NuGet version and container
   tags (`<version>`, `<major>`, and `latest`) match the prepared version.

The entire publication workflow shares the `release-drafter` concurrency
group with the reusable draft pipeline, with `cancel-in-progress: false` and
`queue: max`. Do not add that group to the draft caller: its callee owns it.
The queue prevents participating runs from replacing pending releases; it
does not stop humans or other clients from modifying GitHub state.

## Recover from a failed run

Do not edit a draft while publication is running. Finalize independently
revalidates the tag, draft, source, and unchanged prepare context. A failed
consumer job blocks finalization, but already-uploaded assets, NuGet packages,
and container images are not rolled back.

After diagnosing a transient failure, prefer **Re-run failed jobs** on the
original tag-creation run so successful publication jobs are not repeated.
Inspect the external destinations first. Executable uploads use `--clobber`
to replace matching asset names on a retry, and container pushes can replace
their tags. NuGet pushes deliberately fail on an existing version rather than
silently treating an unverified package as success. If a package was accepted
but its job failed, verify the published package and resolve that partial
publication before retrying; do not move the tag or blindly rerun every job.

Once the prepared GitHub Release is published, a rerun validates provenance
and skips all consumer build and publication jobs. That no-op behavior does
not make partially completed releases idempotent. A failed finalize request
can also have published the release before reporting an error; inspect its
actual state. See [publication recovery][recovery] for the shared contract.

## Verify deployment and upgrade pins

After merging the onboarding workflows, verify policy checks with a test PR:
`semver:major` without a new valid fragment must fail, as must combining it
with `skip-changelog`. Observe the actual nested policy check name before
adding it to a ruleset; do not guess its name.

Exercise the human-ready documentation PR path and a successful post-merge
drafting run. Test publication, failure propagation, permissions, shared
queue behavior, and already-published reruns in a test repository before
relying on production publication. Local linting does not establish live
activation or end-to-end correctness.

Renovate groups `mthalman/release-automation` updates. Review all four
entrypoints together: both reusable workflows and both publication Actions
must use the same full release commit SHA and matching toolkit tag comment.
Update the SHA-pinned links in `AGENTS.md`, `CONTRIBUTING.md`, and this guide
manually in the same PR; Renovate's GitHub Actions manager does not update
Markdown links. Follow the pinned [upgrade guide][upgrading], then run
drafting successfully again before pushing the next prepared tag.

[installation]: https://github.com/mthalman/release-automation/blob/90551757fe8b061d4dff1a4cab12f10e58f07201/docs/installation.md
[credentials]: https://github.com/mthalman/release-automation/blob/90551757fe8b061d4dff1a4cab12f10e58f07201/docs/tag-publishing.md#choose-credentials-and-verify-draft-visibility
[contributing]: ../CONTRIBUTING.md#label-pull-requests
[recovery]: https://github.com/mthalman/release-automation/blob/90551757fe8b061d4dff1a4cab12f10e58f07201/docs/tag-publishing.md#rerun-and-recover-safely
[upgrading]: https://github.com/mthalman/release-automation/blob/90551757fe8b061d4dff1a4cab12f10e58f07201/docs/upgrading.md
