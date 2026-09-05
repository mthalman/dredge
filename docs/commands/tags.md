# Tag commands

| Sub-command | Description |
|-------------|-------------|
| [`list`](#list) | List the tags in a repository |

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
