# ModuleAnalyzer — Phase 1 Audit

**Protocol:** [phase1-analyzer-architecture-review.md](../phase1-analyzer-architecture-review.md)
**Reviewer roles applied:** Principal .NET Runtime Engineer · ClrMD Expert · CLR/GC Specialist · Memory Diagnostics Engineer · Production SRE · Performance Engineer · Software Architect
**Files reviewed:** `ModuleAnalyzer.cs`, `ModuleDomainResult.cs`, `ModuleAnalysisOptions.cs`, `ModuleSectionBuilder.cs`, `ModuleFindingGenerator.cs`, `ModuleAggregator.cs`, `ModuleProbe.cs`, `ModuleAnalyzerDiscrepancyTests.cs`

---

## Audit Area 1 — Role & Opportunity Assessment

### Current Role

`ModuleAnalyzer` occupies a broad "runtime inventory" role that spans three conceptually distinct sub-domains:

| Sub-domain | Coverage |
|---|---|
| **Loaded module inventory** | Strong — counts, sizes, top-N list, dynamic/PE flags, full path |
| **Version/identity conflicts** | Moderate — detects same filename with different assembly identities |
| **Heap memory attribution by module** | Moderate — via `ModuleAggregator` + heap index |
| **AppDomain inventory** | Moderate — counts, top modules per domain, estimated managed bytes |
| **Dynamic/anonymous module accumulation** | Good — counts, bytes, finding with severity tiers |

### Coverage Gaps

1. **No `IsThreadSafe` declaration.** The interface default is implicitly `false`; concurrent pipeline runs on the same instance are not safe yet the contract is not documented.
2. **No `Tags` or `Order` overrides.** Both default to empty/0 despite the analyzer having clear semantic tags (`["modules", "runtime", "assemblies"]`) and a natural order position.
3. **`Analyze(ClrRuntime)` / `Analyze(ClrRuntime, IProgress<>)` public/private split.** Two non-interface sync overloads exist alongside `AnalyzeAsync`. The sync path bypasses `HeapAnalysisCache` entirely (no heap stats), creates a fresh `ModuleAnalysisOptions()` ignoring all configuration, and never calls `Stamp(this)`. This is a dead code path: no caller in production exercises it because `AnalyzeAsync` is the only pipeline-visible entrypoint. It represents a silent quality gap if ever called directly.
4. **AppDomain memory estimate is per-domain, not total.** The `EstimatedManagedBytes` per domain is accumulated from the heap index, but `ModuleDomainResult` exposes no `TotalEstimatedManagedBytes`. Callers must sum domains manually.
5. **Module load order not captured.** CLR module load sequence is preserved in `runtime.AppDomains[i].Modules` but nothing is emitted about load ordering, which is diagnostic for late-binding failures.
6. **No module-level GC generation data.** The heap index holds type aggregates including generation distribution; the analyzer consumes only `Count` and `TotalSize` but ignores `Gen0Count`, `Gen1Count`, `Gen2Count`, `LargeObjectCount` per `TypeAggregateIndexEntry`. A module-level "Gen2 memory by module" view would be high-value for leak triage.
7. **No LOH attribution.** Large-Object-Heap bytes are not broken out per-module despite being available from the index.
8. **`StratifiedSample` `ModuleSelectionMode` is defined in the enum but never implemented.** The switch on `ModuleSelectionMode` in `AnalyzeAppDomains` only checks `TopBySize` / `TopByTypeCount`; `StratifiedSample` falls through to the unsorted `modules` list — identical to the default path. This is a silent bug.

### Unexpected Functionality

- The `Analyze(ClrRuntime)` / `Analyze(ClrRuntime, IProgress<>)` overloads are dead-code helpers left over from pre-pipeline development. They bypass caching, ignore options, and skip `Stamp`. Should be removed.
- `ModuleProbe.ProbeAssemblyIdentity` uses `System.Reflection` on the live `ClrModule` object to discover metadata-retrieval methods. This is fragile across ClrMD minor versions and runs in a hot `foreach` loop inside `AnalyzeModules` (once per conflict candidate). The reflection cost is bounded by `versionConflicts` candidate count but the approach is unnecessarily opaque.

### Architectural Observations

- The analyzer straddles three logical concerns (module inventory, AppDomain inventory, heap attribution by module). Splitting into `ModuleInventoryAnalyzer` + `AppDomainAnalyzer` would improve cohesion, but the current merge is defensible given the tight data dependencies.
- `ModuleTypeAccumulator` is a mutable class rather than a `readonly struct`. For a dictionary value type accumulated in a loop this causes unnecessary heap pressure; a struct value type would avoid per-entry allocations (though the dictionary boxed-copy pattern requires care).

---

## Audit Area 2 — Diagnostic & Report Quality

### Strengths

- **Version-conflict finding** is well-constructed: severity tiers (info/warning/critical at 0/1+/3+), evidence includes full conflict count and names.
- **Type-density anomaly** finding is creative and genuinely useful — few-type modules consuming large heap memory is a real anti-pattern (large byte-array pools, single-class caches).
- **AppDomain inventory table** shows domain ID, address, module count, and estimated managed bytes — sufficient for multi-domain applications.
- **`TopModulesByTypeCount`** table provides the "module richness" dimension distinct from size.
- `ModuleSectionBuilder` uses `FormatBytes` consistently and truncates long strings for display — good report hygiene.

### Weaknesses

1. **Conflict evidence is shallow.** For each conflict group the report lists only `ModuleName + Instances.Count + up-to-3 assembly names`. Missing:
   - What the conflicting versions are (e.g. `1.2.0` vs `1.3.1`).
   - Which AppDomain each version was loaded into.
   - Whether either version is bound at runtime (active vs shadow-loaded).
2. **Finding severity thresholds are hardcoded in `ModuleFindingGenerator`** (`thresholdBytes = 200 * 1024 * 1024`) independently of `ModuleAnalysisOptions.HeavyModuleWarningThresholdBytes`. The options field exists but is unused in findings, so "heavy" means different things in options vs findings.
3. **`DensityAnomalyMaxTypes` is hardcoded as `≤5 types`** in the finding evidence text regardless of the configured options value.
4. **`TopModulesBySize` section builder emits `Address` as `0x...` hex with no linking context.** Not actionable without a debugger.
5. **`AppDomainSnapshot.TopModules`** is built to a hard cap of 8 entries with no configuration, but `ModuleEnumerationLimit` is configurable. The two limits are inconsistent and the domain-level top-modules list is often redundant with the global `TopModulesBySize` table.
6. **No summary narrative.** The section opens with one sentence (conflict-or-no-conflict) then immediately drops into tables. A brief structured summary ("X modules loaded across Y domains, Z dynamic, N conflicts — see conflict table") would orient engineers faster.
7. **`ExcludedModuleCount` is surfaced only as a metric** (no warning, no context). An engineer reading a report with 300 excluded modules has no indication this represents a potential blind spot.

### Report Improvements

- Emit version strings per conflict instance in `ModuleConflictGroup`.
- Propagate `HeavyModuleWarningThresholdBytes` from options into `ModuleFindingGenerator`.
- Add a "Module Analysis Summary" text block enumerating key numbers.
- Surface `ExcludedModuleCount > 0` as a `Warning` finding with context.
- Consider adding `Gen2Bytes` and `LOHBytes` columns to the heap-footprint table.

---

## Audit Area 3 — ClrMD & Platform Utilization

### ClrMD APIs

| API | Usage | Gap |
|---|---|---|
| `runtime.EnumerateModules()` | Used — with dedup by address | `IsDynamic`, `IsPEFile`, `Size`, `Name`, `AssemblyName` all accessed. `module.MetadataImport` not touched. |
| `runtime.AppDomains` | Used | `domain.Id`, `domain.Name`, `domain.Address`, `domain.Modules` all accessed |
| `module.EnumerateTypeDefToMethodTableMap()` | Used (with sampling guard) | This is the right API for type enumeration by module |
| `ClrModule.MetadataImport` | **Not used** | Direct metadata import can yield `AssemblyRef` table entries — the actual referenced dependency versions. This would allow the analyzer to report "what versions does this module *require*" vs "what versions are *loaded*". |
| `ClrModule.ImageBase`, `ClrModule.FileName` | Not distinguished | `module.Name` is the preferred path but `module.FileName` may differ in certain hosting scenarios |

### Infrastructure Utilization

- **Heap index (`TypeAggregates`)**: consumed correctly via `BuildModuleHeapStats` → `ModuleAggregator`.
- **Module registry (`index.Modules`)**: consumed by `ModuleAggregator` but is a *separate* module list from the one built in `AnalyzeModules`. There are now two module enumeration paths (live ClrMD scan in `AnalyzeModules` + pre-indexed in `ModuleAggregator`) with no reconciliation. If the heap index is stale or was built from a different scan pass, the counts may diverge.
- **`ObjectScanCounter`**: used appropriately in `AnalyzeModules`.
- **`HeapAnalysisCache`/`IHeapIndexBuilder`**: the `BuildModuleHeapStats` path casts `IHeapAnalysisCache` to `IHeapIndexBuilder` to get `HeapIndexBuildResult`. This bypasses the abstraction. If the cache implementation changes, this cast silently returns `null`.

### Missing Infrastructure

- There is no shared helper for emitting "enumeration truncated" findings — each analyzer inventing its own `warnings.Add(...)` pattern leads to inconsistent formatting.
- No `ModuleInfoIndex` (disk-backed). If the ClrMD module enumeration is repeated across multiple analyzers (e.g. `ExceptionAnalyzer` uses `ModuleDomainResult` to classify frame origins), the live module scan happens once in `ModuleAnalyzer` and the result is consumed by others — this is good. But there is no explicit index contract.

---

## Audit Area 4 — Diagnostic Opportunity Analysis

### High-Value Diagnostics Currently Missing

| Diagnostic | Value | Effort |
|---|---|---|
| **`AssemblyRef` version requirements via `MetadataImport`** | Detects version mismatch at the *requirement* level, not just what's loaded | Medium |
| **Gen2/LOH bytes per module** | Distinguishes "live" leak pressure modules from incidentally large ones | Low — data is in the index |
| **Module load count per AppDomain** (duplicate loads) | Detects re-loading of same module into multiple domains | Low |
| **Native image (NGen/R2R) vs JIT-compiled ratio** | `module.IsPEFile` is available but `IsReadyToRun` / `IsNgen` is not surfaced | Medium |
| **`AssemblyLoadContext` name per module** | High-value in .NET 5+ microservices — determines which ALC loaded each module and whether it is collectible | Low–Medium via reflection on ClrMD objects |
| **Top types per heavy module** | "The top 3 types in `Newtonsoft.Json.dll` are X, Y, Z consuming N MB" — directly actionable | Medium |
| **Cross-domain module sharing stats** | How many modules are loaded >1 time across domains | Low |
| **Anonymous module type names (sampled)** | Dynamic modules are anonymous but their types are enumeratable — sampling 3–5 type names would explain their origin | Medium |
| **Module age / load sequence index** | Modules loaded late (by ordinal position) are candidates for lazy-load or startup-time issues | Low |

### High-Value Statistics

- **Ratio: `DynamicModules / TotalModules`** — currently available but not computed as a metric.
- **`TopModulesBySize` vs `TopModulesByHeapMemory` overlap** — a module appearing in the top-N of both lists is a strong leak signal. Currently not surfaced.
- **Module address entropy** — whether all modules share the same base address range (indicating ASLR was disabled or rebase occurred). Unusual for diagnostics but relevant in crash analysis.

---

## Audit Area 5 — Performance, Memory & Scalability

### Heap Traversal

`AnalyzeModules` iterates `runtime.EnumerateModules()` — O(N) in module count, typically 100–3000 modules. This is not a heap traversal and scales well.

`AnalyzeAppDomains` iterates `domain.Modules` per domain and then `module.EnumerateTypeDefToMethodTableMap()` per module. The inner enumeration is bounded by `ModuleEnumerationLimit` (default 50) and `TypeEnumerationMode` (default `Full`). For Full profile with 100-module limit and 1000 types/module, this is 100,000 `MethodTable` lookups per domain. Across 3–5 domains = 300,000–500,000 lookups. Each `typeAggregates.TryGetValue(mt, ...)` is O(1) hash lookup — acceptable.

### Materialization Concerns

1. **`moduleTypeData` (`Dictionary<ulong, ModuleTypeAccumulator>`)**: allocated per `AnalyzeAppDomains` call. For 500 unique modules this is small.
2. **`moduleEntries` list**: allocated from `moduleTypeData.Values`, sorted in-place — small.
3. **`processedModuleAddresses` (`HashSet<ulong>`)**: allocated in `AnalyzeModules`, grows with module count. Acceptable.
4. **`stringPool` (`Dictionary<string, string>`)**: scope-local, explicitly cleared on exit. Good practice.
5. **`moduleByAddress` (`Dictionary<ulong, ClrModule>`)**: holds live `ClrModule` references through the conflict-detection phase. Explicitly cleared after use — correct. However, for dumps with thousands of modules (e.g. Azure Functions with many micro-assemblies) this could hold several hundred `ClrModule` objects simultaneously.

### Scalability Bottleneck

The dominant cost at scale is `module.EnumerateTypeDefToMethodTableMap()` in `AnalyzeAppDomains` when `PreferIndexOnly = false` and there is no pre-built heap index. On a 25GB dump with 500 modules per domain and `TypeEnumerationMode.Full`, this becomes 500 × avg_types_per_module iterations. If avg types = 2,000, that is 1,000,000 iterations per domain. With the `typeAggregates` index absent, none of those lookups produce data, making all that work wasted. The `PreferIndexOnly` guard addresses this partially, but the `Balanced` preset sets `PreferIndexOnly = false`, exposing the wasteful path by default.

**Recommendation**: when no heap index exists and `PreferIndexOnly = false`, the loop still runs but all `TryGetValue` lookups return false — `TypeCount` increments but all live stats remain zero. This should emit a warning and skip enumeration entirely (or the Balanced preset should set `PreferIndexOnly = true`).

### Cancellation

`cancellationToken.ThrowIfCancellationRequested()` is called at the top of `AnalyzeAsync` and inside the domain + type enumeration loops. Coverage is good.

### Progress Reporting

No progress reporting is implemented in `AnalyzeAsync`. The sync overload stubs `progress?.Report(new(0, "analyzing modules"))` but reports only at step 0. For large dumps where type enumeration is slow, there is no visibility into progress.

---

## Audit Area 6 — Correctness & Confidence

### Conflict Detection Logic

The conflict detection algorithm:

1. Groups modules by filename.
2. For each group with count > 1, partitions by `AssemblyIdentity`.
3. Counts "known" identities (have version, public-key-token, or file-hash).
4. If `knownCount > 1` → real conflict.
5. If single known identity (or all unknown) → annotate unknowns, skip conflict.

**Risk 1: Unknown identities are silently annotated but not reported as a finding.** A module with no version, no PKT, no file-hash appearing multiple times is annotated `(Unknown identity)` in `AssemblyName` but doesn't appear in `VersionConflictGroups`. This is correct defensively, but there is no finding or warning informing the engineer.

**Risk 2: `ModuleProbe.ProbeAssemblyIdentity(ClrModule, string)` uses reflection to find metadata methods.** If ClrMD provides a method named `GetMetadata` returning a non-byte array type, the `try { metaHash = ComputeHashHex((byte[])arr); } catch { }` path will silently swallow a `InvalidCastException` and return an identity without a hash. The conflict detection then falls back to name+version only, which may under-detect conflicts.

**Risk 3: Module deduplication is by address only.** `processedModuleAddresses.Add(module.Address)` ensures each address is visited once. If a `ClrModule` is somehow re-enumerated (e.g. via an enumeration bug in ClrMD), a zero address would be skipped correctly. However, if two different physical assemblies are mapped to the same address (unusual but possible in re-used address space after unload), they would be collapsed into one entry.

**Risk 4: `EstimatedManagedBytes` is approximate.** The heap index `TypeAggregates` maps `MethodTable → (count, totalSize)`. The per-domain accumulation sums `totalSize` for each live type belonging to modules in that domain. A single object cannot be attributed to two domains, but the index doesn't track which domain instance an object belongs to — only which `MethodTable` (type) it has. In multi-domain setups where both domains load the same module, the same object's bytes may be credited to both domains' `EstimatedManagedBytes`. This is a fundamental measurement limitation but is not surfaced to the user.

**Risk 5: `StratifiedSample` mode falls through silently.** As noted in Area 1, the enum value exists but the implementation falls through to unsorted enumeration. A caller expecting stratified sampling gets a silent degradation.

### False Positive Risk

- **Type-density anomaly**: a module with 1–5 types can legitimately dominate the heap (e.g. a ByteString buffer module, a logging sink). The finding is always `Warning` regardless of whether this is intentional. False positive rate is moderate.
- **Dynamic module accumulation**: threshold of 20 for Warning / 100 for Critical is reasonable but static. A process that intentionally generates 50 dynamic modules (e.g. a rules engine) will always fire Warning.

### False Negative Risk

- **Silent conflict suppression for unknown-identity modules**: described above.
- **No cross-domain duplicate-load detection**: if the same assembly is loaded into 10 domains independently, this is not flagged.

---

## Audit Area 7 — Industry Benchmark

### WinDbg + SOS

`!eeversion`, `!dumpdomain`, `!lm` (native modules), `!dumpmodule`. SOS offers:
- `!dumpmodule -metadata` → raw metadata token inspection.
- `!findappdomain` → resolves an object to its owning domain.
- Module base-address, size, flags.

**DumpDetective advantage**: automated conflict detection, heap attribution, density anomaly, finding severity — all absent from SOS.

**SOS advantage**: direct `MetadataImport` access, assembly-reference table inspection, per-module IL method enumeration.

### PerfView

PerfView `.NET Assembly` view shows loaded assemblies by name, version, GAC/local. Does not show heap attribution or conflict analysis.

**DumpDetective gap**: PerfView's JIT-compiled method view by module (showing which types/methods were JIT'd) is not replicated.

### Visual Studio Memory Profiler

Shows objects by type with module attribution. Does not show module-level load metadata or conflict detection.

### JetBrains dotMemory

Dominators by namespace/assembly — the closest analog to `TopModulesByHeapMemory`. dotMemory also shows retention paths grouped by assembly, which DumpDetective's module analyzer does not provide (retention is a separate `DominatorAnalyzer` concern).

**DumpDetective gap**: "top retained bytes by module" (dominator subtree rooted in module-level statics) is not emitted.

### Key Missing Capabilities Relative to Industry Tools

1. Assembly reference table (`AssemblyRef`) inspection — "what version does this module require?"
2. JIT compilation stats by module (method count, bytes of JIT'd code) — available in SOS via `!dumpmodule`.
3. Module-specific static root attribution — which statics in a given module are holding the most memory.
4. `AssemblyLoadContext` isolation information (.NET 5+).

---

## Final Executive Summary

### Overall Assessment

**Score: 72 / 100**

**Production-ready**: Yes, for inventory and conflict detection. Conditionally for heap attribution (requires pre-built index).

**Major Strengths**
- Complete module inventory with address deduplication.
- Version-conflict detection using per-assembly identity (not just name comparison) is technically sound.
- Heap attribution via `ModuleAggregator` adds genuine diagnostic value absent from standard tools.
- Type-density anomaly detection is a unique, high-value signal.
- Options presets allow fast/balanced/full profiles.
- Dynamic module accumulation finding with severity tiers is production-ready.

**Major Weaknesses**
- Dead sync overloads bypass caching, options, and `Stamp`.
- `StratifiedSample` mode is silently broken.
- Hardcoded threshold in `ModuleFindingGenerator` diverges from `ModuleAnalysisOptions`.
- Conflict evidence lacks version strings and domain context.
- `ExcludedModuleCount > 0` has no finding.
- No Gen2/LOH per-module breakdown despite data availability in index.
- `ModuleProbe` uses fragile reflection across ClrMD API surface.

---

### Priority Roadmap

| # | Recommendation | Area | Impact | Difficulty | Confidence | Classification |
|---|---|---|---|---|---|---|
| **P0** ✅ | Fix `StratifiedSample` mode or remove the enum value — silent fallthrough is a correctness bug | Area 1/6 | Medium | Low | High | Improvement |
| **P0** ✅ | Align `HeavyModuleWarningThresholdBytes` between `ModuleAnalysisOptions` and `ModuleFindingGenerator` — diverged thresholds produce inconsistent severity | Area 2/6 | Medium | Low | High | Improvement |
| **P1** ✅ | Remove or formally deprecate the sync `Analyze(ClrRuntime)` overloads; they bypass options and never call `Stamp` | Area 1 | Low | Low | High | Improvement |
| **P1** | Add version strings to `ModuleConflictGroup` instances; emit conflict versions in finding evidence | Area 2 | High | Low | High | Improvement |
| **P1** | Emit a `Warning` finding when `ExcludedModuleCount > 0` (analysis blind-spot notification) | Area 2 | Medium | Low | High | Improvement |
| **P1** | Add Gen2 + LOH bytes columns to `ModuleHeapStats` and the heap-footprint table — data is in the index | Area 4 | High | Low | High | Improvement |
| **P1** | In `Balanced` preset, set `PreferIndexOnly = true` or add a guard to skip type enumeration when no heap index exists and all results would be zero | Area 5 | Medium | Low | High | Improvement |
| **P2** | Expose `Tags` and `Order` on `ModuleAnalyzer` (`["modules","runtime","assemblies"]`, order ~60) | Area 1 | Low | Low | High | Improvement |
| **P2** | Replace `ModuleProbe` reflection with direct `ClrModule.MetadataImport` API once confirmed available in target ClrMD version | Area 3/6 | Medium | Medium | Medium | Improvement |
| **P2** | Add `IsThreadSafe` declaration (currently `false` by interface default; document explicitly) | Area 1 | Low | Low | High | Improvement |
| **P2** | Emit "unknown-identity duplicate modules" as a `Warning` finding rather than silently annotating `AssemblyName` | Area 6 | Medium | Low | High | Improvement |
| **P2** | Add a summary text block to `ModuleSectionBuilder` enumerating totals before tables | Area 2 | Medium | Low | High | Improvement |
| **P3** | Expose `AssemblyLoadContext` name per module (via reflection on ClrMD, .NET 5+) | Area 4/7 | High | Medium | Medium | Evolution |
| **P3** | Expose top-N types per heavy module as a sub-table ("top types in heaviest module") | Area 4 | High | Medium | High | Improvement |
| **P3** | Cross-domain duplicate load detection — module loaded in >1 domain flagged as a finding | Area 4 | Medium | Low | High | Improvement |
| **P3** | `AssemblyRef` table inspection via `MetadataImport` — "required version" vs "loaded version" per module | Area 4/7 | High | High | Medium | Evolution |

---

### Final Verdict

1. **Production-ready?** Yes for core scenarios (inventory, conflicts, heap attribution). The `StratifiedSample` bug and threshold divergence are correctness defects that should be fixed before expanded production use.

2. **Highest-impact improvements?**
   - Adding version strings to conflict evidence (P1, low effort, high value).
   - Gen2/LOH per-module breakdown (P1, low effort, data already in index).
   - Fixing `StratifiedSample` or removing it (P0, low effort).
   - Aligning `HeavyModuleWarningThresholdBytes` in findings (P0, trivial).

3. **Platform evolution opportunities?**
   - `AssemblyLoadContext` awareness is an increasingly important diagnostic dimension for .NET 5+ applications using plugin architectures and Blazor/hot-reload scenarios.
   - A `ModuleInfoIndex` (disk-backed, shared across analyzers) would eliminate the dual-enumeration pattern and let `ExceptionAnalyzer` and `TypeSystemSectionBuilder` consume pre-indexed module data without live ClrMD calls.

4. **Highest engineering return?** P0+P1 fixes together require ~1–2 days of work and would raise the effective diagnostic quality of the module section substantially, particularly for version-conflict investigation workflows which are the most common use of this analyzer in production incident reviews.
