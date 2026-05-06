**Memory Leak Analyzer — Presets**

- **Fast:**
  - `TopFinalizerTypesToShow`: 5
  - `TopHighlyReferencedObjectsToShow`: 8
  - `HighReferenceThreshold`: 75
  - `MaxDuplicateStringLength`: 300
  - `MinDuplicateStringCount`: 20
  - `MaxReferenceAddresses`: 250_000
  - `MaxLeakScanObjects`: 500_000

- **Balanced (default):**
  - `TopFinalizerTypesToShow`: 10
  - `TopHighlyReferencedObjectsToShow`: 15
  - `HighReferenceThreshold`: 50
  - `MaxDuplicateStringLength`: 500
  - `MinDuplicateStringCount`: 10
  - `MaxReferenceAddresses`: 1_000_000
  - `MaxLeakScanObjects`: 2_000_000

- **Full:**
  - `TopFinalizerTypesToShow`: 25
  - `TopHighlyReferencedObjectsToShow`: 40
  - `HighReferenceThreshold`: 30
  - `MaxDuplicateStringLength`: 2_000
  - `MinDuplicateStringCount`: 5
  - `MaxReferenceAddresses`: 2_000_000
  - `MaxLeakScanObjects`: 5_000_000

Notes: exact values from `MemoryLeakOptions.Preset(AnalysisProfile)`; `MaxLeakScanObjects=0` disables the cap.

# MemoryLeakAnalyzer — Preset Design

Purpose: surface suspicious retention patterns, top memory-consuming types, and candidate leak clusters while keeping analysis bounded on large heaps.

Current working (summary):

Section builder: `MemoryLeakSectionBuilder` emits TOP TYPES, TOP INSTANCES and RETAINER PATHS subsections; retainer paths include BFS depth and visited node counts.

Goals for preset-driven flow:

Suggested important options:

How analyzer flow should respect presets:

Concrete preset mappings (recommended):

Minimal code changes:

Tests and validation:

Next steps:

# MemoryLeakAnalyzer — Preset Design

Purpose: detect finalizer backlogs, duplicate strings and highly referenced objects causing retention.

Where to look in the repo:
- Analyzer: `DumpDetective/Analyzers/MemoryLeakAnalyzer.cs`
- Section builder: `src/DumpDetective.Reporting/SectionBuilders/MemoryLeakSectionBuilder.cs` (reporting consumes `MemoryLeakDomainResult`).

Observed implementation details (from source):
- Single-pass heap scan (`AnalyzeObjectsPass`) — simultaneously collects string-fingerprints and tallies incoming-reference counts.
- Finalizer queue scanned via `heap.EnumerateFinalizableObjects()` in `AnalyzeFinalizerQueue()`.
- Analyzer constructor reads thresholds from `AnalysisConfiguration`: `_highReferenceThreshold`, `_maxStringLength`, `_minDuplicateCount`, `_maxReferenceAddressesToTrack`.
- Produces `MemoryLeakDomainResult` including `DuplicateStringCount`, `DuplicateStringWastedBytes`, `HighlyReferencedObjectCount`, and `SkippedReferenceAddresses` when the `_maxReferenceAddressesToTrack` cap is hit.

Preset levers (match current config names):
- `HighReferenceThreshold` (int)
- `MaxReferenceAddressesToTrack` (int)
- `MinDuplicateStringCount` (int)
- `MaxDuplicateStringLength` (int)
- `TopDuplicateStringsToShow`, `TopHighlyReferencedObjectsToShow` (present as internal constants today; suggest promoting to options)

Concrete preset mappings (recommended):
Fast:
- `HighReferenceThreshold = 100`, `MaxReferenceAddressesToTrack = 25_000`, `MinDuplicateStringCount = 5`, `MaxDuplicateStringLength = 256`, small Top* values

Balanced (current/safer defaults):
- `HighReferenceThreshold = config.HighReferenceThreshold` (defaults in `AnalysisConfiguration`), `MaxReferenceAddressesToTrack = 250_000`, `MinDuplicateStringCount = 2`, `MaxDuplicateStringLength = 4096`

Full:
- Raise budgets: `MaxReferenceAddressesToTrack = 2_000_000`, `MinDuplicateStringCount = 1`, `MaxDuplicateStringLength = 16_384`, increase Top* results and enable raw artifact exports.

Concrete code changes recommended:
- Promote `TopFinalizerTypesToShow`, `TopDuplicateStringsToShow`, `TopHighlyReferencedObjectsToShow` (currently internal consts in `MemoryLeakAnalyzer.cs`) into `MemoryLeakAnalysisOptions` with `Preset(...)` behavior.
- Ensure the constructor still accepts `AnalysisConfiguration` and document expected memory tradeoffs for `MaxReferenceAddressesToTrack`.
- `MemoryLeakSectionBuilder` should render `SkippedReferenceAddresses` and annotate when top lists were truncated by `Top*` caps.

Tests and validation:
- Unit: create small synthetic ClrHeap mocks to validate duplicate string detection, fingerprint correctness, and reference-count thresholding.
- Integration/perf: run the analyzer on a medium dump and report `SkippedReferenceAddresses` under `Fast`/`Balanced`/`Full` to ensure presets shape memory usage.

Next steps I can take:
- Implement `MemoryLeakAnalysisOptions.Preset(...)` and promote the internal `Top*` constants to configurable fields, plus update tests and reporting.

Built-in presets (from `MemoryLeakOptions.Preset`):
- **Fast:**
  - `TopFinalizerTypesToShow = 5`
  - `TopHighlyReferencedObjectsToShow = 8`
  - `HighReferenceThreshold = 75`
  - `MaxDuplicateStringLength = 300`
  - `MinDuplicateStringCount = 20`
  - `MaxReferenceAddresses = 250_000`
  - `MaxLeakScanObjects = 500_000`
- **Balanced (default):**
  - `TopFinalizerTypesToShow = 10`
  - `TopHighlyReferencedObjectsToShow = 15`
  - `HighReferenceThreshold = 50`
  - `MaxDuplicateStringLength = 500`
  - `MinDuplicateStringCount = 10`
  - `MaxReferenceAddresses = 1_000_000`
  - `MaxLeakScanObjects = 2_000_000`
- **Full:**
  - `TopFinalizerTypesToShow = 25`
  - `TopHighlyReferencedObjectsToShow = 40`
  - `HighReferenceThreshold = 30`
  - `MaxDuplicateStringLength = 2_000`
  - `MinDuplicateStringCount = 5`
  - `MaxReferenceAddresses = 2_000_000`
  - `MaxLeakScanObjects = 5_000_000`

Rationale:
- **Fast:** tight budgets and higher `HighReferenceThreshold` reduce I/O and memory on very large dumps; good for initial triage.
- **Balanced:** default trade-off between coverage and resource use; sufficient for most medium-sized investigations.
- **Full:** expanded budgets lower the chance of capping and increase fidelity of duplicate-string and high-reference findings; use only when host has RAM/time to spare.

