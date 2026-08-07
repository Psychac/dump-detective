# CrashAnalyzer Audit Report

> **Scope**: `CrashAnalyzer.cs`, `CrashDomainResult.cs`, `CrashAnalyzerHelpers.cs`,
> `CrashAnalysisOptions.cs`, `ExceptionAnalysisSectionBuilder.cs`, `CrashFindingGenerator.cs`,
> `CrashTrendComparer.cs`, `CrashAnalyzerDiscrepancyTests.cs`
>
> **Protocol**: [phase1-analyzer-architecture-review.md](phase1-analyzer-architecture-review.md)
>
> **Date**: 2026-07-28

---

## Audit Area 1 â€” Role & Opportunity Assessment

### Current Role

`CrashAnalyzer` is the exception-domain analyzer. Its responsibilities are:

1. Scan the heap for all exception objects and count them by type.
2. Identify which exceptions are live on managed thread stacks at the moment of capture.
3. Match live exceptions to crash thread candidates with a 4-tier stack-trace inference chain.
4. Project that state into `CrashDomainResult` for the report and finding pipeline.

The scope is correct and cohesive. Crash/exception diagnosis does not trespass into thread-blocking
(rightly owned by `HangAnalyzer`) or heap-pressure analysis (`DominatorAnalyzer`, `StringAnalyzer`).

### Coverage Assessment

| Responsibility | Status | Notes |
|---|---|---|
| Exception type frequency | âœ“ Full | MethodTable cache, sorted by count |
| Active exception identification | âœ“ Full | `thread.CurrentException` per thread |
| Original stack trace extraction | âš  Partial | `_stackTraceString` path works; `_stackTrace` byte-buffer path is silently broken |
| Stack trace confidence annotation | âœ“ Good | 4-tier inference with `InferenceConfidence` surfaced in report |
| Exception message surfacing | âš  Partial | One `SampleMessage` per candidate; no message distribution per type |
| Inner exception chain | âš  Partial | Depth counted, inner type extracted, chain not traversed |
| Rethrow detection | âœ— Missing | `_remoteStackTraceString` present in CLR but not flagged |
| GC generation of exception objects | âœ— Missing | Available from ClrMD; not extracted |
| `AggregateException` unwrapping | âœ— Missing | Inner exceptions swallowed |
| Exception size / heap contribution | âœ— Missing | `HeapEntry.Size` available in the index at zero cost |
| Crash bucket / fault signature | âœ— Missing | No `(type, top_user_frame)` deduplication key |

### Coverage Gaps of Highest Value

**`AggregateException` unwrapping.** In TPL-heavy services â€” the dominant pattern in modern .NET
microservices â€” the top-level exception type is almost always `AggregateException`. The contained
exceptions carry the real signal. Without unwrapping, the exception type frequency table is
dominated by a near-useless count. This is the highest-value missing diagnostic.

**Crash bucket.** Without a `(exception_type, top_user_frame)` deduplication key, a dump with
1,000 `NullReferenceException`s from 40 different call sites is presented identically to one with
1,000 from a single pathological site. This is the single most important missing capability for
production SRE use.

**GC generation distribution.** Gen2 and LOH exception objects indicate the exception was
promoted through at least two collections â€” a retention signal. The data is available from the
heap index at zero marginal scan cost.

### Expansion Opportunities

**Exception-to-module attribution.** Cross-referencing the top user-code frame against the
module inventory (`ModuleDomainResult`) turns a raw frame string into an ownership attribution:
"YourApp.DataLayer.dll â€” 47 % of exceptions." Low complexity, high diagnostic value.

**Exception retention paths.** `DominatorAnalyzer` builds partial retention graphs. For Gen2
exception objects, a short root-path query (depth 3â€“5) against the existing reverse-reference
index would identify retention causes (static field, event handler, cache entry) without a new
heap scan.

### Architectural Observations

The dual execution path (`IHeapIndexScanParticipant` + parallel fallback `RunParallelExceptionScan`)
is a platform-wide pattern. Any feature added to the participant path must be duplicated in the
fallback or the two outputs will diverge. `CrashAnalyzerDiscrepancyTests` validates their agreement
but the duplication overhead is real. A platform-level shared extraction-function pattern â€”
where the per-entry processing body is a stateless method callable from both paths â€” would reduce
this burden across all participant analyzers.

---

## Audit Area 2 â€” Diagnostic & Report Quality

### Strengths

- **Severity escalation**: `FindingSeverity` progresses `Info â†’ Warning â†’ Critical` correctly.
  The lead finding triggers `Critical` when exceptions are live on thread stacks at capture time.
- **Active vs. total split**: Two separate tables (all-heap counts, active-only counts) make it
  immediately clear which exceptions are being actively thrown versus dormant leftovers.
- **4-tier stack-trace inference**: The `Exact â†’ ThreadId â†’ MessageHResult â†’ TypeInnerType`
  heuristic chain is a genuine engineering contribution. `InferenceConfidence` is surfaced in the
  report so the engineer knows whether a trace is definitively matched or a best-guess.
- **Inferred trace count**: Exposed as a key metric, signaling how much of the stack trace evidence
  is heuristic â€” important for calibrating confidence.
- **Frame origin classification**: The `FrameworkCode / ThirdParty / UserCode` table focuses
  attention on user-code frames in the call stack.
- **Exception chain depth histogram**: Surfaces patterns where exceptions wrap many inner exceptions
  â€” common in connection-pool exhaustion and serialization failures.
- **HRESULT included**: Present on both thread candidates and instance snapshots; directly actionable
  for COM/interop crashes.

### Weaknesses

- **`CreateFinding()` is dead code.** Defined at line 526 of `CrashAnalyzer.cs` as a private
  static method but never called. The actual finding is generated by `CrashFindingGenerator`, which
  has its own (slightly different) logic. The two implementations can diverge silently.
- **Hardcoded confidence of 0.85** in `ExceptionAnalysisSectionBuilder` on the lead finding
  regardless of the actual `InferenceConfidence` level. A report with zero `Exact` traces should
  not present 85 % confidence.
- **`MaxCurrentThreadFramesToPrint` defaults to 5.** Five frames is often insufficient to locate
  user code above framework infrastructure in async or middleware stacks.
- **No exception message aggregation.** Ten instances of `SqlException` can have ten different
  messages indicating ten different failure modes. The current design picks one `SampleMessage` per
  crash thread candidate and discards the rest.
- **`_remoteStackTraceString` appended as raw blob.** The fallback in `ExtractExceptionStackTrace`
  adds the entire remote stack string as a single list entry. If the string contains multiple frames,
  they are not split â€” the report renders one unsplit blob.
- **No cross-section commentary.** The section builder references `threads` and `modules` as
  `null` for most renders (they are `null` by default); the cross-reference note is conditional
  but the actual correlation data (e.g., "thread 17 is also the top blocked thread from HangAnalyzer")
  is never provided.
- **No HRESULT categorization.** `0x8007000E` (E_OUTOFMEMORY), `0x80004005` (E_FAIL), and
  `0x80131500` (COR_E_EXCEPTION) carry very different diagnostic implications; all are shown raw
  without commentary.

### Missing Diagnostics

| Diagnostic | Rationale |
|---|---|
| GC generation distribution of exception objects | Gen2/LOH exceptions are retained â€” a leak signal at zero marginal cost |
| Exception message distribution per type | Distinct messages identify distinct fault modes within one exception type |
| `AggregateException` inner exception counts | The outer type is nearly uninformative in TPL/async code |
| Rethrow detection via `_remoteStackTraceString` | Flags that the current thread is not the origin; lowers confidence of inference |
| Call-site grouping (same type, same top frame) | Distinguishes a systemic fault from scattered independent failures |
| Exception object heap size per type | Identifies exceptions retaining large strings or arrays |

---

## Audit Area 3 â€” ClrMD & Platform Utilization

### ClrMD â€” Correct Usage

- `thread.CurrentException` is the correct API for the exception currently being handled by a thread.
- `thread.EnumerateStackTrace()` for current thread frame capture is correct.
- `heap.GetObject(address)` with `IsValid` and `Type != null` guards on every access site.
- `_stackTraceString` read before `_stackTrace` â€” correct priority, the formatted string is the
  reliable source.
- Named field access (`_message`, `_HResult`, `_innerException`) via `GetFieldByName` with
  validity checks is technically correct when `ClrException` is not used.

### ClrMD â€” Issues

**`_stackTrace` parsing is incorrect (critical runtime semantics bug).**  
`ExtractExceptionStackTrace` reads `_stackTrace` as a managed object array, looking for elements
with a `_method` subfield. `Exception._stackTrace` in .NET is a raw CLR byte buffer containing
packed method descriptors and IP offsets. It is not a `StackTraceElement[]`. The loop will never
produce output and fails silently. This path is reached for any exception where `_stackTraceString`
is null â€” the common case for unhandled exceptions on background threads where the string has not
been formatted yet.

**`ClrException` wrapper not used.**  
ClrMD exposes `ClrException` via `ClrObject.AsException()` with typed properties: `.Message`,
`.HResult`, `.Inner`, `.StackTrace` (as `IList<ClrStackFrame>`). The field-by-field approach using
string field names is directly responsible for the `_stackTrace` misinterpretation and is fragile
against CLR internals changes. Migrating to `ClrException` would fix the broken `_stackTrace`
path, eliminate field-name lookup fragility, and simplify the code substantially.

**Double heap read per uncached MethodTable in the hot path.**  
`OnHeapEntry` calls `IsExceptionEntry` then `ResolveExceptionType` for the same address. Both
independently call `heap.GetObject(entry.Address)` for MethodTables not yet in the cache â€” two
heap reads for the same object on first encounter. Merge into one `GetObject` call.

**`.Take(10)` ceiling in `BuildActiveExceptionLookup` is below the configurable maximum.**  
`thread.EnumerateStackTrace().Take(10).ToList()` silently caps all thread stacks at 10 frames.
The `Full` profile supports up to `MaxCurrentThreadFramesToPrint = 40`. Any configured value
above 10 will never be satisfied because the source data was discarded at capture time.

**`IsExceptionEntry` uses substring match on type name.**  
`typeName.Contains("Exception", StringComparison.Ordinal)` matches `ExceptionDispatchInfo`,
`ExceptionHandlingMiddleware`, and `ExceptionFilterAttribute`. The correct check is
`clrObject.Type?.IsException` which traverses the base-type chain to `System.Exception`.

### Platform Infrastructure â€” Utilization

**`IHeapIndexScanParticipant` is correctly implemented.** The shared scan pass is the platform's
most important scalability mechanism; the analyzer participates correctly.

**`HeapEntry.Size` is present in the index but not consumed.**  
`HeapEntry` carries object size. Summing `entry.Size` for exception objects in `OnHeapEntry`
adds exception heap size per type at zero additional scan cost.

**`DominatorAnalyzer` reverse-reference infrastructure is not consumed.**  
For Gen2 exception objects, a short (depth 3â€“5) root-path query against the existing
reverse-reference index would identify retention causes without a new heap scan. The
infrastructure exists in the platform; the analyzer does not use it.

**`ModuleDomainResult` declared but never populated in the section builder.**  
`ExceptionAnalysisSectionBuilder` holds `ModuleDomainResult? modules = null`. The existing
module inventory could elevate frame origin classification from string-prefix heuristic to
assembly-accurate attribution.

### Design Issues (Analyzer Boundary)

**`BuildCrashThreadSnapshots` static wrapper breaks instance encapsulation.**  
`private static ... BuildCrashThreadSnapshots(ExceptionAnalysis analysis)` instantiates
`new CrashAnalyzer()` to call `BuildCrashThreadSnapshotsImpl`. The comment claims it exists for
unit tests but the method is `private` â€” unreachable from tests. It exists solely to avoid calling
an instance method. The fix is to call `BuildCrashThreadSnapshotsImpl` directly as `this.Build...`
from `BuildDomainResult`.

**`CreateFinding()` is dead code.**  
Defined as a `private static` method but never called. `CrashFindingGenerator` is the canonical
location. Two divergent implementations create silent maintenance risk.

**Dual execution paths must be kept in sync manually.**  
The participant path and `RunParallelExceptionScan` diverge in concurrency model and sort
semantics. Every new extraction feature must be added to both or the outputs will differ.
`CrashAnalyzerDiscrepancyTests` validates their agreement but does not prevent divergence.

---

## Audit Area 4 â€” Diagnostic Opportunity Analysis

### High-Value Opportunities

**1. `AggregateException` inner exception unwrapping**  
`AggregateException` exposes `InnerExceptions` â€” a collection of the actual failures.
Recursively unpack it and attribute counts and instances to the contained types. For TPL-heavy
services, the outer count is near-meaningless; the inner types are the real signal. This is the
single highest-impact diagnostic missing from the report.  
Impact: High. Difficulty: Low.

**2. Crash bucket / fault signature**  
Compute a `(exception_type, normalized_top_user_frame)` hash per exception instance. Group
instances by bucket; rank buckets by frequency. This is how WER, Sentry, and all production
APM tools deduplicate crashes. A dump with 10,000 `NullReferenceException`s from a single
call site is a systemic failure; the same count from 200 sites is scattered noise. Without a
bucket, both look identical.  
Impact: High. Difficulty: Medium.

**3. GC generation distribution of exception objects**  
Call `heap.GetObjectGeneration(entry.Address)` in `OnHeapEntry` at zero additional scan cost.
Gen2/LOH exception objects are retained through at least two GC cycles â€” a retention signal
that complements `DominatorAnalyzer` and correlates with what the reverse-reference index shows.  
Impact: High. Difficulty: Low.

**4. Exception message distribution per type**  
For the top-N types, accumulate distinct messages and count occurrences. Surface: total distinct
messages, most common message, message with highest active count. A `SqlException` with 200
instances and 3 distinct messages points to 3 query failures; 200 distinct messages indicates
systemic connectivity failure.  
Impact: High. Difficulty: Low.

**5. Rethrow detection and confidence adjustment**  
`_remoteStackTraceString` being non-null means the exception was rethrown via `throw;` or
`ExceptionDispatchInfo.Throw()`. When set, the current thread's call stack is not the origin.
Flag rethrown instances in the report and lower `InferenceConfidence` for affected candidates â€”
their top frames are the rethrow site, not the throw site.  
Impact: Medium. Difficulty: Low.

**6. Exception object heap size contribution per type**  
`HeapEntry.Size` is already in the participant scan. Sum it per exception type. Surfaces cases
where exception objects (via large `Message` strings, embedded arrays, or deep chains) contribute
meaningfully to heap pressure.  
Impact: Medium. Difficulty: Low.

**7. Exception-to-module attribution**  
Cross-reference the top user-code frame of each instance against `ModuleDomainResult`. Attribute
exception counts and active counts to owning assemblies. Turns frame classification from
string-prefix heuristic to assembly-accurate attribution.  
Impact: Medium. Difficulty: Medium.

**8. Exception retention paths for Gen2 objects**  
For Gen2/LOH exception objects, request depth-3 root paths from the reverse-reference index
already present in the platform. Identifies static fields or event handlers keeping the exception
alive â€” the essential question for leaked exception patterns.  
Impact: High. Difficulty: Medium (requires index integration).

---

## Audit Area 5 â€” Performance, Memory & Scalability

### Performance Assessment

**Strengths**

- Participant path costs one heap-index scan shared across all `IHeapIndexScanParticipant`
  analyzers. For a 10 GB dump with multiple participants, this is the difference between 1 pass
  and N passes.
- `Dictionary<ulong, bool>` MethodTable cache eliminates repeated `heap.GetObject` calls for
  exception types seen many thousands of times.
- `MaxExceptionsPerType` cap prevents unbounded list growth on dumps with millions of instances.
- Explicit loops in `BuildExceptionInstanceSnapshots` and `BuildCrashThreadSnapshotsImpl` avoid
  LINQ allocation chains in the aggregation phase.

**Issues**

| Issue | Location | Impact |
|---|---|---|
| 2Ã— `heap.GetObject` per uncached MT | `IsExceptionEntry` + `ResolveExceptionType` | Medium â€” redundant on first type encounter |
| `.Take(10)` stack ceiling | `BuildActiveExceptionLookup` | Medium â€” silent data loss for configurable profiles |
| `lock(candidateLock)` in hot path | `RunParallelExceptionScan.ProcessEntry` | Medium â€” serializes active-exception updates |
| `ConcurrentBag` + `Array.Sort` post-pass | `RunParallelExceptionScan` | Medium â€” memory spike before cap enforcement |
| Single `CancellationToken` check | `AnalyzeAsync` entry | Low â€” aggregation phase has no checkpoints |

### Scalability by Dump Size

| Dump Size | Participant Path | Parallel Fallback |
|---|---|---|
| 1â€“5 GB | Fast. Type cache warms quickly. | Functional. |
| 10 GB | Good. Bounded by `MaxExceptionsPerType`. | Acceptable. Lock contention moderate. |
| 25 GB | Good. Single-pass cost is shared. | Pressure from `ConcurrentBag` + sort. |
| 50â€“100 GB | Good. Memory footprint is options-bounded. | Risk: `ConcurrentBag` + `Array.Sort` memory spike before cap enforcement. |

### No `ILogger`

All exceptions inside `ExtractExceptionInfo` and `ExtractExceptionStackTrace` are swallowed by
bare `catch {}`. Per the platform convention, `ILogger<CrashAnalyzer>?` should be injected to
emit per-object diagnostics in verbose mode. This is especially important given the silently broken
`_stackTrace` path.

---

## Audit Area 6 â€” Correctness & Confidence

### Critical Bugs

**Bug 1 â€” `BuildCrashThreadSnapshots` static wrapper uses default options (not the configured instance options).**

```csharp
private static IReadOnlyList<CrashThreadCandidateSnapshot> BuildCrashThreadSnapshots(ExceptionAnalysis analysis)
{
    return new CrashAnalyzer().BuildCrashThreadSnapshotsImpl(analysis);
}
```

`new CrashAnalyzer()` uses `CrashAnalysisOptions.Default`. The `_options` of the running instance
â€” set via `BeforeHeapIndexScan` or the constructor â€” is never consulted.
`MaxOriginalStackFramesToPrint`, `MaxCurrentThreadFramesToPrint`, and `TopCrashThreadCandidates`
are applied from defaults regardless of the configured `AnalysisProfile`. A `Full` profile
requesting 40 frames delivers 20. A `Fast` profile requesting 12 also delivers 20.

**Bug 2 â€” `_stackTrace` parsed as a managed object array (incorrect runtime semantics).**

`ExtractExceptionStackTrace` treats `Exception._stackTrace` as a `StackTraceElement[]` with
`_method` and `_name` subfields. The field is a raw CLR byte buffer. The loop will never produce
output and fails silently. This path is reached for exceptions whose `_stackTraceString` is null â€”
common for unhandled exceptions on background threads, exceptions in release builds before
string formatting, and exceptions rethrown via `ExceptionDispatchInfo`.

**Bug 3 â€” Thread stack capture ceiling below the configurable maximum.**

`thread.EnumerateStackTrace().Take(10).ToList()` is hardcoded. Any future increase to
`MaxCurrentThreadFramesToPrint` beyond 10 silently delivers no additional frames.

### False Positives

- `typeName.Contains("Exception")` matches `ExceptionDispatchInfo`, `ExceptionHandlingMiddleware`,
  `ExceptionFilterAttribute`, and similar utility types. These are not `System.Exception` subclasses.

### False Negatives

- Custom exception classes without "Exception" in the name (e.g., `DatabaseError : Exception`,
  `FaultCondition : ApplicationException`) are excluded from all counts.

### Edge Cases Handled Correctly

- `ComputeExceptionChainDepth`: `HashSet<ulong>` guard prevents infinite loops on corrupted heaps
  with circular `_innerException` references. Correct.
- `OnHeapIndexScanCompleted(false)` correctly gates `_participantScanSucceeded` and falls back to
  the parallel path. Correct.

### Confidence Summary

| Evidence | Confidence | Basis |
|---|---|---|
| Active exception count | High | `thread.CurrentException` â€” direct runtime state |
| Total exception count | High | MethodTable-cache scan with type name check |
| `_stackTraceString`-based traces | High | Formatted string, correct CLR source |
| `_stackTrace`-based traces | None | Broken â€” byte buffer misread as object array |
| Inference tier 1 (Exact) | High | Candidate had its own `OriginalExceptionStack` |
| Inference tier 2 (ThreadId) | Medium | Heuristic ThreadId match |
| Inference tier 3 (Message+HResult) | Medium-Low | Messages can repeat across instances |
| Inference tier 4 (Type+InnerType) | Low | Type alone is not a unique identifier |
| Lead finding confidence (0.85) | Misleading | Hardcoded regardless of actual trace evidence quality |
| Frame origin classification | Low | String-prefix only; not module-backed |

---

## Audit Area 7 â€” Industry Benchmark

### Capability Comparison

| Tool | Capability | DumpDetective |
|---|---|---|
| SOS `!pe` | Type, message, HRESULT, inner type, `_stackTraceString` | âœ“ Covered |
| SOS `!threads` | Active exceptions per managed thread | âœ“ Covered |
| SOS `!dumpheap -type Exception` | All exception objects ranked by frequency | âœ“ Covered and ranked |
| SOS `!analyze -v` | Crash bucket / Watson fault signature | âœ— Missing |
| SOS `!clrstack -a` | Locals at throw site | âœ— Not feasible from dump alone |
| SOS `!gcroot` on exception | Retention path for exception object | âœ— Missing (infrastructure exists) |
| PerfView exception events | Throw rate, catch/rethrow sequence | âœ— N/A (dump only) |
| dotMemory | Group by origin call site | âœ— Missing (crash bucket would cover this) |
| dotMemory | `AggregateException` unwrapping | âœ— Missing |
| dotMemory | Exception retention tree | âœ— Missing (infrastructure exists) |
| VS Memory Profiler | Exception grouping with filter | âœ— No grouping |
| Application Insights / Sentry | Crash bucket, type+frame deduplication | âœ— Missing |

### Where DumpDetective Exceeds Baseline Tools

- **4-tier inference chain** is more sophisticated than any single SOS command. SOS `!pe`
  requires the engineer to manually correlate exceptions to threads; DumpDetective does it
  automatically with confidence annotation.
- **Exception type frequency ranking** across the full heap is not available from SOS without
  a manual `!dumpheap -type Exception` + `!pe` loop per instance.
- **`InferenceConfidence` annotation** is a unique feature â€” SOS does not indicate whether its
  stack trace is the origin or a rethrow site.

### Highest-Value Competitive Gaps

**Crash bucket** is the single most impactful missing capability. Every production incident
management tool is built on a `(exception_type, top_frame)` deduplication key. Without it,
DumpDetective cannot tell whether a heap with 10,000 exceptions represents one systemic failure
or 10,000 independent ones.

**`AggregateException` unwrapping** is standard in every .NET profiler (VS, dotMemory, Rider).
A crash analyzer that does not unwrap aggregates is significantly less useful for modern
TPL/async .NET services, which is the dominant production workload.

**Exception retention paths.** dotMemory shows exactly what is keeping an exception object
alive. For Gen2 exception objects this is the essential question; the infrastructure exists
in DumpDetective's platform but is not wired into this analyzer.

---

## Recommendation Classification

### Improvements (Enhance CrashAnalyzer)

| ID | Recommendation | P | Impact | Difficulty | Confidence | Status |
|---|---|---|---|---|---|---|
| I-1 | Fix `BuildCrashThreadSnapshots`: eliminate static wrapper, call `this.BuildCrashThreadSnapshotsImpl` | P0 | High | Low | High | ✅ DONE |
| I-2 | Replace `_stackTrace` byte-buffer parsing with `ClrObject.AsException()?.StackTrace` | P0 | High | Low | High | ✅ DONE |
| I-3 | Migrate full field extraction (`_message`, `_HResult`, `_innerException`) to `ClrException` wrapper | P1 | High | Medium | High | Pending |
| I-4 | Raise thread stack capture from `.Take(10)` to match `MaxCurrentThreadFramesToPrint` ceiling | P1 | Medium | Low | High | Pending |
| I-5 | Replace `typeName.Contains("Exception")` with `ClrType.IsException` / base-type walk | P1 | Medium | Low | High | ✅ DONE |
| I-6 | Remove dead `CreateFinding()` method | P1 | Low | Trivial | High | Pending |
| I-7 | Add `ILogger<CrashAnalyzer>?` injection; emit per-object diagnostics on `catch {}` sites | P1 | Medium | Low | High | Pending |
| I-8 | Add GC generation distribution per exception type (zero marginal scan cost) | P2 | High | Low | High | Pending |
| I-9 | Add `AggregateException` inner exception unwrapping | P2 | High | Medium | High | Pending |
| I-10 | Add exception message distribution per type (distinct count + most common message) | P2 | High | Low | High | Pending |
| I-11 | Implement crash bucket `(exception_type, top_user_frame)` | P2 | High | Medium | High | Pending |
| I-12 | Flag rethrown exceptions (non-null `_remoteStackTraceString`) and lower inference confidence | P2 | Medium | Low | High | Pending |
| I-13 | Derive lead finding `ConfidenceScore` from actual `InferenceConfidence` distribution | P2 | Medium | Low | High | Pending |
| I-14 | Add exception heap size per type (sum `HeapEntry.Size` in participant scan) | P3 | Medium | Low | Medium | Pending |
| I-15 | Cross-reference top user-code frames against `ModuleDomainResult` for assembly attribution | P3 | Medium | Medium | Medium | Pending |

### Evolutions (Improve the Platform)

| ID | Recommendation | P | Impact | Difficulty | Confidence | Status |
|---|---|---|---|---|---|---|
| E-1 | Add exception retention paths for Gen2 objects via existing reverse-reference index | P2 | High | Medium | High | Pending |
| E-2 | Wire `ModuleDomainResult` and `ThreadDomainResult` into section builder for cross-section correlation | P2 | Medium | Medium | High | Pending |
| E-3 | Define a platform-level shared extraction-function pattern for `IHeapIndexScanParticipant` dual paths | P3 | Medium | High | Medium | Pending |

---

## Final Executive Summary

### Overall Assessment

| Dimension | Score |
|---|---|
| Role clarity & coverage | 70 / 100 |
| Diagnostic & report quality | 68 / 100 |
| ClrMD & platform utilization | 52 / 100 |
| Diagnostic opportunity coverage | 58 / 100 |
| Performance & scalability | 72 / 100 |
| Correctness & confidence | 56 / 100 |
| Industry benchmark position | 60 / 100 |
| **Overall** | **62 / 100** |

**Production readiness: Conditionally ready.**

The analyzer correctly identifies active exceptions, ranks exception types by frequency, and
implements a non-trivial inference chain for original stack traces. On a typical crash dump
where `_stackTraceString` is populated it delivers actionable output. Two silent correctness
failures reduce fidelity: Bug I-1 (snapshot construction uses default options regardless of
configured profile) and Bug I-2 (`_stackTrace` byte-buffer path has never worked). Both are
low-difficulty fixes with high confidence.

### Major Strengths

1. `IHeapIndexScanParticipant` implementation shares one index pass across all participants â€”
   correct and necessary for 10 GB+ dumps.
2. 4-tier inference chain with `InferenceConfidence` annotation is a platform-differentiated
   capability with no equivalent in standard diagnostics tools.
3. Exception type frequency ranking across the full heap exceeds what SOS provides without
   manual scripting.

### Major Weaknesses

1. No `AggregateException` unwrapping makes the analyzer significantly less useful for modern
   async services â€” the dominant production workload.
2. No crash bucket means the report cannot distinguish a single pathological fault from
   scattered independent failures.
3. `_stackTrace` path is silently broken â€” exceptions captured before string formatting produce
   no original trace with no indication to the engineer.

### Final Verdict

**1. Is the analyzer production-ready?**  
Conditionally. Useful on typical crash dumps with `_stackTraceString` populated. Not reliable
for unhandled background-thread exceptions or any path where original stack traces depend on
the `_stackTrace` byte-buffer. Profile options are silently not applied to snapshot construction.

**2. What are its highest-impact improvements?**  
Fix I-1 and I-2 (P0 bugs), then add `AggregateException` unwrapping (I-9) and crash bucket
(I-11). Those four changes would take the analyzer from conditionally ready to a strong
production-grade crash diagnostic tool.

**3. What opportunities exist to evolve the platform?**  
E-1 (exception retention paths) leverages the existing reverse-reference infrastructure for a
new high-value use case at no new indexing cost. E-2 (cross-section wiring) would enable genuine
correlated findings across crash, thread, and module sections rather than conditional notes that
never materialize. E-3 (shared extraction helper) reduces the dual-path maintenance burden for
all `IHeapIndexScanParticipant` analyzers â€” a platform-wide improvement triggered by a pattern
observed here.

**4. Which recommendations provide the highest engineering return?**  
Migrating to `ClrException` (I-3) is the single highest-leverage change: it subsumes the P0
broken `_stackTrace` fix (I-2), eliminates all brittle field-name string lookups, simplifies
`ExtractExceptionInfo` substantially, and makes the implementation resilient to CLR internals
layout changes. It yields correctness, simplicity, and maintainability in one refactor.

---