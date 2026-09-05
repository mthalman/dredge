# Repository commands

| Sub-command | Description |
|-------------|-------------|
| [`list`](#list) | List the repositories in a registry |

## List

Returns the list of repositories from the specified registry.

> **Note:** Not supported for Docker Hub.

```console
dredge repo list <registry> [--limit <count>]
```

| Option | Description |
|--------|-------------|
| `--limit` | Return at most this many repositories; must be greater than zero |

Without `--limit`, Dredge retrieves all result pages. With `--limit <count>`,
Dredge returns the first `<count>` repositories provided by the registry, then
sorts those repositories before writing JSON. Dredge stops requesting pages
once it has collected the requested number of repositories. The command does
not return a continuation value for retrieving later repositories.

Example:

```console
dredge repo list mcr.microsoft.com
[
  "acc/samples/acc-perl",
  "acc/samples/attestation-inproc",
  "acc/samples/attestation-outproc",
  "acc/samples/attested-tls-inproc",
  "acc/samples/attested-tls-outproc",
--- <cut> ---
  "windows/servercore/iis",
  "windows/servercore/iis/insider",
  "windows/servercore/insider",
  "windowsprotocoltestsuites",
  "wwllab/skills/skills-extractor-cognitive-search"
]
```
