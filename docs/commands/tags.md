# Tag commands

| Sub-command | Description |
|-------------|-------------|
| [`list`](#list) | List the tags in a repository |
| [`delete`](#delete) | Delete a tag without deleting its manifest |

## List

Returns the tags associated with the specified repository.

```console
dredge tag list <repository> [--limit <count>]
```

| Option | Description |
|--------|-------------|
| `--limit` | Return at most this many tags; must be greater than zero |

Without `--limit`, Dredge retrieves all result pages. With `--limit <count>`,
Dredge returns the first `<count>` tags provided by the registry, then sorts
those tags before writing JSON. Dredge stops requesting pages once it has
collected the requested number of tags. The command does not return a
continuation value for retrieving later tags.

Example:

```console
dredge tag list ubuntu
[
  "10.04",
  "12.04",
  "12.04.5",
  "12.10",
  "13.04",
--- <cut> ---
  "zesty-20170703",
  "zesty-20170913",
  "zesty-20170915",
  "zesty-20171114",
  "zesty-20171122"
]
```

## Delete

Deletes one tag association. The referenced manifest (including a manifest list
or OCI image index), its child manifests, layers, and other tags remain.
To delete the manifest and all tags pointing to it instead, use
[`manifest delete`](manifests.md#delete).

The registry must support tag deletion and your credentials must have delete
permission. Tag deletion is optional in the OCI Distribution specification.
Dredge reports unsupported or disabled deletion as an error and **never falls
back to deleting the manifest**. See [authentication](../authentication.md#deletion-permissions).

```console
dredge tag delete <image>:<tag> [--yes]
```

| Option | Description |
|--------|-------------|
| `--yes`, `-y` | Skip confirmation; required when standard input is redirected |

An explicit tag is required, including `:latest` when that is the intended tag.
A bare repository name or digest is rejected.

Without `--yes`, Dredge displays the tag and asks for confirmation on stderr.
Press `y` to confirm, or `n` or Enter to cancel; the default is No.
Canceled operations return a nonzero exit code. For scripts and redirected
stdin, pass `--yes`; piped confirmation is not accepted.
`--yes` does not ignore missing tags, unsupported deletion, or permission errors.

Example (deletes immediately without prompting):

```console
dredge tag delete registry.example.com/team/app:old --yes
Deleted tag 'registry.example.com/team/app:old'.
```

Deleting a tag does not itself reclaim storage. Registry retention policies
and garbage collection determine when unreferenced content is removed.
