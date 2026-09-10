# Remove per-analyzer configurability — status: done (2026-09-11)

## Goal

The tool must produce one canonical, deterministic verdict per dump. No analyzer's severity
thresholds, scan-scoping caps, or export toggles are externally tunable via `config.json` or CLI
flags anymore — the same dump always produces the same findings, regardless of who runs it or what
settings they pass.

This completes what `analysis-profile-removal-plan.md` (deleted, `git show
ad37513b~1:docs/refactor/analysis-profile-removal-plan.md`) decided in **D4** but never fully
enforced: *"every Category 5 threshold keeps its Balanced value as its single constant... the same
dump produces contradictory findings depending on a knob users read as 'how thorough.'"* D4 removed
the 3-tier `Fast`/`Balanced`/`Full` split, but `ConfigurationResolver.ApplySectionOverrides` was a
fully generic JSON-merge with no allowlist — it still let `config.json` override *any* property of
*any* analyzer's options object. The "constant" D4 decided on was never actually locked down in
code, until now.

## Decisions

1. **`ExecutionPolicy`** — folded into fixed, well-commented `private const` fields directly in
   `DominatorAnalyzer` (its only real consumer), not left as a config-bound knob.
2. **Export toggles** — deleted outright: the `ProduceClusterExports`/`ProduceRawExports`
   properties *and* the gzip/NDJSON/JSON export code blocks they gated.
3. **`IncludeAnalyzers`/`ExcludeAnalyzers`** — stayed. A feature (which analyzers run), not an
   analysis-affecting option — out of scope for this plan entirely.
4. **A `const`-inlining sweep** (deleting `Options/Xxx.cs` files entirely, matching
   `HeapTopologyAnalyzerOptions`'s already-established `static class` + `const` shape) — deferred,
   not part of this pass. Every `XxxAnalysisOptions` class stays a plain C# type; only the external
   override path is gone. Rewriting ~25 analyzer signatures to inline `const` fields instead would
   be a much larger, higher-risk change for no behavioral benefit.

## What's gone

- **`ConfigurationResolver.cs`**: all 19 `BuildXxxFromConfig` methods (one per analyzer), plus the
  generic override machinery they shared (`ApplySectionOverrides<T>`, `ApplyOptionsOverrides<T>`,
  `TryGetAnalyzerSection`, `MergeCollectionModel`, `BuildExecutionPolicy`). Shrank from 596 lines to
  ~230.
- **`CliConfigurationModels.cs`**: the ~23 per-analyzer properties on `CliConfigurationFileModel`,
  `AnalyzerOptionsModel`/`Sections` (the JSON-object-keyed `"Analyzers": {...}` binding), and the
  corresponding `[JsonSerializable(typeof(...))]` attributes.
- **`AnalyzerOptionsBuilder.BuildStringAnalysisFromCli`** — the one place a literal CLI flag, not
  just config.json, still overrode an analyzer option (`MaxDuplicateStringLength`/
  `MinDuplicateStringCount`).
- **`ResolvedExecutionOptions.cs`**: shrank from a 30-parameter positional record to 11 (`DumpPath`,
  `OutputPath`, `BaselineDumpPath`, `TrendDumpPaths`, `Diagnostics`, `Report`, `ConfigPath`,
  `UsedConfigFile`, `IncludeAnalyzers`, `ExcludeAnalyzers`, `DiagnosticMode`, plus `CacheDirectory`
  as an init-only extra). `ExecutionPolicy` dropped entirely.
- **`AnalyzerExecutionService.BuildContext`**: the 23-property `new AnalysisOptions { Xxx = resolved.Xxx, ... }`
  copy collapsed to `new AnalysisOptions()` — nothing external feeds it anything but defaults
  anymore, so there was nothing left to thread through.
- **`ExecutionPolicy` type deleted** (`src/DumpDetective.Core/Options/ExecutionPolicy.cs`).
  `DominatorAnalyzer.AnalyzeObjectsPass` (its only real consumer, the live-heap fallback path used
  when no disk-backed reverse index exists) now reads `FallbackMaxLeakScanObjects`/
  `FallbackMaxReferenceAddresses` — two `private const int` fields declared right at the top of the
  class with a comment explaining what they gate and why they're fixed.
- **4 dead CLI flags removed entirely** (`--high-reference-threshold`, `--max-duplicate-string-length`,
  `--min-duplicate-string-count`, `--max-reference-addresses`) — found, while doing this, that none
  of the four were ever actually wired to anything (`RootCommandBuilder`'s own pre-existing TODO
  said as much: *"Need to see if these options are necessary tbh... For now, leaving them in for
  advanced users"*). Also deleted `CliArguments.cs`, a record type with zero construction sites
  anywhere in the codebase, discovered dead while touching these same fields.
- **Export code blocks deleted**: `ThreadStackClusterAnalyzer`'s JSON/NDJSON-gz export (~60 lines),
  `StringAnalyzer`'s JSON/CSV/NDJSON-gz export (~80 lines), `WeakReferenceAnalyzer`'s NDJSON-gz
  export plumbing scattered across 6 call sites in `Analyze` (~90 lines total). Each analyzer's
  domain result now always passes `Artifacts: null`.
- **`StartupValidator`**: `ValidateRetentionOptions`/`ValidateStringAnalysisOptions`/
  `ValidateReferenceChainOptions` deleted — they validated config-supplied per-analyzer option
  values that can no longer exist (always the fixed default, always valid by construction).
- **Config files cleaned**: `config.sample.json` and `src/DumpDetective.Cli/config.json` both had
  dead `"Profile"` keys and (in the real, checked-in `Cli/config.json`) a fully commented-out
  `Analyzers` block — both stripped to the surviving top-level keys only.
- **`WarnIfLegacyProfileKeyPresent` generalized to `WarnIfDeadKeysPresent`** — now warns on any of
  `Profile`/`Analyzers`/`ExecutionPolicy` at the top level of a config file, not just `Profile`, so
  a config file written against the old system fails loud instead of silently doing nothing.

## What stayed configurable (pipeline-level, not analysis-affecting)

`DumpPath`/`OutputPath`/`BaselineDumpPath`/`TrendDumpPaths`/`ConfigPath` (which dump, where to
write), `IncludeAnalyzers`/`ExcludeAnalyzers` (a scope-selection feature, not an accuracy knob),
`CacheDirectory`/`DiagnosticMode` (pure operational), `ReportOptions` (output presentation, not
analysis), `DiagnosticsOptions` (the tool's own instrumentation/memory-management behavior, not an
analyzer's findings — not really a "per-analyzer option" despite living in the same options family).

## Gates

- Full non-real-dump suite green after every batch (1237 tests, down from ~1241 before this plan —
  the difference is entirely deleted tests that asserted now-removed config-binding behavior in
  `ConfigurationResolverTests.cs`, not a regression).
- `ConfigurationResolverTests.cs`, `StartupValidatorTests.cs`, and `ResolvedExecutionOptionsFactory.cs`
  (a test helper) all updated to match the shrunk `ResolvedExecutionOptions`/`AnalysisCommandRequest`
  shapes; new tests added confirming dead config keys (`Profile`, `Analyzers`, `ExecutionPolicy`) no
  longer cause resolution to fail.
- 0 errors, 0 new warnings in every touched file across the whole solution build.
