# Tag Commands

| Sub-command | Description |
|-------------|-------------|
| [`list`](#list) | List the tags in a repository |

## List

Returns the tags associated with the specified repository.

```console
dredge tag list <repo>
```

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
