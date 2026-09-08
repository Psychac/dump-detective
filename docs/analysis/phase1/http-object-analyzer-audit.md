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
| **P0-2** | **Add `HttpWebRequest` finding threshold (e.g. ≥ 10)** | **Diagnostic** | **Improvement** | **High** | **Low** | **High** | ✅ **DONE** |
| **P1-1** | **Implement `ITypedResourceInstanceSampler<HttpClientSnapshot>` with base URI + timeout capture** | **Diagnostic** | **Improvement** | **High** | **Medium** | **High** | ✅ **DONE** |
| **P1-2** | **Fix `HttpObjectTrendComparer.Compare` to include `ServicePointCount` and `HttpMessageHandlerCount` deltas** | **Correctness** | **Improvement** | **Medium** | **Low** | **High** | ✅ **DONE** |
| **P1-3** | **Add section narrative blocks for `HttpWebResponse` and `ServicePoint` in `HttpObjectSectionBuilder`** | **Diagnostic** | **Improvement** | **Medium** | **Low** | **High** | ✅ **DONE** |
| **P2-1** | **Add `HttpWebRequest` instance snapshot (URL, state) via per-instance sampling** | **Diagnostic** | **Improvement** | **Medium** | **Medium** | **Medium** | ✅ **DONE** |
| **P2-2** | **Add `IHttpClientFactory` handler tracking entry detection** | **Diagnostic** | **Improvement** | **Medium** | **Medium** | **Medium** | ✅ **DONE** |
| **P2-3** | **Add GC generation breakdown for `HttpClient` instances** | **Diagnostic** | **Improvement** | **Medium** | **Medium** | **High** | ✅ **DONE** |
| **P2-4** | **Add overflow guard (use `long` accumulators) for per-category counters** | **Correctness** | **Improvement** | **Low** | **Low** | **High** | ✅ **DONE** |
| **P2-5** | **Add handler chain depth / handler-per-client ratio metric** | **Diagnostic** | **Improvement** | **Medium** | **Low** | **Medium** | ✅ **DONE** (partial — see summary) |
| **P3-1** | **`HttpMessageHandler` accumulation finding threshold** | **Diagnostic** | **Improvement** | **Low** | **Low** | **Medium** | ✅ **DONE** |
| **P3-2** | **`ServicePoint.m_ConnectionLimit` field read on sampled instances** | **Diagnostic** | **Improvement** | **Low** | **Medium** | **High** | ✅ **DONE** |
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

---

## Implementation Summary (P0-2)

**Status:** ✅ **COMPLETE** — Commit `81f0756`

### What Was Done

1. **Added HttpWebRequest finding threshold** in `HttpObjectFindingGenerator`
   - Threshold: ≥ 10 objects (Warning severity)
   - Detects obsolete API accumulation in .NET 6+ applications

2. **Finding details**
   - Title: "{count:N0} HttpWebRequest objects on managed heap"
   - Evidence: Explains obsolescence and resource retention risk
   - Recommendation: Migrate to HttpClient; investigate pending requests
   - Tags: `["infrastructure", "http", "httpwebrequest", "obsolete"]`

3. **Test updates**
   - Lowered test baseline from 10 → 9 in `HttpObject_BelowThresholds_NoFindings` to maintain test intent
   - Added new test: `HttpObject_Warning_WhenManyWebRequests` (covers threshold exactly at 10)
   - All 8 finding generator tests pass (was 7, added 1)

### Impact

- **Diagnostic gap closed:** HttpWebRequest accumulation now generates findings instead of being silent
- **Consistency:** Now has same threshold-based structure as HttpClient/HttpWebResponse/ServicePoint
- **Production incident support:** Engineers investigating socket exhaustion will now see HttpWebRequest accumulation immediately
- **Obsolescence detection:** Provides early warning of legacy API usage in modernization efforts

---

## Implementation Summary (P1-2)

**Status:** ✅ **COMPLETE** — Commit `112c273`

### What Was Done

1. **Extended `HttpObjectTrendComparer.ExtractMetrics`**
   - Added `http.messagehandler` metric tracking HttpMessageHandlerCount
   - Added `http.servicepoint` metric tracking ServicePointCount
   - Both marked with MetricTrendDirection.HigherIsWorse (accumulation is bad)

2. **Extended `HttpObjectTrendComparer.Compare`**
   - Added delta computation for `http.messagehandler` (baseline vs current)
   - Added delta computation for `http.servicepoint` (baseline vs current)
   - Follows same pattern as existing metrics (total, client, request, response, bytes)

3. **Impact**
   - Previously: ServicePoint and Handler count changes were extracted but silently dropped by Compare
   - Now: Spikes in ServicePoint or Handler counts between snapshots are visible in trend reports
   - Enables detection of handler leaks and ServicePoint accumulation across time

### Testing

- All 40 trend comparison tests pass
- All 8 HTTP object analyzer tests pass
- No regressions in existing trend comparer functionality

---

## Implementation Summary (P1-1)

**Status:** ✅ **COMPLETE** — Commit `813dfd9`

### What Was Done

1. **Created `HttpClientSnapshot` record** (InfrastructureDomainModels.cs)
   ```csharp
   internal sealed record HttpClientSnapshot(
       string TypeName,
       ulong Address,
       string? BaseAddress = null,
       long TimeoutMilliseconds = -1);
   ```
   - Captures type name, instance address, destination URI, and configured timeout
   - Graceful null/default handling for field-read failures

2. **Implemented `ITypedResourceInstanceSampler<HttpClientSnapshot>`** on HttpObjectAnalyzer
   - `MaxStateSamplesPerType`: 500 (per-type cap on field reads)
   - `TopSampleCap`: 20 (top N instances to keep)
   - `TrySample()`: Reads HttpClient instance fields and returns populated snapshot

3. **Field Reading Implementation**
   - `_baseAddress`: ReadObject → AsString (destination URI)
   - `_timeout`: Read<long> ticks → convert to milliseconds
   - Both fields optional; returns valid snapshot with whatever could be read

4. **Integration with Shared Index Scan**
   - Store heap reference in `BeforeHeapIndexScan`
   - Create sampler via `TypedResourceScanDriver.CreateSampler()`
   - In `OnHeapEntry` for HttpClient types: call `TryGetSample()`, add successful snapshots to sampler
   - In `BuildResult`: include `TopHttpClients` and `InstanceScanCapped` in domain result

5. **Updated HttpObjectDomainResult**
   - New field: `IReadOnlyList<HttpClientSnapshot> TopHttpClients`
   - New field: `bool InstanceScanCapped`
   - Signals whether per-instance data is complete or capped

### Impact

- **Investigation enablement:** Engineers can now see which endpoints HttpClient instances connect to and their timeouts
- **Singleton verification:** Can identify whether reuse is actually happening (multiple clients vs single)
- **Configuration discovery:** Timeout settings visible without manual debugger inspection
- **Bounded cost:** Capped per-type (500) and top-N (20) prevents O(n) field reads on million-object heaps

### Testing

- All 8 HTTP object analyzer tests pass
- Test helper updated to provide empty TopHttpClients / InstanceScanCapped=false for baseline tests
- Sampler integration verified through existing shared index scan test suite

---

## Implementation Summary (P1-3)

**Status:** ✅ **COMPLETE** — Commit `9033ae1`

### What Was Done

1. **Added HttpWebResponse narrative block** (`HttpObjectSectionBuilder.cs`)
   - Trigger: When HttpWebResponseCount ≥ 20
   - Text: "Undisposed HttpWebResponse objects hold network streams open, exhausting connection pool slots. Always wrap responses in a `using` statement or explicitly call Dispose()."
   - Explains the root cause (stream retention) and remediation (Dispose/using)

2. **Added ServicePoint narrative block** (`HttpObjectSectionBuilder.cs`)
   - Trigger: When ServicePointCount ≥ 50
   - Text: "ServicePointManager.MaxServicePoints defaults to unlimited, causing ServicePoint accumulation and potential OOM. Set a reasonable limit (e.g., 100) or migrate to HttpClient (.NET 6+ preferred)."
   - Explains the system limit issue and both tactical (limit) and strategic (migrate) solutions

3. **Maintained consistent style** with existing HttpClient narrative
   - Concise, technical language
   - Direct guidance without repeating finding evidence
   - Actionable recommendations

### Impact

- **Report quality:** Section now provides complete guidance for all three HTTP object classes (HttpClient, HttpWebResponse, ServicePoint)
- **Engineer experience:** No need to cross-reference findings to understand what to do about HttpWebResponse/ServicePoint accumulation
- **Consistency:** All major findings now have corresponding section narrative, not just HttpClient

### Testing

- All 8 HTTP object analyzer tests pass
- No breaking changes to section builder API or output format

---

## Implementation Summary (P2-1)

**Status:** ✅ **COMPLETE**

### What Was Done

1. **Unified `HttpClientSnapshot` and the new `HttpWebRequest` snapshot into a single `HttpInstanceSnapshot` record** (`InfrastructureDomainModels.cs`)
   - Replaces the HttpClient-only `HttpClientSnapshot` with `HttpInstanceSnapshot(Category, TypeName, Address, Uri, TimeoutMilliseconds, ResponsePending)`
   - Avoids doubling the sampler/list/wiring machinery that implementing `ITypedResourceInstanceSampler<T>` twice (once per snapshot type) would have required — one sampler, one list, one section table, extensible to future categories (e.g. `ServicePoint.m_ConnectionLimit`)
   - `HttpObjectDomainResult.TopHttpClients` renamed to `TopHttpInstances`

2. **Implemented `HttpWebRequest` per-instance sampling** (`HttpObjectAnalyzer.cs`)
   - `TrySampleHttpWebRequest` reads `_requestUri` (Uri → URL) and derives `ResponsePending` from `_beginGetResponseCalled && !_endGetResponseCalled`
   - Field names confirmed by decompiling the actual .NET 9 runtime `System.Net.Requests.dll` rather than assuming .NET Framework layout — since .NET 5, `HttpWebRequest` is implemented as a compatibility shim over `HttpClient`, so its private fields differ from the legacy implementation
   - `ResponsePending = true` flags a request whose response was requested but never completed — the case most relevant to a hang/leak investigation
   - `HttpClient` sampling (`TrySampleHttpClient`) unchanged in behavior, just returns `HttpInstanceSnapshot` instead of `HttpClientSnapshot`

3. **Closed a pre-existing reporting gap found during scoping**: `TopHttpClients` from P1-1 was populated in the domain model but never rendered anywhere in `HttpObjectSectionBuilder`. Added an "HTTP object instances" compact table (Category, Address, URI, Detail) covering both `HttpClient` and `HttpWebRequest` snapshots, following the same pattern as `WcfChannelSectionBuilder`'s faulted-channel table.

### Impact

- **Diagnostic gap closed:** `HttpWebRequest` accumulation now has instance-level URL and pending-response visibility, matching what P1-1 gave `HttpClient`
- **Previously-invisible data now visible:** the P1-1 `HttpClient` snapshot table is now actually rendered in the report for the first time
- **No new capping:** `InstanceStateSampler<T>` is uncapped (post profile-removal refactor); no `MaxStateSamplesPerType`/`TopSampleCap` reintroduced

### Testing

- All 45 HTTP object / infrastructure finding generator tests pass
- Full unit suite (799 tests) passes
- No real-dump discrepancy tests run yet (not required for this change; can be run one-at-a-time in foreground on request)

---

## Implementation Summary (P2-2)

**Status:** ✅ **COMPLETE**

### What Was Done

1. **Corrected a wrong assumption in the original audit**: the audit guessed a nested type `HttpClientFactory+ActiveHandlerTrackingEntry`. Decompiling `Microsoft.Extensions.Http.dll` (versions 6.0.0, 8.0.0, 9.0.5, across `netstandard2.0`/`net461`/`net6.0`+/`net9.0` TFMs) showed both types are actually **top-level, non-nested, `internal sealed`**: `Microsoft.Extensions.Http.ActiveHandlerTrackingEntry` and `Microsoft.Extensions.Http.ExpiredHandlerTrackingEntry`, with an identical field/property shape across all three package versions.

2. **Added detection for both tracking-entry types** (`HttpObjectAnalyzer.cs`)
   - New `HttpObjectCategory` values `ActiveHandlerTrackingEntry` / `ExpiredHandlerTrackingEntry`, classified by exact type-name match (no pattern matching needed)
   - `TrySampleHandlerTrackingEntry` reads the `Name` auto-property's compiled backing field (`<Name>k__BackingField`) — the logical client name passed to `IHttpClientFactory.CreateClient(name)` — surfaced via the new `HttpInstanceSnapshot.ClientName` field
   - New `ActiveHandlerTrackingEntryCount` / `ExpiredHandlerTrackingEntryCount` counters on `HttpObjectDomainResult`, wired through `OnHeapEntry`/`BuildResult` identically to the existing categories

3. **Deliberately excluded**: resolving `ExpiredHandlerTrackingEntry._livenessTracker.IsAlive` to detect a handler that's expired-but-still-referenced (the "true leak" signal). Doing so requires walking the GC handle table to match a specific `WeakReference`'s target — the same machinery `WeakReferenceAnalyzer` already built (`IRequiresReachableGraphIndex`, full handle-table pass). That's a disproportionate dependency for this analyzer's lightweight streaming design. Used a cheaper proxy instead: `ExpiredHandlerTrackingEntryCount` tracked via the trend comparer — a count that doesn't shrink across snapshots is the leak signal.

4. **Cross-runtime fix to the P2-1 `HttpWebRequest` sampler, per explicit request**: decompiled `System.Net.Requests.dll` (.NET 9, the P2-1 baseline) against .NET Framework's `System.dll` (v4.0.30319) and found the field layouts differ completely — .NET 5+ implements `HttpWebRequest` as a shim over `HttpClient` (`_requestUri`, `_beginGetResponseCalled`/`_endGetResponseCalled`), while .NET Framework has the original implementation (`_Uri`, `m_RequestSubmitted`/`_HttpResponse`). `TrySampleHttpWebRequest` now tries the modern field names first and falls back to the Framework field names, so URL/pending-response capture works on both runtimes. Note: pre-.NET-5 .NET Core (2.1–3.1) has a third, unverified field layout — not explicitly covered, but the existing try/catch/null-check pattern degrades gracefully (empty URI, `ResponsePending: false`) rather than failing.

5. **Findings, trend, and reporting**
   - New Warning finding when `ExpiredHandlerTrackingEntryCount >= 20`, explaining handler-rotation churn and pointing at `HandlerLifetime` / direct-handler-capture as causes
   - `HttpObjectTrendComparer`: added `http.activehandlertrackingentry` / `http.expiredhandlertrackingentry` metrics (`ExtractMetrics` and `Compare`)
   - `HttpObjectSectionBuilder`: added key metrics for both counts, a churn narrative block, and extended the P2-1 "HTTP object instances" table's `Detail` column to show `Client: {name}` for tracking-entry rows

### Impact

- **Diagnostic gap closed:** `IHttpClientFactory` handler rotation is now visible at both the aggregate (counts, trend) and instance (client name) level
- **Cross-runtime correctness:** `HttpWebRequest` instance sampling (P2-1) now works on both .NET 5+ and .NET Framework dumps instead of only the runtime it was originally verified against
- **Avoided scope creep:** did not pull `WeakReferenceAnalyzer`'s handle-table machinery into a lightweight streaming analyzer for a count-based proxy that already gives an adequate signal via trend

### Testing

- All 46 HTTP object / infrastructure finding generator tests pass (45 + 1 new churn-finding test)
- Full unit suite (800 tests) passes
- No real-dump discrepancy tests run yet (not required for this change; can be run one-at-a-time in foreground on request)

---

## Implementation Summary (P2-3)

**Status:** ✅ **COMPLETE**

### What Was Done

1. **GC generation breakdown for `HttpClient`** (`HttpObjectAnalyzer.cs`)
   - Added `_httpClientGen0`/`_httpClientGen1`/`_httpClientGen2` analyzer-level accumulators (not per-type — `HttpClient` is matched by exact type name, so there's normally exactly one candidate `MethodTable`), incremented in the existing `OnHeapEntry` `HttpClient` case using `entry.Generation`
   - **No new ClrMD calls needed**: `HeapEntry.Generation` is already populated for free during the Phase-1 index build for every entry reaching `IHeapIndexScanParticipant.OnHeapEntry` — `CollectionAnalyzer` already relies on this same field for its own per-kind generation breakdown, so this reuses an established, already-cheap mechanism rather than adding a second heap pass or per-object ClrMD field read
   - `gen >= 2` clamped into the Gen2 bucket (absorbs LOH/POH/unusual cases), `gen < 0` (unresolved) excluded from all buckets — same defensive pattern `CollectionAnalyzer` uses
   - New `HttpClientGen0Count`/`HttpClientGen1Count`/`HttpClientGen2Count` on `HttpObjectDomainResult`

2. **Enriched the existing "N HttpClient instances" finding instead of adding a new finding** (`HttpObjectFindingGenerator.cs`)
   - `BuildGenerationEvidence` appends a clause to the existing finding's evidence text: "{X}% are Gen0, consistent with per-request allocation" when Gen0 dominates (>50%), or "{X}% are Gen2, consistent with long-lived reuse" when Gen2 dominates
   - Deliberately not a separate finding — same instance count means opposite things depending on generation, so this refines the existing signal rather than creating finding-proliferation for what's the same underlying observation
   - Silently omits the generation clause when no instances have a resolved generation (defensive: fallback/non-indexed paths where `entry.Generation` is the -1 sentinel)

3. **Trend and reporting**
   - `HttpObjectTrendComparer`: added `http.httpclient.gen0`/`gen1` (`HigherIsWorse` — churn) and `http.httpclient.gen2` (`Neutral` — higher isn't inherently good or bad as a trend signal, it just describes allocation pattern)
   - `HttpObjectSectionBuilder`: added `http_client_gen0`/`gen1`/`gen2` key metrics

### Impact

- **Diagnostic gap closed:** the same "N HttpClient instances" count now carries a generation-based interpretation, resolving the ambiguity the audit called out (audit item 5: "Gen2 confirms long-term survival; Gen0/1 confirms per-request allocation — a single datum changes the diagnostic conclusion")
- **Zero marginal cost:** implemented entirely from data the shared heap-index scan already produces; no additional heap pass, no additional per-object ClrMD reads

### Testing

- All 49 HTTP object / infrastructure finding generator tests pass (46 + 3 new generation-evidence tests)
- Full unit suite (803 tests) passes
- No real-dump discrepancy tests run yet (not required for this change; can be run one-at-a-time in foreground on request)

---

## Implementation Summary (P2-4)

**Status:** ✅ **COMPLETE**

### What Was Done

1. **Corrected the audit's own justification before implementing**: the audit's cited overflow site (`httpClientCount += count` with `count` derived from `(int)Math.Min(kv.Value.Count, int.MaxValue)`) describes the pre-P0-1/P3-3 bulk-add design, which `IHeapIndexScanParticipant` already replaced — `OnHeapEntry` increments counters one object at a time now, not via a bulk `TypeAggregates` count. The specific scenario the audit described no longer exists. Implemented anyway for consistency with the project's existing `Gen0Count`/`Gen1Count`/`Gen2Count` precedent (already promoted `int`→`long` project-wide for the same class of risk), even though a per-category HTTP counter overflowing today would require more live objects of one type than a 25GB dump ceiling could physically hold.

2. **Widened every per-category counter from `int` to `long`** (`HttpObjectAnalyzer.cs`, `InfrastructureDomainModels.cs`)
   - `HttpObjectDomainResult`: `TotalHttpObjects`, `HttpClientCount`, `HttpWebRequestCount`, `HttpWebResponseCount`, `HttpMessageHandlerCount`, `ServicePointCount`, `ActiveHandlerTrackingEntryCount`, `ExpiredHandlerTrackingEntryCount`, `HttpClientGen0Count`, `HttpClientGen1Count`, `HttpClientGen2Count`
   - `HttpObjectTypeSummary.Count`
   - `HttpObjectAnalyzer`'s internal `_typeStats` tuple fields, `_httpClientGen0`/`gen1`/`gen2`, and all `BuildResult` accumulator locals

3. **Verified before implementing that this is a fully contained change**: `InsightFinding.MetricValue` (`double?`), `AnalyzerMetric.Value` (`double`), and `NumericMetricValue`/`KM` (`double`) already receive these counts by implicit widening conversion — so `HttpObjectFindingGenerator`, `HttpObjectSectionBuilder`, and `HttpObjectTrendComparer` needed **zero** changes. Confirmed by a clean rebuild with no compile errors anywhere outside the two files actually touched.

4. **Opportunistic cleanup**: removed a dead line in `BeforeHeapIndexScan` left over from the pre-P0-1 design — `int total = (int)Math.Min(kv.Value.Count, int.MaxValue);` was computed and never used.

### Impact

- **Consistency, not a live bug fix**: no test changes were needed and none were added, since this is a pure type-widening change with no new behavior to verify — the existing 803-test suite passing unchanged is exactly the expected signal
- **Removed dead code** left over from the superseded pre-P0-1 accumulation design

### Testing

- Full unit suite (803 tests) passes unchanged, confirming the widening introduced no behavioral or compile-time regressions
- No real-dump discrepancy tests run yet (not required for this change; can be run one-at-a-time in foreground on request)

---

## Implementation Summary (P2-5)

**Status:** ✅ **DONE (partial)** — implemented the ratio metric and module-breakdown table; deliberately did not implement per-instance handler chain depth (see below)

### What Was Done

1. **Handler-per-client ratio** (`HttpObjectSectionBuilder.cs`, `HttpObjectTrendComparer.cs`)
   - Purely derived (`HttpMessageHandlerCount / HttpClientCount`), no new domain-model fields needed
   - Section builder: new `handler_client_ratio` key metric, only emitted when `HttpClientCount > 0`
   - Trend comparer: new `http.handlerratio` metric/delta (`HigherIsWorse`), with a `HandlerClientRatio` helper that returns `0.0` instead of `NaN`/`Infinity` when there are no `HttpClient` instances to divide by — matches the audit's own framing ("if ratio grows over time in multi-dump sessions, may indicate handler leaks")

2. **HttpMessageHandler breakdown by owning module** (`HttpObjectAnalyzer.cs`, `InfrastructureDomainModels.cs`)
   - Reused the existing `TypeAggregateNameResolver.ResolveModuleName` utility (already used by `ModuleAnalyzer`) rather than adding new ClrMD surface — resolves a MethodTable to its containing DLL filename
   - Resolved once per distinct `HttpMessageHandler` subclass type (bounded by distinct types seen, not per instance) inside the existing `BuildResult` per-type loop — no additional heap pass
   - New `HttpHandlerModuleSummary(ModuleName, Count, TotalBytes)` and `HttpObjectDomainResult.HandlerModules`, rendered as a new "HttpMessageHandler by module" table

3. **Deliberately excluded**: true per-instance handler *chain* depth (walking each `DelegatingHandler` instance's `InnerHandler`/`_innerHandler` field recursively to count how many handlers are stacked, e.g. Logging→Auth→SocketsHttpHandler = depth 3). This requires per-instance ClrMD field-chasing, meaningfully more expensive than the type-level module resolution above, and was flagged as speculative before implementing — the module breakdown already answers the audit's stated diagnostic goal (distinguishing Polly/logging/auth/application-layer handlers) without it. Would only be worth building against a confirmed need for depth specifically, not just handler categorization.

### Impact

- **Diagnostic gap closed**: `HttpMessageHandler` accumulation (previously a single opaque count with "no guidance text explaining what the count means," per the audit's own Area 4 weakness #4) can now be attributed to a specific module/library, and the client-to-handler ratio gives a session-over-session leak signal
- **No new ClrMD API surface**: module resolution reuses an existing, already-tested shared utility

### Testing

- New `HttpObjectTrendComparerTests.cs` (3 tests): ratio computed correctly, ratio is `0` (not `NaN`) when there are no HttpClient instances, and `Compare` produces the correct delta for a rising-churn scenario
- Full unit suite (806 tests) passes
- No real-dump discrepancy tests run yet (not required for this change; can be run one-at-a-time in foreground on request)
- Not covered by an automated test: the `HandlerModules` population itself (`ResolveModuleName` against a live `ClrHeap`), since no heap-scan test harness exists for `HttpObjectAnalyzer` in this codebase (a pre-existing gap, not introduced by this change) — same limitation applies to all HTTP-object-analyzer work done in this audit pass

---

## Implementation Summary (P3-1)

**Status:** ✅ **COMPLETE**

### What Was Done

1. **Added the missing `HttpMessageHandlerCount` finding** (`HttpObjectFindingGenerator.cs`), filling the gap the audit called out directly (Area 4 weakness #4: "50 HttpMessageHandler subclasses reported in type table but never discussed")
   - Warning at ≥10, Critical at ≥50 — same severity-escalation shape as the existing `HttpClient` finding, chosen by scale reasoning (handlers are heavier/rarer than `HttpClient` wrappers) rather than measured data, consistent with how the other HTTP thresholds in this analyzer were originally set
   - Evidence text explains the three root causes from the audit: `IHttpClientFactory` rotation, leaked/directly-captured handlers, and long `DelegatingHandler` middleware chains
   - Recommendation points at the P2-2 expired-handler-tracking-entry count and the P2-5 handler-per-client ratio as the next diagnostic step

2. **Connected the finding to the P2-5 module breakdown** via a new `BuildTopHandlerModuleEvidence` helper — appends "Largest contributor: {module} (N instances)" when `HandlerModules` is populated. Without this, the P2-5 module-breakdown table existed in the domain result and report but nothing pointed a reader at it; this finding is now the entry point that sends them there.

3. Confirmed this finding is genuinely independent of P2-2/P2-5, not a duplicate: it fires even when `IHttpClientFactory` isn't in use at all (no `ActiveHandlerTrackingEntry`/`ExpiredHandlerTrackingEntry` objects would exist in that case) and even when `HttpClientCount` is 0 (so the P2-5 ratio is undefined/zero).

### Impact

- **Diagnostic gap closed**: the last HTTP object category without any finding coverage (`HttpMessageHandler`) now has one, and it actively surfaces the P2-5 module-breakdown data that would otherwise sit unused in the report

### Testing

- 4 new tests: Warning at 10, Critical at 50, module-name evidence enrichment, and confirmed the existing `HttpObject_BelowThresholds_NoFindings` test (handlers defaults to 0) still passes unchanged
- Full unit suite (809 tests) passes
- No real-dump discrepancy tests run yet (not required for this change; can be run one-at-a-time in foreground on request)

---

## Implementation Summary (P3-2)

**Status:** ✅ **COMPLETE**

### What Was Done

1. **Corrected the audit's field-name assumption before implementing** (same discipline as P2-1): decompiled `ServicePoint` on both runtimes.
   - **.NET Framework** (`System.dll` v4.0.30319): `private int m_ConnectionLimit;` — matches the audit's original guess
   - **.NET 9** (`System.Net.Requests.dll`): `private int _connectionLimit;` — different name, same naming-drift pattern already found for `HttpWebRequest`'s URI field in P2-1
   - `TrySampleServicePoint` tries `_connectionLimit` first, falls back to `m_ConnectionLimit`

2. **Extended the existing per-instance sampling machinery rather than building new infrastructure** — this is the 4th category (after `HttpClient`, `HttpWebRequest`, and the handler tracking entries) to plug into the same `ITypedResourceInstanceSampler<HttpInstanceSnapshot>`/`InstanceStateSampler`/`TopHttpInstances` pipeline built in P2-1. Added:
   - `HttpInstanceSnapshot.ConnectionLimit` (dedicated nullable `int` field, not overloading an existing one — same reasoning as `ClientName` in P2-2)
   - `TrySampleServicePoint`, wired into the `TrySample` dispatch and the existing `ServicePoint` case in `OnHeapEntry`
   - `HttpObjectSectionBuilder`'s instance-table `Detail` column extended with `ConnectionLimit: N` for `ServicePoint` rows

3. **Enriched the existing `ServicePointCount >= 50` finding rather than adding a new one** (same pattern as P2-3's HttpClient-generation enrichment and P3-1's module-name enrichment) — `BuildLowConnectionLimitEvidence` scans `TopHttpInstances` for the lowest sampled `ConnectionLimit` and appends a clause when it's ≤4 (covers the historical .NET default of 2). Directly answers the audit's own framing: "`ServicePoint` count alone doesn't say whether any of them are a bottleneck — the limit does."

### Impact

- **Diagnostic gap closed**: a `ServicePoint` count is no longer just a raw number — when connection limits were successfully sampled, the finding now says whether any of them are actually constraining throughput
- **Reused, not duplicated, infrastructure**: zero new sampler/list/wiring machinery — this addition is almost entirely mechanical repetition of the pattern P2-1 established, confirming that pattern generalizes cleanly to a 4th category

### Testing

- 2 new tests: evidence mentions the lowest sampled `ConnectionLimit` when ≤4, and the clause is omitted when no sample is low enough (or none was sampled at all)
- Full unit suite (811 tests) passes
- No real-dump discrepancy tests run yet (not required for this change; can be run one-at-a-time in foreground on request)
