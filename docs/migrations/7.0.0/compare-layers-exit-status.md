# Handle layer differences separately from comparison errors

**Version introduced:** 7.0.0

`dredge image compare layers` now returns exit code `2` when layers differ,
`0` when they are equal, and `1` on error. Update scripts that treat every
nonzero status as a failed comparison so they distinguish differences from
operational errors.

## Previous behavior

`dredge image compare layers` returned exit code `0` after a completed
comparison, even when the layers differed. Scripts had to inspect the rendered
or JSON output to detect differences.

## New behavior

The command reports the result through its process exit status:

| Exit code | Meaning |
| --- | --- |
| `0` | The compared layers are equal. |
| `1` | An error prevented successful completion. |
| `2` | The comparison completed and found differences. |

The contract applies to `side-by-side`, `inline`, and `json` output. Equality
uses the selected comparison options, including history when `--history` is
enabled. The exit-status change does not change the rendered or JSON output.

## Type of breaking change

This is a process exit-status compatibility change. A comparison that previously
returned success now returns a nonzero status when it finds differences.
Shell scripts using `set -e`, `&&`, or generic nonzero-as-error handling may stop
or misreport a completed comparison as an operational failure.

## Reason for change

[PR #329](https://github.com/mthalman/dredge/pull/329) lets automation distinguish
equal layers, different layers, and errors without parsing output.

## Recommended action

Decide whether differences should fail your workflow. If they should, preserve
the nonzero status. If differences are an expected result, handle `2` explicitly
while propagating errors. This POSIX shell example also works with `set -e`:

```sh
if dredge image compare layers ubuntu:22.04 ubuntu:24.04 --output json; then
    status=0
else
    status=$?
fi

case "$status" in
    0) printf '%s\n' 'Layers are equal.' ;;
    2) printf '%s\n' 'Layers differ; comparison completed.' ;;
    *) printf 'Comparison failed (exit %s).\n' "$status" >&2; exit "$status" ;;
esac
```

This example deliberately treats differences as a handled result. It preserves
exit code `1` and unexpected failures such as a missing executable. Capture the
status immediately; do not replace this handling with `|| true`, which would
also suppress authentication, network, and other real errors.

## Affected APIs

The affected interface is the process exit status of
`dredge image compare layers`, for every output format. This migration does not
change the exit-status contracts of `image compare files` or
`image compare metadata`, or any registry HTTP API.
