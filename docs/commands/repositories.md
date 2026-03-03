# Repository Commands

| Sub-command | Description |
|-------------|-------------|
| [`list`](#list) | List the repositories in a registry |

## List

Returns the list of repositories from the specified registry.

> **Note:** Not supported for Docker Hub.

```console
dredge repo list <registry>
```

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
