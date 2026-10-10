# Exit codes

Dredge uses exit codes to report whether a command succeeded, failed, or
completed a comparison or check that found a difference:

| Exit code | Meaning |
|----------:|---------|
| `0` | The command succeeded. For comparison and check commands, no differences or missing requirements were found. |
| `1` | The command failed before completing, for example because of invalid input, a registry error, or an execution error. |
| `2` | The command completed, but found a difference: image layers differ, or one or more required OCI referrer artifact types are missing. |

Exit code `2` is used by [`image compare layers`](commands/images.md#compare-layers)
and [`referrer check`](commands/referrers.md#check). It indicates a completed
comparison or check, not an execution failure.
