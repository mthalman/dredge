### Explicitly opt in to replacing saved layer content

`dredge image save-layers` now rejects nonempty output directories unless you
pass `--force`. Prefer a new or empty directory. Use `--force` only when you
intend to allow existing content to be overwritten or deleted.

#### Previous behavior

`dredge image save-layers` could write into a nonempty output directory without
an explicit overwrite option. Applying squashed layers could overwrite files
and remove existing content through OCI whiteouts (layer entries that specify
deletions).

#### New behavior

The output directory must be new or empty unless `--force` is supplied. A
nonempty directory without `--force` causes exit code `1` before registry access,
leaving its existing content unchanged.

`--force` permits destructive writes; it is not just a way to silence the
directory check. Squashed output can overwrite, replace, or delete entries,
including entries targeted by OCI whiteouts. With `--no-squash --force`, Dredge
replaces each matching per-layer output directory, removing its old contents
before copying that layer. It does not clean the entire output directory.

An output path that is an existing file or is itself a symbolic link is rejected
even with `--force`. A real output directory beneath a linked ancestor is still
allowed.

#### Type of breaking change

This is a command-line behavior change for filesystem writes. Scripts that
reuse a populated destination now fail unless they choose a new or empty
directory or explicitly opt in to destructive replacement.

#### Reason for change

[PR #328](https://github.com/mthalman/dredge/pull/328) prevents saving image
layers from silently overwriting or deleting user content. Destructive
replacement requires an explicit choice.

#### Recommended action

Prefer a new or empty destination for each extraction. For example, ensure
`ubuntu-layers-new` does not exist or is empty before running:

```sh
dredge image save-layers ubuntu:24.04 ubuntu-layers-new
```

If you intentionally reuse a nonempty directory, first back up anything you need
and verify the destination. Only then opt in to overwrites and deletions:

```sh
dredge image save-layers ubuntu:24.04 ubuntu-layers --force
```

Do not add `--force` indiscriminately to shared-directory workflows. For
`--no-squash`, treat matching `layer<index>-<digest>` directories as replaceable
units, not directories whose unrelated files will be preserved. If the output
path is a symbolic link, explicitly choose its intended real directory and
apply the same empty-directory or backup precautions.

#### Affected APIs

The affected command is `dredge image save-layers <image> <output-path>`,
including its squashed and `--no-squash` modes and the new `--force` option.
The compatibility change concerns local destination handling, not registry
HTTP APIs.
