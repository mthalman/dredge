# Exit codes

Dredge uses exit codes to report whether a command succeeded, failed, or
completed a comparison or check that found a difference:

| Exit code | Meaning |
|----------:|---------|
| `0` | The command succeeded. For `image compare semantic`, all requested evidence is complete and equal; for `image compare layers`, layers are equal; for `referrer check`, every required artifact type exists. Other commands may use `0` even when a comparison finds differences. |
| `1` | The command failed, or `image compare semantic` emitted a usable but incomplete report. Incomplete coverage takes precedence over known differences. |
| `2` | The command completed, but found a difference: requested unified image comparison evidence differs, image layers differ, or required OCI referrer artifact types are missing. |

Exit code `2` is used by [`image compare semantic`](commands/images.md#compare-semantic),
[`image compare layers`](commands/images.md#compare-layers)
and [`referrer check`](commands/referrers.md#check). It indicates a completed
comparison or check, not an execution failure.
