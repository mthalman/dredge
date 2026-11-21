# Headless image comparison measurements

The fixed-fixture harness measures `image compare semantic` with all five scopes, text
output limited to one detail, and complete JSON output. The
[measurement record](measurements/image-compare-v1.json) contains every sample,
fixture layer digest, compressed input size, output length, and derived budget.
These are archive-backed measurements, not network or live Docker benchmarks.

## Reproduce

Use the .NET SDK selected by `global.json`. Restore and build from `src` before
measuring, and avoid running other builds or tests during the measurement:

```powershell
dotnet restore
dotnet build -c Release --no-restore
$env:DREDGE_MEASURE_COMPARE = '1'
dotnet test --project Valleysoft.Dredge.Tests\Valleysoft.Dredge.Tests.csproj --no-build --no-restore -c Release --filter-method Valleysoft.Dredge.Tests.ComparePerformanceTests.MeasureFixedFixtures
Remove-Item Env:\DREDGE_MEASURE_COMPARE
```

This replaces the measurement record. To compare against the existing record
without replacing it, also set `DREDGE_COMPARE_CHECK_BUDGETS=1` before running
the harness. Remove that environment variable afterward. Wall-clock budgets are
machine-dependent reference ceilings, not portable CI thresholds.

## Fixtures and acquisition

| Fixture | Baseline | Target |
|---|---|---|
| Small | 128 files, 256 bytes each, one layer | One additional layer overwrites 16 files |
| Dependency-heavy | 4,000 files, 256 bytes each, plus 1,000 npm manifests in distinct installation roots, one layer | One additional layer overwrites 500 content files |
| High-file-count | 20,000 files, 1,024 bytes each, plus 1,000 bound hardlinks, six layers | One additional layer overwrites 2,500 files |

Names, contents, timestamps, tar ordering, and gzip inputs are fixed. Payloads
cycle through the alphabet; an overwrite with the existing `z` bytes is not a
semantic file difference. The observed difference counts are recorded separately
from overwrite counts. The small baseline layer is pinned to
`sha256:5c06921bcbcd87572a6b4223381a3a9acb3938cf526f3e55d84b2b1a8058ddc2`.
The record pins all remaining layer digests.

The registry case serves deterministic compressed layers through the registry
client abstraction. The mixed case compares that baseline with a synthetic
Docker export read by the actual archive source. Export construction is included
in mixed acquisition time. Fixture generation before acquisition is excluded.
Mixed reports intentionally return partial coverage because original daemon
compressed sizes and registry download estimates are unavailable.

Each fixture/source/format combination has three repeats. Each repeat uses a new
isolated cache for a cold run, immediately followed by a warm run using that same
cache. Both runs acquire fresh sources; daemon exports are never retained between
commands. There are 72 samples. The test worker and JIT are reused, with forced
collection before each run; cold does not mean a cold OS page cache or new process.

## Recorded results

The completed run used Windows `10.0.26300.0`, .NET SDK `10.0.403`, runtime
`10.0.14`, and 12 logical processors. These are fresh measurements of the final
shared package-comparison implementation; all 72 comparisons completed.
An earlier budget-check run exceeded its prior cold-start time ceiling
(1,395 ms versus 965 ms), illustrating sensitivity to JIT startup and shared-host
load. The current ceilings are derived from the fresh samples below, not a
manually increased tolerance. An earlier measurement attempt also encountered
a transient Windows access error while publishing cache metadata.

Elapsed columns show the minimum and maximum of three runs in milliseconds.
RSS and allocation columns show the maximum across both cold and warm runs.

| Fixture | Source | Format | Cold ms | Warm ms | Peak RSS MiB | Allocated MiB |
|---|---|---|---:|---:|---:|---:|
| Small | Registry | Text | 106–530 | 52–68 | 92.7 | 5.3 |
| Small | Registry | JSON | 143–302 | 64–88 | 94.9 | 5.0 |
| Small | Mixed | Text | 151–293 | 95–112 | 96.4 | 6.2 |
| Small | Mixed | JSON | 156–171 | 96–115 | 96.0 | 6.3 |
| Dependency-heavy | Registry | Text | 582–1,388 | 785–943 | 278.6 | 263.8 |
| Dependency-heavy | Registry | JSON | 500–602 | 544–588 | 278.7 | 274.1 |
| Dependency-heavy | Mixed | Text | 606–667 | 648–893 | 357.5 | 334.9 |
| Dependency-heavy | Mixed | JSON | 659–716 | 731–942 | 318.3 | 344.8 |
| High-file-count | Registry | Text | 1,714–2,141 | 1,948–2,216 | 501.2 | 943.3 |
| High-file-count | Registry | JSON | 1,995–2,326 | 1,755–2,088 | 437.2 | 964.8 |
| High-file-count | Mixed | Text | 1,311–2,742 | 1,302–2,141 | 565.0 | 1,235.0 |
| High-file-count | Mixed | JSON | 1,572–3,885 | 1,474–2,705 | 557.4 | 1,254.5 |

Disk columns show maxima across formats, cache states, and repeats. Independently
sampled component peaks need not occur simultaneously.

| Fixture | Source | Peak cache MiB | Peak daemon scratch MiB | Peak total disk MiB | Retained cache MiB |
|---|---|---:|---:|---:|---:|
| Small | Registry | 0.19 | 0 | 0.19 | 0.19 |
| Small | Mixed | 0.31 | 0.15 | 0.46 | 0.24 |
| Dependency-heavy | Registry | 11.60 | 0 | 11.60 | 8.51 |
| Dependency-heavy | Mixed | 13.75 | 5.38 | 19.12 | 10.66 |
| High-file-count | Registry | 41.68 | 0 | 41.68 | 30.96 |
| High-file-count | Mixed | 50.34 | 33.46 | 83.80 | 39.62 |

Observed file differences were 16, 481, and 2,404 respectively. Registry JSON
output contained 30,033, 1,682,443, and 3,487,932 characters; text contained only
1,722, 1,767, and 1,763 characters at `--limit 1`. The limit bounds presentation,
not filesystem, package, or cache analysis. Warm runs can still rewrite derived
cache metadata, so warm does not guarantee less disk activity or allocation.

## Metrics and limitations

- Elapsed time includes acquisition, analysis, and formatting into a
  `StringWriter`, but not terminal, pipe, or filesystem output throughput.
- Peak working set is process RSS sampled every 5 ms. It includes fixture data,
  test-worker state, and instrumentation, not only comparison-owned memory.
- Managed allocation is the process-wide cumulative allocation delta during the
  run, including the sampler. It is not retained memory.
- Cache, daemon scratch, and total disk peaks are sampled sums of logical file
  lengths under the fixture root. They are not allocated filesystem blocks, and
  short-lived peaks between samples can be missed.
- Retained cache is measured after comparison sources are disposed. Daemon
  scratch is asserted to return to zero.
- Compressed payloads are highly compressible synthetic data. These fixtures
  exercise file count, scoped inventory, layering, and hardlink behavior, not
  production registry bandwidth or realistic compression ratios.

## Regression checks

For each fixture/source/format/cache combination, the harness derives ceilings
for elapsed time, RSS, managed allocation, peak disk, and retained cache as
`maximum + (maximum - minimum)` across the three samples. A metric with no
observed variation has no added margin. The optional check mode compares every
new sample with those recorded ceilings.

Normal tests do not enforce wall-clock timing. They pin the small fixture digest,
semantic change count, effective and hidden byte counts, and use the recorded
small warm JSON allocation ceiling as a conservative serializer regression
guard. Correctness tests separately require complete JSON regardless of the text
detail limit.

## Native AOT validation

Release compilation and source-generated serialization are validated separately
from these measurements. A Linux x64 Native AOT executable was published using
the cached `mcr.microsoft.com/dotnet/sdk:10.0-noble-aot` SDK container and exercised
against the fixed small images through Docker Engine. Native JSON reports passed
the version-1 schema and exact result assertions: equal inputs returned `0` with
no filesystem changes, changed inputs returned `2` with 16 filesystem changes
when selecting files/config, and all-scopes daemon comparison returned `1` with
16 changes and explicit incomplete size coverage.

The cached container uses SDK `10.0.401`. Final native validation explicitly
selected runtime/compiler `10.0.14` with `-p:RuntimeFrameworkVersion=10.0.14`
instead of the cached SDK's older defaults; publication completed without
NuGet audit, trimming, or AOT code warnings. Repeated native comparisons emitted
byte-identical JSON. This temporary artifact is not a release build.
Windows native publication remains unavailable on this host because Visual
Studio's **Desktop development with C++** platform linker is missing. Managed
publish output is not evidence of a successful native build on either platform.
