# Phase 0 — Deliverable 2 Addendum: New-Analyzer vs. Extend-Existing Verdicts

> Scope: takes the "Summary of Gaps" section of
> [phase0-deliverable-2-capability-matrix.md](phase0-deliverable-2-capability-matrix.md) and, for
> each missing/partial capability, answers the question the matrix itself doesn't: **should this
> be a brand-new `IAnalyzer`, or a capability bolted onto an existing one?** Cross-checked against
> the [Deliverable 10 roadmap](phase0-deliverable-10-platform-roadmap.md) (so priority ordering
> isn't re-litigated here) and the `docs/analysis/phase1/*-audit.md` per-analyzer audits, which
> surfaced one correction to the capability matrix itself (see below).

## Decision principle

Default to **extend existing** when the gap shares an object model, a scan pass, or a domain
result with an analyzer that already exists — a new analyzer multiplies the registration fan-out
flagged in Deliverable 7/10 (domain result, finding generator, trend comparer, section builder,
catalog entry) for every capability added. Reserve **new analyzer** for gaps with a genuinely
distinct object model (a new set of CLR types to walk, a new heap-scan shape) where bolting it onto
an existing analyzer would itself become the next scope-creep flag (as already happened to
`CollectionAnalyzer`, per Deliverable 1/6).

## Correction to the capability matrix

**Pinned object / POH reporting is not "Missing."** The capability matrix
([Memory table](phase0-deliverable-2-capability-matrix.md#memory), row "POH") marks this `No`.
[docs/analysis/phase1/heap-topology-analyzer-audit.md](../phase1/heap-topology-analyzer-audit.md)
confirms `HeapTopologyAnalyzer` already classifies POH/Frozen segments, tracks committed/reserved/
used bytes per kind, and produces a POH type breakdown. The real residual gap is narrower than the
matrix states: cross-referencing pinned-handle count against POH segment occupancy (already flagged
as an expansion opportunity in that audit) — not POH visibility itself. The capability matrix's
Memory > POH row and GC > Pinned objects row should be merged/reworded to reflect this.

## Verdict table

| Gap (from Deliverable 2) | Verdict | Target | Why |
|---|---|---|---|
| DI scoped-service leak detection | **New analyzer** | `DiScopeLeakAnalyzer` | `IServiceProvider`/`ServiceProviderEngineScope` internals are a distinct object model unrelated to any existing analyzer's scan shape. Highest-value gap; per Deliverable 10 P2 item 1, sequence after the ranking/evidence engine and shared type-classification layer so it reports through the same evidence model instead of becoming a 7th independently-scored leak source. |
| Runtime configuration / GC mode reporting | **Extend** `ModuleAnalyzer` | — | No heap scan needed — pure `ClrRuntime`/`DacInfo`/env-var metadata, the same "dump/process metadata" flavor `ModuleAnalyzer` already owns per the matrix's Dump & Process Metadata table. A standalone analyzer for a few scalar fields would just add to the 4x registration fan-out (Deliverable 7/10 P2 item 6) for no scan-cost benefit. |
| EF Core–aware analysis (DbContext, change tracker, compiled-query cache) | **New analyzer** | `EfCoreAnalyzer`, built on `TypedResourceSampler`/`ITypedResourceCandidateSource` | `DbContext`/change-tracker internals are a distinct object model from `DbConnectionAnalyzer`'s raw ADO.NET connections — real risk of repeating `CollectionAnalyzer`'s scope-creep if bolted on. Should reuse the P1-item-7/8 typed-resource-sampler contracts as infrastructure rather than duplicating candidate-discovery logic. |
| Cache health (`IMemoryCache`/static caches) | **New analyzer**, reusing `StaticRootLeakDetector`'s static-field sweep | `CacheHealthAnalyzer` | `IMemoryCache`'s internal entry store needs cache-specific typed sampling (entry count/size, expired-but-unevicted entries) that doesn't fit `CollectionAnalyzer`'s generic waste detection. But reachability (is this cache instance rooted via a static or DI singleton) should reuse the static-field sweep already built for `StaticRootLeakDetector`/`EventLeakAnalyzer` rather than a third independent sweep — Deliverable 10 already flags the static-field-sweep pair as a duplication cluster; a third copy would make it a trio. |
| Native/unmanaged memory + COM interop (RCW/CCW) | **New analyzer** | `NativeInteropAnalyzer` | RCW/CCW enumeration and native committed-memory totals are a distinct ClrMD API surface (sync-block/COM-wrapper data) with no existing owner. Independent, no blocking dependency (Deliverable 10 P2 item 3). |
| AssemblyLoadContext leak detection | **Extend** `ModuleAnalyzer` | — | Same data domain `ModuleAnalyzer` already owns post-`AppDomainAnalyzer`-merge (assemblies, load contexts); the gap is a reachability check (is an unloaded ALC's assemblies still rooted) layered on data already collected, not a new scan. |
| Pinned object / POH reporting | **Extend** `HeapTopologyAnalyzer` | — | See correction above — POH is covered; the residual (pinned-handle-to-POH-segment cross-reference) is explicitly scoped as low-effort in the heap-topology audit and only needs a join against `GCHandleAnalyzer`'s already-collected pinned-handle list, not a new scan. |
| ASP.NET diagnostics (HttpContext, Kestrel, SignalR) | **New analyzer** (P3, low priority) | `AspNetDiagnosticsAnalyzer` | Distinct object model from `HttpObjectAnalyzer` (client-side `HttpMessageHandler`s) — server-side request/connection state is unrelated. Novel engineering, correctly deferred per Deliverable 10 P3. |
| Object ownership (who allocated it) | **Non-goal, no analyzer** | — | Not derivable from a static snapshot — no allocation-context data exists in a dump. Matrix already flags this as an inherent limitation, not a gap to close. |
| Duplicate objects (non-string, value-equal instances) | **New analyzer** | `DuplicateObjectAnalyzer`, modeled on `StringAnalyzer`'s hash-based dedup | Generalizing `StringAnalyzer`'s duplicate-detection approach to arbitrary value-type/record field hashing is a different enough scan shape (structural field-value hashing vs. string interning) to warrant its own analyzer rather than bloating `StringAnalyzer`. |
| Minidump exception-stream parsing | **Extend** `CrashAnalyzer` | — | Already the plan per [p1-item-11-minidump-exception-stream-investigation.md](p1-item-11-minidump-exception-stream-investigation.md) and Deliverable 10 P1 item 11 — same `CrashDomainResult`, additional DBGHELP-sourced data feeding it. Not a new analyzer; deferred on engineering cost (DBGHELP P/Invoke), not on ownership. |
| Resurrection detection | **Extend** `FinalizableObjectAnalyzer` | — | Same finalizer-domain object model `FinalizableObjectAnalyzer` already walks; resurrection is a specific pattern within that walk (object re-reachable after finalization), not a new heap-scan shape. |
| Native (non-managed) OS thread enumeration | **Extend** `ThreadAnalyzer` | — | Same "threads" domain and can reuse the `IThreadStackScanParticipant`/`ThreadStackScanDispatcher` infra (Deliverable 10 P1 item 8) as the natural place to add a native-thread section, rather than a parallel analyzer duplicating thread enumeration. |
| `System.Threading.Channels` support | **Extend** `AsyncTaskAnalyzer` | — | Channels are TPL/async infrastructure, the same domain `AsyncTaskAnalyzer` already owns; classification can reuse the shared `TypeNamePatternMatcher`/`TypedResourceSampler` infra the same way `TimerLeakAnalyzer` does, rather than becoming a standalone analyzer for one BCL type family. |

## Partials worth closing via extension (not in the matrix's "entirely missing" list, but flagged as Future Candidates)

| Capability | Verdict | Target |
|---|---|---|
| Proven-cycle deadlock detection (currently candidate-only) | **Extend** `LockGraphAnalyzer` | Strengthen the existing cycle-candidate algorithm to a proven cycle; same data, no new scan. |
| Dedicated ThreadPool-health trend view | **Extend** `HangAnalyzer` | Already the ThreadPool-health owner (Partial); needs a trend comparer/section addition, not a new analyzer. |
| SOH fragmentation (only LOH is covered today) | **Extend** `HeapTopologyAnalyzer` | The `heap-topology-analyzer-audit.md` finding is explicit: per-kind fragmentation (committed − used, including SOH) is "already computed; not split" — a low-effort exposure of existing data. |
| Free-object / free-list space ratio | **Extend** `HeapTopologyAnalyzer` or `SegmentReservationAnalyzer` | Same per-segment data both analyzers already read; needs a free-object classification pass, not a new scan. |

## Net recommendation

Of the 11 "entirely missing" capabilities, **4 justify a genuinely new `IAnalyzer`** with no
reasonable existing owner: DI scoped-service leaks, EF Core awareness, cache health, and
native/COM interop. A 5th (duplicate-object detection) and 6th (ASP.NET diagnostics) are new but
lower-priority/deferred. The remaining 5 — runtime config, ALC leaks, POH/pinned cross-reference,
minidump exception-stream, resurrection, native-thread enumeration, and Channels — are all
extensions of an existing analyzer's already-established object model and scan pass, and adding
them as standalone analyzers would only worsen the registration-fan-out problem Deliverable 7/10
already flags as an open architectural risk.

## Feasibility, complexity, effort — new analyzers

The four verdicts above split sharply on feasibility once you ask "does ClrMD actually give us a
stable way to read this structure across .NET versions," which matters more than raw scan cost for
a new analyzer's real effort.

| Analyzer | Feasibility | Complexity | Effort | Key risk |
|---|---|---|---|---|
| `CacheHealthAnalyzer` (`IMemoryCache`) | **High** | Medium | **S–M** (~1–1.5 wk) | `MemoryCache`'s `CoherentState._entries` layout has been stable since .NET Core 3.1; reuses the existing static-field sweep, so most of the effort is the cache-entry typed sampler, not reachability. |
| `DuplicateObjectAnalyzer` (non-string value equality) | **High** | Medium | **S–M** (~1–1.5 wk) | Pure heap-scan + structural hashing, no internal-BCL-type coupling at all. Main design cost is bounding memory for the hash-bucket index (reservoir/streaming, per the no-`ToList()` rule), not ClrMD API risk. |
| `DiScopeLeakAnalyzer` (`IServiceProvider` scopes) | **Medium** | High | **L** (~3–4 wk) | `ServiceProviderEngineScope`/`ServiceProviderEngine` are `internal` DI-container types with no compatibility guarantee; field layout has already shifted across .NET 5→9 concrete DI implementations (`DynamicServiceProviderEngine`, `RuntimeServiceProviderEngine`, etc.). Needs a per-major-version field-offset matrix and will need re-validation on every new .NET release — an ongoing maintenance cost, not just a one-time build cost. |
| `EfCoreAnalyzer` (`DbContext`/change tracker) | **Medium** | High | **L** (~3–4 wk) | Same version-drift problem as DI scopes: `ChangeTracker`'s internal `IStateManager`/`InternalEntityEntry` types have changed shape across EF Core 3–9. Can amortize some cost by reusing the `TypedResourceSampler` infra built for `DbConnectionAnalyzer`, but the type-layout research is EF-specific and non-transferable. |
| `NativeInteropAnalyzer` (RCW/CCW, native COM) | **Low–Medium** | High | **L–XL** (~4–6 wk incl. spike) | ClrMD's public API surface for RCW/CCW enumeration is thin (no first-class "list all CCWs" call comparable to `ClrHeap.EnumerateObjects`); likely needs raw sync-block-table parsing or DAC calls ClrMD doesn't wrap cleanly. Needs a feasibility spike *before* committing effort — this is the one candidate where "can we build this at all with acceptable reliability" isn't yet answered, unlike the other three. |
| `AspNetDiagnosticsAnalyzer` (HttpContext/Kestrel/SignalR) | **Low** | High | **XL** (~6+ wk) | Server-side per-request state is largely ephemeral and version-coupled to Kestrel/SignalR internals; even if built, most dumps will show few live requests at capture time, capping the value relative to the DI/EF-Core-sized effort. Confirms the existing P3/deferred call — low value-to-effort ratio, not just low priority. |

## Feasibility, complexity, effort — extensions to existing analyzers

Extensions are cheap almost by definition (no new registration, reuse of an existing scan pass),
but they aren't uniformly trivial — a few have real feasibility questions of their own.

| Extension | Feasibility | Effort | Note |
|---|---|---|---|
| SOH fragmentation → `HeapTopologyAnalyzer` | High | **XS** (days) | Data already computed per the audit; this is a section/finding-generator change only. Cheapest item on this whole page. |
| POH/pinned-handle cross-reference → `HeapTopologyAnalyzer` | High | **XS–S** (days) | Join against `GCHandleAnalyzer`'s existing pinned-handle list; no new scan. |
| Runtime config/GC mode → `ModuleAnalyzer` | High | **XS–S** (days) | Scalar `ClrRuntime`/env-var reads, no heap scan. |
| ThreadPool-health trend → `HangAnalyzer` | High | **S** | Trend comparer + section addition on data already collected. |
| Free-object/free-list ratio → `HeapTopologyAnalyzer`/`SegmentReservationAnalyzer` | High | **S** | Needs a free-object classification pass over already-read segment data. |
| Channels → `AsyncTaskAnalyzer` | High | **S** | Reuses `TimerLeakAnalyzer`'s typed-resource-classification pattern for one more BCL type family. |
| Proven-cycle deadlock → `LockGraphAnalyzer` | Medium | **S–M** | Algorithmic tightening (candidate → proven cycle) on existing lock-graph data; correctness risk (false negatives on partial dumps) is the main design cost, not new scanning. |
| ALC leak detection → `ModuleAnalyzer` | Medium | **S–M** | Data (assemblies/load contexts) already collected; the reachability check needs care to avoid false positives on ALCs that are unloaded-but-still-legitimately-rooted (e.g., static caches keyed by type). |
| Resurrection detection → `FinalizableObjectAnalyzer` | Medium | **S–M** | Needs to distinguish "still in finalizer queue" from "resurrected" from generation/finalizer-queue state, which isn't always unambiguous from a single snapshot. |
| Native OS thread enumeration → `ThreadAnalyzer` | Medium | **S–M** | Depends on whether the dump type (full vs. minidump) retained native thread context; feasibility is dump-format-dependent, not just engineering effort. |
| Minidump exception-stream → `CrashAnalyzer` | Medium | **L** | Already flagged as deferred on engineering cost (DBGHELP P/Invoke) per Deliverable 10 P1 item 11 — this is the one "extend" item that's actually as expensive as a new analyzer. |

## Priority-ordered implementation plan

**Now (quick wins — implement first, days each, no version-drift risk):**
1. SOH fragmentation → `HeapTopologyAnalyzer`
2. POH/pinned-handle cross-reference → `HeapTopologyAnalyzer`
3. Runtime configuration/GC mode → `ModuleAnalyzer`
4. Channels → `AsyncTaskAnalyzer`
5. ThreadPool-health trend → `HangAnalyzer`

These close 5 matrix gaps essentially for the cost of one sprint, with zero new registration
overhead and no dependency on the ranking/evidence-engine work called out in Deliverable 10.

**Next (S–M effort, medium feasibility, worth doing before the big new analyzers):**
6. Free-object/free-list ratio → `HeapTopologyAnalyzer`/`SegmentReservationAnalyzer`
7. ALC leak detection → `ModuleAnalyzer`
8. Resurrection detection → `FinalizableObjectAnalyzer`
9. Proven-cycle deadlock → `LockGraphAnalyzer`
10. `CacheHealthAnalyzer` (new, but high feasibility and reuses existing static-sweep infra)
11. `DuplicateObjectAnalyzer` (new, but no BCL-internal coupling at all)

**Later (large effort, real value, but version-drift or spike risk — sequence deliberately, one at
a time, after the ranking/evidence-engine and shared type-classification layer from Deliverable
10 land so they don't each become independent scoring systems):**
12. `DiScopeLeakAnalyzer` — highest leak-detection value of the deferred set; do this before EF Core.
13. `EfCoreAnalyzer` — same version-drift shape as #12; benefits from whatever tooling/process #12
    establishes for tracking internal-type layout across .NET releases.
14. Native OS thread enumeration → `ThreadAnalyzer` — gate on confirming dump-format support first.

**Deferred indefinitely / spike-gated (don't schedule effort until feasibility is confirmed or
value case improves):**
15. `NativeInteropAnalyzer` — run a short (1-week) feasibility spike against ClrMD's actual RCW/CCW
    API surface before committing to the full L–XL build; do not schedule the full build yet.
16. Minidump exception-stream → `CrashAnalyzer` — already deferred on DBGHELP P/Invoke cost;
    revisit only if a customer dump makes exception-stream data a blocking need.
17. `AspNetDiagnosticsAnalyzer` — lowest value-to-effort ratio on this page; revisit only if
    demand materializes for live-request-state analysis specifically (most dumps won't benefit).
