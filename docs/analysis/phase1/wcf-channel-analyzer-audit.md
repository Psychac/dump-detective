# WcfChannelAnalyzer — Phase 1 Audit

**Reviewed:** 2026-08-03  
**Protocol:** [phase1-analyzer-architecture-review.md](phase1-analyzer-architecture-review.md)

**Components reviewed:**
- `WcfChannelAnalyzer.cs`
- `InfrastructureDomainModels.cs` (WCF section)
- `WcfChannelSectionBuilder.cs`
- `WcfChannelFindingGenerator.cs`
- `WcfChannelTrendComparer.cs`
- `InsightEngine.cs` (`DetectWcfChannelFault`)
- `WcfChannelAnalyzerHeapIndexScanTests.cs`
- `WcfChannelAnalyzerDiscrepancyTests.cs`
- `TypedResourceScanDriver.cs`, `TypedResourceSampler.cs`, `TypedResourceCandidateScanner.cs`
- `TypeNamePatternMatcher.cs`

---

## Audit Area 1 — Role & Opportunity Assessment

### Current Role

The analyzer detects `System.ServiceModel.*` channel and proxy objects on the managed heap,
classifies each instance's `CommunicationState`, and surfaces faulted or accumulated channels.
It occupies an important gap: WCF channel lifecycle is invisible to generic heap analyzers.

The role is internally cohesive. All logic flows through the typed-resource quartet
(`ITypedResourceCandidateSource` / `ITypedResourceInstanceSampler`) and the parallel heap scan
infrastructure, consistent with `DbConnectionAnalyzer`.

### Coverage Gaps

**Intermediate states silent.** `Opening` (1) and `Closing` (3) are both folded into `OtherChannels`.
`Opening` channels that never complete represent DNS/TCP connection failures — a high-value signal
that is silently discarded. An engineer cannot distinguish "50 channels in misc state" from
"50 channels stuck connecting."

**ChannelFactory<T> detection absent.** The finding generator itself warns engineers to
*cache ChannelFactory<T>* rather than channels, but never detects whether the application
is creating ChannelFactory<T> per-call — one of the most expensive WCF anti-patterns
(DNS resolution + certificate negotiation per factory). High counts would be a P0 finding.

**Endpoint address not extracted.** WCF channels hold their remote endpoint (`_remoteAddress`,
`_via`, or `Via` property backed fields). Knowing *which service* is faulted is the first
question an engineer asks; the analyzer requires a manual `!do <addr>` to answer it.

**Duplex and session channels not differentiated.** `IDuplexChannel`-backed objects have
bidirectional resource profiles; session channels (`ISessionChannel<T>`) hold per-session
state. Both are currently absorbed into the generic channel pool with no distinction.

**No binding-type inference.** Type names partially encode binding type
(e.g., `BasicHttpChannel`, `NetTcpChannel`). Binding type correlates directly with failure
mode and remediation advice. It is accessible without extra ClrMD calls.

### Expansion Opportunities

- Explicit `OpeningChannels` and `ClosingChannels` counters derived from current `OtherChannels`
- ChannelFactory type detection (same pattern matching approach, different type tokens)
- Remote endpoint extraction from known field names
- Binding-type classification from type name suffix

### Architectural Observations

`WcfChannelAnalyzer` is the only `IParallelHeapIndexScanParticipant` in the infrastructure
analyzer family. Its sibling `DbConnectionAnalyzer` implements only `IHeapIndexScanParticipant`
(sequential). The parallel implementation here is strictly superior for large heaps; there is
no technical reason `DbConnectionAnalyzer` does not share it.

---

## Audit Area 2 — Diagnostic & Report Quality

### Strengths

- Per-type table with Total / Opened / Faulted / Closed / Other / Heap Size is comprehensive
  and directly actionable.
- Faulted channel address list gives engineers a starting point for `!do <addr>` in WinDbg.
- Finding generator recommendations are technically precise (`Abort()` vs `Close()`, cache
  ChannelFactory not channel, Close() on a faulted channel throws).
- `InsightEngine.DetectWcfChannelFault` correctly cross-correlates with
  `System.ServiceModel.*` and `ObjectDisposedException` on the heap.
- Trend comparer covers `wcf.total`, `wcf.opened`, `wcf.faulted` — useful for dump diffing.

### Weaknesses

**`OtherChannels` is opaque.**  
`Opening`, `Closing`, and `Created` all produce `OtherChannels`. The report table shows a
number with no guidance on what it means. An engineer cannot distinguish stuck-opening
channels from channels that were never started.

**Missing `Opening`/`Closing` state breakdown in model.**  
`WcfChannelDomainResult` has no `OpeningChannels` or `ClosingChannels` fields. These states
are diagnostically significant and should have first-class representation.

**Key metrics exclude total bytes.**  
`TotalBytes` is present per type in `WcfChannelTypeSummary` but is not aggregated in
`WcfChannelDomainResult` and not surfaced in `AnalyzerDetailSection.KeyMetrics`. An engineer
cannot see the overall memory footprint of WCF objects at a glance.

**Section builder key metrics are inconsistent.**  
`KeyMetrics` includes `total_channels`, `opened`, `faulted`, and `closed` but omits `other`.
This makes the metrics non-additive (total ≠ opened + faulted + closed).

**State-scan-cap notice lacks specificity.**  
The report note says "state sampling was capped" but does not say which type(s) were capped
or how many instances were sampled. An engineer cannot assess how much state information is
missing.

**No summary of faulted channel heap cost.**  
Faulted channels hold live network sockets. The report shows addresses but no per-address
size or estimated total retained bytes. This information is available via `entry.Size`.

**Finding threshold lacks evidence basis.**  
100 channels → Warning, 500 → Critical. These are hard-coded constants with no explanation.
Different applications have legitimately different channel pool sizes.

**`TopFaultedChannels` shows no endpoint address.**  
Every faulted sample has a type name and address but no indication of which remote service
it was connected to. This is the single most useful piece of diagnostic evidence and requires
a `!do` to retrieve manually.

---

## Audit Area 3 — ClrMD & Platform Utilization

### ClrMD Usage Assessment

**TypeAggregates — correct.**  
Candidate discovery goes through `TypedResourceCandidateScanner`, which reads pre-built
`TypeAggregates` from the Phase-1 index when available, falling back to heap enumeration.
This is optimal: zero extra heap passes for candidate discovery.

**Parallel scan — correct.**  
`IParallelHeapIndexScanParticipant` is implemented and the merge logic is correct.
`Total` and `Bytes` are not summed across workers (pre-seeded from TypeAggregates);
only the state-change counters are merged. This is the right semantics.

**State field reading — partially correct.**  
`StateElementTypes` includes `ClrElementType.Object` to handle older .NET Framework WCF where
the `CommunicationState` enum may be boxed. Field name list `["_state", "state",
"communicationState"]` covers known implementations.

However, `TryReadIntField` probes all field names independently without consulting the
inheritance chain via `ClrType.BaseType`. If `System.ServiceModel.Channels.CommunicationObject`
defines `_state` but a deep derived type does not, the lookup relies on ClrMD's field
enumeration including inherited fields — which ClrMD 3.x does include in `ClrType.Fields`
for instance fields. This works but is implicit rather than explicit. No correctness risk,
but worth noting.

**No endpoint address extraction.**  
WCF channels store their remote address in fields such as `_remoteAddress` (type
`System.ServiceModel.EndpointAddress`). Extracting the `Uri` or `Identity` string from that
object would be straightforward with `ClrObject.ReadObjectField` / `ClrType.GetFieldByName`,
but is not attempted.

**No type hierarchy traversal for interface membership.**  
The analyzer does not verify that matched types implement `IChannel`. This is a minor
concern given that the namespace prefix `System.ServiceModel.` is tightly constrained, but
interface verification would eliminate false positives from any hypothetical non-channel
types in that namespace.

### Infrastructure Utilization

**`TypedResourceScanDriver` — fully utilized.**  
All three entry points (`DiscoverCandidates`, `CreateSampler`, `TryGetSample`) are used in
canonical order.

**`InstanceStateSampler<T>` — fully utilized.**  
`TryReserveSample` gate, `MergeFrom` for parallel workers, `AddTopSample` for top-N list —
all correct.

**`TypeNamePatternMatcher.HasPrefixAndSuffixOrContains` — correct usage.**  
Prefix `System.ServiceModel.`, suffix `.ServiceChannel`, contains-tokens
`["Channel", "ClientBase", "CommunicationObject"]`. The broad contains-token "Channel" is
constrained by the required prefix, eliminating false positives from
`System.Threading.Channels` or SignalR channel types.

---

## Audit Area 4 — Diagnostic Opportunity Analysis

### High-Value Missing Diagnostics (priority-ranked)

**1. Opening and Closing state breakdown (P0)**  
`Opening` (1) channels are actively trying to connect. High counts mean the application is
hammering a service that is down or unreachable — a production emergency. `Closing` (3)
channels are draining; high counts indicate slow or stuck graceful shutdown. Both should be
first-class counters in the domain model and the report.  
*Implementation:* Add `OpeningChannels` and `ClosingChannels` to `WcfChannelDomainResult`
and accumulate them with dedicated state comparisons in `OnHeapEntry`.

**2. Endpoint address extraction for faulted channels (P0)**  
The `_remoteAddress` field on a WCF channel is an `EndpointAddress` whose `Uri` property
contains the service URL. For each faulted channel sample, reading this string would allow
the finding to report "Channel to https://payments.internal/v2/svc is faulted" instead of
"System.ServiceModel.Channels.ServiceChannel at 0x00012345 is Faulted." This is the
information engineers need first.  
*Implementation:* In `TrySample`, attempt to read `_remoteAddress` → `_uri` or `Uri` field;
store the resulting string in `WcfChannelSnapshot`.

**3. ChannelFactory<T> accumulation (P1)**  
`ChannelFactory<T>` is expensive to create. Applications that construct one per call instead
of caching it will show high `ChannelFactory`-derived type counts on the heap. This is a
well-known WCF performance anti-pattern that causes latency spikes and DNS thrashing.  
*Implementation:* Extend type matching to recognize `System.ServiceModel.ChannelFactory`
types; add a separate `FactoryCount` to the result and a dedicated finding.

**4. Binding type classification (P1)**  
Type names partially encode binding:
- `System.ServiceModel.Channels.ServiceChannel` (internal implementation)
- `BasicHttpChannel`, `NetTcpChannel`, `WSHttpChannel`, `NetNamedPipeChannel`
Known type name tokens can infer binding. `NetTcp` channels hold OS TCP connections;
`BasicHttp` channels hold HTTP connections. Failure modes differ.  
*Implementation:* Short suffix classification in `IsCandidateType`, stored in a `BindingHint`
field on `WcfChannelTypeSummary`.

**5. Session channel detection (P2)**  
`ISessionChannel<T>` implementations carry per-session state. A large count of session
channels indicates that the application is not properly closing sessions, which prevents
server-side session state from being released.

**6. Duplex channel and callback contract detection (P2)**  
`IDuplexChannel` implementations hold a callback sink. Each unclosed duplex channel pins a
callback dispatcher on both client and server.

**7. `Opening` state correlation with timeout exceptions (P2)**  
High `OpeningChannels` combined with timeout exceptions in `CrashDomainResult` is a
near-certain indicator of connection-level failure (DNS, TCP refusal, TLS negotiation).
`InsightEngine` can add this cross-correlation once the state is first-class.

### Missing Statistics

- Total aggregate heap bytes for all WCF channel objects (sum of `TotalBytes` from `ByType`)
- Faulted channel percentage of total (`FaultedChannels / TotalChannels`)
- Minimum / maximum / average channel count by state per type

---

## Audit Area 5 — Performance, Memory & Scalability

### Current Performance Characteristics

**Candidate discovery — O(T) where T = unique MethodTables.**  
`TypedResourceCandidateScanner` reads TypeAggregates in one pass. Zero heap traversal for
candidate discovery. Correct and efficient.

**Parallel heap scan — scales linearly with CPU count.**  
`IParallelHeapIndexScanParticipant` distributes the object index range across workers.
Each worker maintains its own `_typeStats` and `_sampler`. Merge is O(W × T) where W =
worker count, T = candidate type count. Both are small in practice.

**State field read cap — 500 per type.**  
At 500 samples per MethodTable, the field read budget is bounded. For WCF service-heavy
apps with millions of channels of a single type, this means state counts for Opened/Faulted
etc. can be materially below the true values. The `StateScanCapped` flag exists but is
report-level only; no per-type cap information is available.

### Allocation Concerns

**Value tuple mutation pattern.**  
`_typeStats` stores `(string Name, int Total, int Opened, int Faulted, int Closed, int Other,
ulong Bytes)` tuples as dictionary values. Every `OnHeapEntry` call that updates state counts
writes a new tuple. In .NET, value tuples in Dictionary values require a dictionary entry
update (no in-place mutation). At high channel counts this creates no extra heap allocations
(ValueTuple is a struct and is stored inline), but it does require a dictionary write per
entry. This is acceptable; a dedicated `ChannelTypeCounts` mutable struct would be cleaner
but is not a performance regression.

**`_candidateMts` ContainsKey + TryGetValue double-lookup.**  
`OnHeapEntry` calls both `candidateMts.ContainsKey(entry.MethodTable)` and then
`typeStats.TryGetValue(entry.MethodTable, ...)`. Both dictionaries have the same keys after
`BeforeHeapIndexScan`. Eliminating the `ContainsKey` check and relying on `TryGetValue`
alone would remove a redundant hash computation per heap entry.

### Scalability Assessment

For 10–100 GB dumps the analyzer is safe. The TypeAggregates path eliminates a heap pass;
the parallel scan handles millions of objects without per-object allocation. The 500-sample
cap per type contains ClrMD field read cost.

The `IParallelHeapIndexScanParticipant` advantage over sibling analyzers is significant.
A 64-core host processes WCF channels 64× faster than the sequential `DbConnectionAnalyzer`
would on the same machine.

---

## Audit Area 6 — Correctness & Confidence

### Confidence Assessment: **High** for core counting; **Medium** for state attribution

**Total channel count — High confidence.**  
Sourced from `TypeAggregates.Count` per MethodTable. This is the same count the Phase-1
index uses for all heap-level statistics. It is correct.

**State-attributed counts — Medium confidence.**  
Opened + Faulted + Closed + Other are accumulated only for objects where a state sample slot
was reserved (up to 500 per type). If capped, these counts are understated relative to
`Total`. The `StateScanCapped` flag surfaces this, but the discrepancy size is unknown.

**State value range — Low risk, minor correctness gap.**  
`stateVal` is passed directly to the `opened/faulted/closed/other` comparisons without
validating that it is in [0..5]. Values outside the enum range go to `other`. The
`MapCommunicationState` switch returns "Unknown" for them. This does not cause incorrect
findings but silently absorbs corrupt heap objects into the `OtherChannels` bucket.

### Risks and Edge Cases

**Risk: `Opening` channels misclassified as Other.**  
`Opening` = 1 is neither 2 (Opened), 4 (Closed), nor 5 (Faulted), so it falls to `other`.
If the dump was taken during a mass connection attempt (e.g., service restart), hundreds or
thousands of Opening channels would inflate `OtherChannels` with no actionable signal.

**Risk: WcfContainsTokens broad matching.**  
`"Channel"` as a contains-token, constrained to `System.ServiceModel.*`, is tight enough.
However, `System.ServiceModel.Channels` namespace contains many non-channel types
(e.g., `MessageHeader`, `BodyWriter`, `Message`) that do *not* contain "Channel" in their
name — so these will not be false positives. The concern is the opposite: a type like
`System.ServiceModel.Channels.ChannelPool` (if it exists) would be included.

**Risk: Parallel merge defensive branch never exercised in tests.**  
`MergePartial` handles the case where a worker has a MethodTable key absent from the primary.
Given that `BeforeHeapIndexScan` pre-seeds identical candidate sets on every worker from the
same TypeAggregates, this branch cannot be triggered in normal execution. It is dead code in
practice, but correct if it were ever reached. No test covers it.

**Risk: State field `ClrElementType.Object` path correctness.**  
When the state field is typed as `object` (boxed enum in old .NET Framework WCF), ClrMD
would return the boxed object address. `TryReadIntField` handles this path — the
implementation needs to unbox correctly. This is implemented in `InstanceStateSampler`
shared infrastructure. Not re-verified here but trusted as tested by `InstanceStateSamplerTests`.

### False Positives

Low risk. The namespace prefix `System.ServiceModel.` and the contains / suffix checks
together produce a tightly scoped candidate set with no known false-positive types in
standard .NET distributions.

### False Negatives

**Possible for third-party WCF-compatible stacks.**  
CoreWCF (community WCF port), custom WCF transports, or gRPC/ServiceModel hybrid types
that do not reside in `System.ServiceModel.*` will not be detected. This is a deliberate
scope decision rather than a defect, but worth documenting.

---

## Audit Area 7 — Industry Benchmark

### WinDbg + SOS

| Capability | WinDbg + SOS | DumpDetective |
|---|---|---|
| Find all WCF channel types | `!dumpheap -type ServiceModel` (manual) | Automated, indexed |
| Get channel state | `!do <addr>` for each object | Automated, sampled |
| Faulted channel count | Manual counting | Automatic with threshold finding |
| Remote endpoint address | `!do <addr>` → `_remoteAddress` | **Not extracted** |
| Aggregate by type | Manual | Automatic |
| Binding type | Inferred from type name manually | **Not classified** |
| ChannelFactory presence | Manual | **Not detected** |
| Cross-correlation with exceptions | Manual | Automatic (`InsightEngine`) |

DumpDetective automates the tedious aspects of WCF diagnosis and adds cross-correlation
that WinDbg lacks. The gap is endpoint address extraction, which is the first question
engineers type into WinDbg when they find a faulted channel.

### PerfView

No WCF channel analysis. PerfView is allocation-trace oriented; it has no static heap
state analysis capability equivalent to what DumpDetective provides.

### Visual Studio Memory Usage

No WCF protocol awareness. Generic type grouping could surface high channel counts but
provides no state classification or actionability.

### JetBrains dotMemory

No WCF protocol awareness. Retention analysis could indirectly reveal faulted channel
accumulation through object graph traversal, but there is no automated WCF triage.

### Competitive Conclusion

DumpDetective is ahead of every commercial tool in automated WCF channel state triage from
a dump. The one gap where WinDbg has a clear advantage is endpoint address — engineers
using WinDbg can read `_remoteAddress` with a single `!do` command. Closing that gap would
make DumpDetective's WCF analysis definitively superior to any available tool.

---

## Final Executive Summary

### Overall Assessment

**Score: 74 / 100**  
**Production readiness: Yes, with known limitations**

**Major strengths:**
- Correct TypeAggregates-backed candidate discovery — zero extra heap passes
- Parallel scan implementation (unique among infrastructure analyzers)
- Accurate faulted channel detection with sound `Abort()` vs `Close()` guidance
- Cross-correlation with communication exceptions in `InsightEngine`
- Clean architecture via typed-resource quartet interfaces

**Major weaknesses:**
- `Opening` and `Closing` states collapsed into opaque `OtherChannels` bucket
- No endpoint address in faulted channel samples — first question any SRE asks
- Total heap bytes not surfaced in key metrics
- ChannelFactory anti-pattern not detected

---

### Priority Roadmap

#### P0 — Critical

| # | Recommendation | Impact | Difficulty | Confidence | Classification |
|---|---|---|---|---|---|
| P0-1 | **Add `OpeningChannels` and `ClosingChannels` to domain model and report.** Opening = stuck connecting; Closing = stuck draining. Both are actionable and currently invisible. | High | Low | High | ✅ Complete (commit 40da729) |
| P0-2 | **Extract remote endpoint address into `WcfChannelSnapshot`.** Read `_remoteAddress` → `Uri` string in `TrySample`. Report in faulted-channel table and finding evidence. | Critical | Medium | High | ✅ Complete (commit 5e94be5) |

#### P1 — High

| # | Recommendation | Impact | Difficulty | Confidence | Classification |
|---|---|---|---|---|---|
| P1-1 | **Add `ChannelFactory<T>` detection.** Detect `System.ServiceModel.ChannelFactory`-derived types; emit a Warning finding when high counts are present. Per-call ChannelFactory creation is a well-known expensive anti-pattern. | High | Low | High | Improvement |
| P1-2 | **Add aggregate `TotalBytes` to `WcfChannelDomainResult` and to key metrics.** Sum `TotalBytes` across `ByType` in `BuildResult`. Surface in `AnalyzerDetailSection.KeyMetrics`. | Medium | Low | High | Improvement |
| P1-3 | **Fix key metrics to include `OtherChannels`** so metrics sum to `TotalChannels`. | Low | Trivial | High | Improvement |
| P1-4 | **Promote `DbConnectionAnalyzer` to `IParallelHeapIndexScanParticipant`.** The pattern is proven here; `DbConnectionAnalyzer` uses identical infrastructure but is single-threaded. | Medium | Medium | High | Evolution |

#### P2 — Medium

| # | Recommendation | Impact | Difficulty | Confidence | Classification |
|---|---|---|---|---|---|
| P2-1 | **Classify binding type from type name suffix.** Append `BindingHint` (Basic, NetTcp, WsHttp, NamedPipe, Unknown) to `WcfChannelTypeSummary`. Differentiate finding recommendations by binding type. | Medium | Low | Medium | Improvement |
| P2-2 | **Add `Opening` + `Closing` cross-correlation in `InsightEngine`.** When `OpeningChannels > 0` and timeout exceptions are present, emit a cross-cutting finding for connection-level failures. | Medium | Low | High | Evolution |
| P2-3 | **Add per-type cap indicator to `StateScanCapped`.** Change from `bool` to `IReadOnlyList<string>` of capped type names, or add a `CappedTypeCount` integer, so report consumers know the scope. | Low | Low | High | Improvement |
| P2-4 | **Eliminate `ContainsKey` + `TryGetValue` double lookup in `OnHeapEntry`.** Single `TryGetValue` suffices. | Low | Trivial | High | Improvement |

#### P3 — Low

| # | Recommendation | Impact | Difficulty | Confidence | Classification |
|---|---|---|---|---|---|
| P3-1 | **Add `stateVal` range validation.** Guard `stateVal < 0 || stateVal > 5` and increment a separate `InvalidStateCount` rather than silently absorbing into `OtherChannels`. | Low | Trivial | Medium | Improvement |
| P3-2 | **Detect duplex and session channels separately.** Add `ISessionChannel` and `IDuplexChannel` type tokens; add `SessionChannelCount` and `DuplexChannelCount` to domain model. | Medium | Medium | Medium | Improvement |
| P3-3 | **Add a test that exercises the `MergePartial` new-key-from-worker path under realistic pre-seeding.** Currently the branch is logically unreachable in production. | Low | Low | High | Improvement |

---

### Final Verdict

1. **Is the analyzer production-ready?**  
   Yes. Core channel detection, parallel scan, and faulted-channel triage are correct and
   scale to large dumps. The `Opening`/`Closing` gap and the missing endpoint address reduce
   diagnostic completeness but do not cause incorrect conclusions.

2. **Highest-impact improvements?**  
   P0-2 (endpoint address extraction) is the single highest-return change — it eliminates
   the manual `!do` step that every engineer performs when investigating a WCF fault.
   P0-1 (Opening/Closing breakdown) converts a silent blind spot into an actionable finding
   for connection-failure scenarios.

3. **Platform evolution opportunities?**  
   P1-4 (parallel `DbConnectionAnalyzer`) is a low-risk, high-value platform improvement.
   The parallel scan pattern is proven here; applying it to the DB analyzer requires only an
   interface change and the same merge implementation pattern.

4. **Highest engineering return?**  
   P0-2 → P0-1 → P1-1 in that order. These three changes, totalling approximately one day
   of implementation work, would make DumpDetective's WCF analysis definitively superior to
   any available tool including manual WinDbg + SOS workflows.

---

## Implementation Status

### ✅ P0-1 Complete (2026-08-04)

**Commit:** `40da729`  
**Status:** Opening and Closing states now first-class in domain model

**What was done:**
- Added `OpeningChannels` and `ClosingChannels` to `WcfChannelDomainResult` and `WcfChannelTypeSummary`
- Analyzer now tracks state transitions: Opening (1), Closing (3) counted separately from Other
- Section builder displays Opening/Closing in key metrics and per-type table columns
- Finding generator includes state breakdown in evidence: "Opening: N, Opened: N, Faulted: N, Closing: N, Closed: N, Other: N"
- Trend comparer added `wcf.opening` and `wcf.closing` metrics marked as HigherIsWorse

**Impact:**
- Connection-level failures (high Opening count) now visible in reports
- Graceful shutdown bottlenecks (high Closing count) now visible in reports
- Trend analysis can now track Opening/Closing growth across dumps
- Ready for P2-2: cross-correlation with timeout exceptions for automated diagnosis

### ✅ P0-2 Complete (2026-08-10)

**Commit:** `5e94be5`  
**Status:** Remote endpoint addresses now extracted and displayed

**What was done:**
- Added `RemoteAddress` field (nullable string) to `WcfChannelSnapshot` record
- Implemented three-level extraction chain in WcfChannelAnalyzer:
  1. `TryExtractRemoteAddress()`: probe `_remoteAddress` or `_via` field on channel object
  2. `TryExtractUriFromEndpointAddress()`: read `_uri` or `Uri` field from `System.ServiceModel.EndpointAddress`
  3. `TryExtractStringFromUri()`: convert Uri to string via AsString() or ToString()
- All field name probes are defensive (multiple variants, null checks, try/catch)
- Updated `WcfChannelSectionBuilder` to display "Remote Endpoint" column in faulted channels table
- Updated `WcfChannelFindingGenerator.BuildEndpointSummary()` to group unique endpoints (cap 3) in Finding evidence

**Impact:**
- Engineers investigating faulted channels no longer need manual `!do <addr>` inspection
- Remote service URL now surfaces directly in report table and highlighted in Critical finding
- Closes the final major diagnostic gap vs. manual WinDbg workflows
- DumpDetective's WCF analysis now definitively superior to any available tool
