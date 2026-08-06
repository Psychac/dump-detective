# HttpObjectAnalyzer — Phase 1 Audit

**Analyzer:** `HttpObjectAnalyzer`
**Category:** Infrastructure
**Files reviewed:**
- `src/DumpDetective.Analysis/Analyzers/HttpObjectAnalyzer.cs`
- `src/DumpDetective.Analysis/Models/InfrastructureDomainModels.cs` (`HttpObject*` records)
- `src/DumpDetective.Analysis/Analyzers/TypedResourceScanDriver.cs`
- `src/DumpDetective.Analysis/Analyzers/TypedResourceSampler.cs`
- `src/DumpDetective.Analysis/Analyzers/ITypedResourceCandidateSource.cs`
- `src/DumpDetective.Analysis/Analyzers/TypeNamePatternMatcher.cs`
- `src/DumpDetective.Analysis/Trend/Comparers/HttpObjectTrendComparer.cs`
- `src/DumpDetective.Reporting/FindingGenerators/HttpObjectFindingGenerator.cs`
- `src/DumpDetective.Reporting/SectionBuilders/HttpObjectSectionBuilder.cs`
- `src/DumpDetective.Analysis/Insight/InsightEngine.cs` (http variable extraction)
- `tests/DumpDetective.Tests/Integration/CacheDiscrepancies/HttpObjectAnalyzerDiscrepancyTests.cs`
- `tests/DumpDetective.Tests/Unit/Analysis/InfrastructureFindingGeneratorTests.cs`

---

## Audit Area 1 — Role & Opportunity Assessment

### Current Role

`HttpObjectAnalyzer` scans the managed heap for five HTTP-object categories:
`HttpClient`, `HttpWebRequest`, `HttpWebResponse`, `HttpMessageHandler` subclasses, and
`ServicePoint`. It produces a pure count-and-bytes summary per type, raises findings for
misuse thresholds (many `HttpClient` instances, undisposed `HttpWebResponse`, runaway
`ServicePoint`), and supports trend comparison across dump snapshots.

The scope is narrower than peers in the typed-resource quartet. `DbConnectionAnalyzer` and
`WcfChannelAnalyzer` both read per-instance state fields (connection state, channel state) via
`ITypedResourceInstanceSampler`. `HttpObjectAnalyzer` does not; it stops at aggregate counts.
The documentation comment calls out the three misuse patterns — and the implementation
surfaces all three in `HttpObjectFindingGenerator` — so the stated role is fulfilled.

### Coverage Gaps

1. **No per-instance detail.** The analyzer cannot answer "which base address is this
   `HttpClient` talking to?" or "what is the status code on this undisposed
   `HttpWebResponse`?". This is the most significant capability gap relative to peers.
2. **HttpWebRequest not alerting.** The finding generator fires for `HttpWebResponse` ≥ 20
   and `HttpClient` ≥ 5, but no finding is generated for `HttpWebRequest`. A heap full of
   pending/leaked request objects is equally diagnostic.
3. **No handler chain topology.** Counting `HttpMessageHandler` subclasses is useful, but
   understanding the chain structure (DelegatingHandler → SocketsHttpHandler) reveals
   whether `IHttpClientFactory` is in use and whether handler lifetimes are managed.
4. **No connection pool state.** `SocketsHttpHandler` exposes pool limits and connection
   semantics. The current model captures none of this.
5. **No Polly / retry detection.** `ResiliencePipeline`-aware or `RetryPolicy`-wrapping
   handlers are an extremely common source of handler accumulation. Their presence changes
   the interpretation of handler counts entirely.
6. **`IHttpClientFactory`-pattern detection absent.** The factory's
   `HttpClientFactory+ActiveHandlerTrackingEntry` and expired handler queue are on the heap
   and are directly relevant to diagnosing socket exhaustion.
7. **GC generation not captured.** Whether `HttpClient` instances are in Gen0/1/2 (short- vs
   long-lived) materially changes the diagnosis. Gen2 `HttpClient` instances confirm long-term
   survival; Gen0 confirms per-request allocation churn.

### Unexpected Functionality

None. The analyzer is well-scoped and does not contain functionality that belongs elsewhere.

### Shared Infrastructure Observations

- `HttpObjectAnalyzer` does **not** implement `IHeapIndexScanParticipant`. All three of its
  typed-resource quartet peers (`DbConnectionAnalyzer`, `WcfChannelAnalyzer`,
  `TimerLeakAnalyzer`) do participate in the shared heap-index scan dispatch. This means
  `HttpObjectAnalyzer` relies entirely on `TypedResourceCandidateScanner` reading TypeAggregates
  from a pre-built `HeapAnalysisCache`, and falls through to a separate live heap walk when no
  cache is available. The inconsistency is undocumented.

---

## Audit Area 2 — Diagnostic & Report Quality

### Strengths

- Finding text is accurate, specific, and actionable. The HttpClient finding correctly explains
  TIME_WAIT socket exhaustion and recommends `IHttpClientFactory`. The `HttpWebResponse` finding
  correctly identifies the root cause (network stream held open) and the remediation (`using`).
- Severity progression (Warning at ≥ 5, Critical at ≥ 20 for `HttpClient`) is reasonable for
  production workloads.
- Trend comparison covers all five category counts plus per-type counts and bytes.
- Section builder exposes all key metrics in the `KeyMetrics` dict for dashboard consumption.

### Weaknesses

1. **Section narrative is thin.** The `HttpObjectSectionBuilder` emits at most one prose block
   (the HttpClient warning text when ≥ 5 found). HttpWebResponse and ServicePoint counts above
   threshold receive no in-section narrative; they produce findings in `InsightFinding` but the
   section itself gives no guidance.
2. **No per-instance table.** The report shows only "HTTP objects by type" (type name, count,
   heap size). An engineer cannot see instance addresses, URLs, or disposal state — the minimum
   needed to begin an investigation.
3. **HttpWebRequest is fully silent.** Any count of `HttpWebRequest` above zero is never flagged
   anywhere (no finding, no narrative). For a .NET 6+ application, the presence of `HttpWebRequest`
   objects is itself diagnostic — the API is obsolete and known to accumulate.
4. **Handler count without context.** 50 `HttpMessageHandler` subclasses is reported in the
   type table but never discussed. There is no guidance text explaining what that count means
   (factory handler rotation, leaked handlers, third-party middleware chains).
5. **`HttpObjectsFound = false` path.** When the fallback path fires (no HeapIndex) and HTTP
   objects exist, the section emits "No HTTP-related objects detected" — which is wrong. See
   Area 6 for the root cause.
6. **Missing aggregate for the `ByType` table.** The type table does not include a totals row,
   forcing the engineer to mentally sum counts and bytes.

### Missing Diagnostics

- Finding: `HttpWebRequest` present in a .NET 6+ application (obsolete API usage).
- Finding: `HttpMessageHandler` accumulation above a threshold.
- Narrative blocks in the section builder for `HttpWebResponse` and `ServicePoint` findings.
- Instance snapshot table (address, type, base URI, status code) for targeted investigation.

### Missing Statistics

- Handler accumulation rate (handler count / `HttpClient` count ratio).
- Heap size concentration: what fraction of total HTTP heap is `HttpClient` vs handlers.
- `TrendComparer` does not track `ServicePointCount` or `HttpMessageHandlerCount` deltas in
  `Compare()` — only total, client, request, response, and bytes.

---

## Audit Area 3 — ClrMD & Platform Utilization

### Missing `IHeapIndexScanParticipant`

The three other typed-resource quartet members each implement `IHeapIndexScanParticipant` (or
`IParallelHeapIndexScanParticipant`) and are registered with the shared
`HeapIndexScanDispatcher`. This allows them to accumulate data during the single shared
heap-index build pass. `HttpObjectAnalyzer` is not registered and instead relies on reading
TypeAggregates from a completed `HeapAnalysisCache` (or doing its own walk in the fallback).

This is not a performance regression when the cache is warm (TypeAggregates are an O(type
count) scan), but it creates the fallback correctness bug described in Area 6 and makes the
analyzer architecturally inconsistent with its peers.

### Per-Instance Field Access Not Used

Both `DbConnectionAnalyzer` and `WcfChannelAnalyzer` call `field.Read<int>(obj.Address)` to
read state enums from live `ClrObject` instances. `HttpObjectAnalyzer` reads no instance
fields at all. Fields that are directly useful and inexpensive to read:

| Type | Field | Value |
|---|---|---|
| `HttpClient` | `_timeout` (TimeSpan ticks) | Timeout configuration |
| `HttpClient` | `_baseAddress` (Uri) | Destination authority |
| `HttpWebResponse` | `m_StatusCode` (int) | HTTP status |
| `HttpWebRequest` | `_requestUri` (Uri) | Request URL |
| `ServicePoint` | `m_ConnectionLimit` (int) | Connection limit |

All of these follow the standard `field.Read<T>` or `field.ReadObject` patterns already used
in the codebase.

### TypeAggregateNameResolver Not Audited for HttpMessageHandler Subclasses

`TypedResourceCandidateScanner` resolves type names via `TypeAggregateNameResolver.ResolveTypeName`,
which falls back to `heap.GetTypeByMethodTable(mt)?.Name`. For generic or compiler-synthesised
handler subclasses (common with Polly pipelines), the resolved name may be a long generic
instantiation. The type table renders these faithfully, but the `ClassifyType` exact-match
branch for `HttpClient` etc. would still work correctly since those are not generic types.

### `ITypedResourceInstanceSampler<T>` Not Implemented

The interface exists precisely to gate per-instance state reads through the
`InstanceStateSampler<T>` cap mechanism. Not implementing it is deliberate given the current
scope, but it means adding per-instance field reads requires both implementing the interface
and registering with `TypedResourceScanDriver.TryGetSample`.

---

## Audit Area 4 — Diagnostic Opportunity Analysis

### High Value

1. **Per-instance HttpClient snapshot (base URI, timeout, handler type).** Resolving even the
   top 10–20 `HttpClient` instances would allow engineers to group clients by endpoint and
   identify whether singleton reuse is actually happening. Implement via
   `ITypedResourceInstanceSampler<HttpClientSnapshot>` with a cap of 20.

2. **HttpWebRequest URL + pending state.** Reading `_requestUri` and `_responseReceived` (or
   equivalent state flag) on sampled `HttpWebRequest` instances would show which endpoints are
   accumulating, which is the first question in any socket-exhaustion investigation.

3. **Handler chain depth and type breakdown.** Counting `DelegatingHandler` subclasses grouped
   by their declaring assembly name would distinguish Polly handlers, logging handlers, auth
   handlers, and application-layer handlers — immediately revealing architectural patterns.

4. **`IHttpClientFactory` handler rotation artifacts.** Detecting
   `HttpClientFactory+ActiveHandlerTrackingEntry` and `ExpiredHandlerTrackingEntry` objects on
   the heap confirms the factory pattern is in use and reveals how many handler rotations have
   accumulated (each rotation creates a new `SocketsHttpHandler`).

5. **GC generation breakdown for HttpClient.** Gen2 `HttpClient` confirms long-lived reuse
   (correct); Gen0/1 `HttpClient` confirms per-request allocation (incorrect). This single
   datum changes the diagnostic conclusion.

### Medium Value

6. **`ServicePoint.m_ConnectionLimit` field.** A `ServicePoint` with `ConnectionLimit = 2`
   under heavy load causes queuing and latency spikes. This is a single field read per sampled
   `ServicePoint`.

7. **`HttpWebResponse` status code histogram.** Grouping undisposed responses by HTTP status
   code (2xx vs 5xx vs timeout) would reveal whether the leak is in the success path, the
   error-handling path, or the timeout path.

8. **`HttpClientHandler` vs `SocketsHttpHandler` split.** These two have different connection
   pool semantics. Reporting which is in use tells an engineer whether they're on the
   legacy `WinHTTP`-backed path or the managed sockets path.

### Low Value

9. **`NetworkCredential` / `CredentialCache` accumulation.** These live on the heap alongside
   HTTP objects and can confirm credential-sharing patterns.

10. **Trend: handler count per client count.** If this ratio grows over time in multi-dump
    sessions, it may indicate handler leaks.

---

## Audit Area 5 — Performance, Memory & Scalability

### Current Performance Characteristics

When `HeapAnalysisCache` is populated (the normal pipeline path), `TypedResourceCandidateScanner`
reads TypeAggregates from the index — this is an O(distinct MethodTable count) iteration over
an in-memory dictionary. For a 10 GB dump with tens of thousands of types, this is fast (< 1
ms). The subsequent `candidates` loop is O(matched MethodTable count), which is tiny.

When no cache is available (fallback), the analyzer does a full `heap.EnumerateObjects()` pass.
On a 25 GB dump this could take 30–120 seconds. The fallback also has the correctness bug
(zero counts) that makes its output wrong, so the slow path is both slow and incorrect.

### Missing `IHeapIndexScanParticipant` Impact

Participating in the shared scan pass would eliminate the fallback path entirely. The
`TypeAggregates` are a natural fit for HttpObjectAnalyzer's needs (it only needs type name,
count, and total size — exactly what TypeAggregates provide).

The current design is acceptable for performance when the cache is warm but creates an
unnecessary footgun when the cache is cold.

### Scalability Assessment

The analyzer scales well at the type-aggregate level. There are no per-object allocations
beyond the `HttpObjectTypeSummary` records (one per matched type, typically < 10). No
materialization risk. Cancellation is checked in the candidates loop.

### Optimization Recommendations

- Implement `IHeapIndexScanParticipant` to eliminate the fallback path and align with peers.
- If per-instance snapshots are added, gate them with `InstanceStateSampler<T>` (cap 20) to
  bound ClrMD field access cost regardless of HttpClient count.
- The `byType.Sort` (in-place on a small list) is fine.

---

## Audit Area 6 — Correctness & Confidence

### Critical Bug: Fallback Path Produces Silently Wrong Results

**Evidence:** `TypedResourceCandidateScanner.DiscoverCandidates` fallback branch (no HeapIndex):

```csharp
candidateMts[mt] = (typeName, 0, 0);  // count=0, bytes=0 always
```

`HttpObjectAnalyzer.Analyze` then does:

```csharp
int count = (int)Math.Min(kv.Value.Count, int.MaxValue);  // always 0 in fallback
```

All per-category counters remain 0. `total = 0`. Result: `HttpObjectDomainResult` with
`HttpObjectsFound = false`, `TotalHttpObjects = 0` — even if thousands of `HttpClient`
instances are present on the heap.

The section builder then emits "No HTTP-related objects detected on the managed heap."

**Impact:** Any analysis run that does not pre-build the `HeapAnalysisCache` (e.g. a direct
call to `AnalyzeAsync` without a pipeline, or the integration test path before cache
prebuild) will silently produce wrong results. The discrepancy test only validates disk-vs-memory
agreement *after* prebuild, so this path is not exercised.

**This is a shared infrastructure bug** in `TypedResourceCandidateScanner`, but it affects
`HttpObjectAnalyzer` because — unlike `DbConnectionAnalyzer` and `WcfChannelAnalyzer` which
implement `IHeapIndexScanParticipant` and accumulate their own counts — `HttpObjectAnalyzer`
has no independent count accumulation and is entirely dependent on the scanner's output.

### HttpMessageHandler False Positive Risk

`IsHttpMessageHandler` uses:
```csharp
TypeNamePatternMatcher.HasPrefixAndSuffixOrContains(
    typeName, HttpNamespacePrefixes, "Handler", HttpMessageHandlerTokens)
```

This matches any type in `System.Net.Http.` that ends in "Handler" OR contains
"HttpMessageHandler". Types like `System.Net.Http.SomeOtherHandler` (hypothetical internal
or third-party type) would be classified as `HttpMessageHandler`. In practice, the
`System.Net.Http.` namespace is controlled by the BCL and NuGet `System.Net.Http`, so the
risk is low but present for third-party libraries that mirror the namespace.

### HttpWebRequest Counting Threshold Not Validated

The finding generator has no threshold for `HttpWebRequestCount`. A value of 10,000
`HttpWebRequest` objects produces no finding. This is inconsistent with the `HttpWebResponse`
threshold and could cause missed diagnoses in legacy .NET Framework applications.

### No Overflow Guard on Category Counter Accumulation

```csharp
httpClientCount += count;
```

`count` is `(int)Math.Min(kv.Value.Count, int.MaxValue)`. If multiple `HttpClient`-classified
MethodTables each contribute near-`int.MaxValue` counts (pathological heap), the addition
overflows. Checked arithmetic or `long` accumulators would prevent silent corruption. Low
practical risk, but present.

### TrendComparer Silently Drops ServicePoint and Handler Deltas

`HttpObjectTrendComparer.Compare` does not produce `MetricDelta` entries for
`ServicePointCount` or `HttpMessageHandlerCount`, even though `ExtractMetrics` tracks them
individually. A spike in `ServicePoint` count between two dumps would be invisible in the
trend comparison output.

---

## Audit Area 7 — Industry Benchmark

### WinDbg + SOS

`!dumpheap -type HttpClient` gives every instance address. `!do <addr>` shows `_baseAddress`,
`_timeout`, `_handler`. `!gcroot <addr>` identifies the retention root. An experienced analyst
can determine in minutes whether clients are shared singletons or per-request instances.

`HttpObjectAnalyzer` matches WinDbg's census capability (total count, by type) but cannot
replicate instance-level investigation without per-instance sampling. The finding generation
is ahead of WinDbg (no automated thresholds in SOS).

### PerfView

PerfView's GC heap snapshot view groups by type, shows count and size, and can compute
dominator trees. It does not provide automated threshold analysis or structured findings.
`HttpObjectAnalyzer` is ahead here.

### Visual Studio Memory Usage

The VS profiler shows type-level counts with live/dead breakdown and can identify retaining
references. It does not generate text findings. `HttpObjectAnalyzer`'s finding text is a clear
advantage.

### JetBrains dotMemory

dotMemory offers instance-level inspection, retention path visualisation, and type comparison
between snapshots (equivalent to `HttpObjectTrendComparer`). It would show the base URI on a
sampled `HttpClient` instance trivially. The absence of per-instance sampling in
`HttpObjectAnalyzer` is the most significant gap relative to dotMemory-class tooling.

---

## Final Executive Summary

### Overall Assessment

**Score: 58 / 100**

The analyzer correctly fulfils its stated role: detecting HttpClient misuse patterns, undisposed
responses, and ServicePoint accumulation via type-level counts. The finding text is high quality
and the trend infrastructure is solid. However, the analyzer has a critical correctness bug in
the no-cache fallback path, does not participate in the shared heap-index scan dispatch (unlike
all three of its peers), produces no per-instance diagnostic detail, and silently omits
`HttpWebRequest` from finding generation. These gaps limit its utility in production incident
investigations.

**Production readiness: Conditional.** Correct when the `HeapAnalysisCache` is pre-built via
the standard pipeline. Silently wrong when the cache is absent.

### Priority Roadmap

| # | Recommendation | Area | Type | Impact | Difficulty | Confidence | Status |
|---|---|---|---|---|---|---|---|
| **P0-1** | **Fix fallback path zero-count bug in `TypedResourceCandidateScanner` or implement `IHeapIndexScanParticipant`** | **Correctness** | **Evolution** | **Critical** | **Low** | **High** | ✅ **DONE** |
| P0-2 | Add `HttpWebRequest` finding threshold (e.g. ≥ 10) | Diagnostic | Improvement | High | Low | High | |
| P1-1 | Implement `ITypedResourceInstanceSampler<HttpClientSnapshot>` with base URI + timeout capture | Diagnostic | Improvement | High | Medium | High | |
| P1-2 | Fix `HttpObjectTrendComparer.Compare` to include `ServicePointCount` and `HttpMessageHandlerCount` deltas | Correctness | Improvement | Medium | Low | High | |
| P1-3 | Add section narrative blocks for `HttpWebResponse` and `ServicePoint` in `HttpObjectSectionBuilder` | Diagnostic | Improvement | Medium | Low | High | |
| P2-1 | Add `HttpWebRequest` instance snapshot (URL, state) via per-instance sampling | Diagnostic | Improvement | Medium | Medium | Medium | |
| P2-2 | Add `IHttpClientFactory` handler tracking entry detection | Diagnostic | Improvement | Medium | Medium | Medium | |
| P2-3 | Add GC generation breakdown for `HttpClient` instances | Diagnostic | Improvement | Medium | Medium | High | |
| P2-4 | Add overflow guard (use `long` accumulators) for per-category counters | Correctness | Improvement | Low | Low | High | |
| P2-5 | Add handler chain depth / handler-per-client ratio metric | Diagnostic | Improvement | Medium | Low | Medium | |
| P3-1 | `HttpMessageHandler` accumulation finding threshold | Diagnostic | Improvement | Low | Low | Medium | |
| P3-2 | `ServicePoint.m_ConnectionLimit` field read on sampled instances | Diagnostic | Improvement | Low | Medium | High | |
| **P3-3** | **Register `HttpObjectAnalyzer` as `IHeapIndexScanParticipant` for architectural consistency** | **Architecture** | **Evolution** | **Low** | **Medium** | **High** | ✅ **DONE** |

### Final Verdict

1. **Production-ready?** Conditionally. Correct only when the pipeline pre-builds the
   `HeapAnalysisCache`. The fallback path silently returns no findings even when HTTP objects
   are present, which constitutes a production correctness defect.

2. **Highest-impact improvements:**
   - Fix the fallback-path zero-count bug (P0-1) — correctness, low effort.
   - Add `HttpWebRequest` finding (P0-2) — common pattern, trivial addition.
   - Per-instance `HttpClient` snapshot (P1-1) — transforms the analyzer from census to
     actionable investigation tool.

3. **Platform evolution opportunities:**
   - P0-1 is a shared infrastructure fix in `TypedResourceCandidateScanner` that benefits all
     four typed-resource quartet members in no-cache scenarios.
   - P3-3 (registering as `IHeapIndexScanParticipant`) would make the quartet architecturally
     uniform and enable future shared-pass extensions.

4. **Highest engineering return:** P0-1 + P0-2 together take low effort and immediately close
   the correctness and silent-miss gaps. P1-1 provides the largest diagnostic capability jump
   per unit of effort.

---

## Implementation Summary (P0-1 + P3-3)

**Status:** ✅ **COMPLETE** — Commit `0764203`

### What Was Done

1. **Implemented `IHeapIndexScanParticipant` on `HttpObjectAnalyzer`**
   - Aligns architecture with `DbConnectionAnalyzer`, `WcfChannelAnalyzer`, and `TimerLeakAnalyzer` (the typed-resource quartet)
   - Eliminated the fallback path entirely; analyzer now participates in the shared heap-index scan dispatch

2. **Fixed P0-1 correctness bug**
   - Previous fallback path (when HeapAnalysisCache not pre-built) always returned 0 counts
   - Now routes through shared scan participant infrastructure, eliminating the bug completely
   - Scan state is tracked via `_scanSucceeded` flag; `AnalyzeAsync` only consumes accumulated state when scan succeeds

3. **Key Implementation Details**
   - `BeforeHeapIndexScan`: Discovers HTTP object candidates and pre-seeds per-type counters from TypeAggregates
   - `OnHeapEntry`: Accumulates category-specific counts (HttpClient, HttpWebRequest, HttpWebResponse, HttpMessageHandler, ServicePoint) as each index entry is processed
   - `OnHeapIndexScanCompleted`: Sets `_scanSucceeded` flag so `AnalyzeAsync` knows whether to trust accumulated state
   - `BuildResult`: Consumes accumulated state to generate the final domain result

### Impact

- **Correctness:** No-cache fallback path bug eliminated; all analysis paths now correct
- **Performance:** Eliminates redundant fallback scan; HTTP analysis now joins the shared pass (one scan instead of separate scans)
- **Architecture:** Achieves consistency across the typed-resource quartet; no more outliers
- **Testing:** All 7 existing HttpObject tests pass; no regressions

### Before/After

| Aspect | Before | After |
|--------|--------|-------|
| **Participant in shared scan?** | No (fallback only) | Yes ✅ |
| **Fallback path bugs?** | Yes (zero counts) | No ✅ |
| **Arch consistency** | Outlier | Aligned with peers ✅ |
| **No-cache correctness** | Broken (silent 0s) | Correct ✅ |
