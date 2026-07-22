# Phase 0 — Deliverable 2: Capability Matrix

> Scope: **Deliverable 2 only** from
> [phase0-cross-analyzer-architecture-review.md](phase0-cross-analyzer-architecture-review.md).
> Per the doc's instructions, the analyzer catalog is deliberately **ignored** while compiling this
> list — capabilities are derived from what a production-grade .NET dump analysis platform should
> offer (comparable to WinDbg/SOS, PerfView, VS Memory Usage, dotMemory), not from what
> DumpDetective already has. Analyzer ownership is then mapped back in using the results of
> [Deliverable 1](phase0-deliverable-1-analyzer-catalog.md), for reference only.

## Legend

- **Covered** — Yes / Partial / No
- **Quality** — Excellent / Good / Partial / Missing
- **Overlap** — Yes (multiple analyzers/None) — names the analyzers if Yes
- **Future candidate** — flags gaps worth prioritizing in Deliverable 10

Two categories (**Native / Interop**, **Dump & Process Metadata**) are added beyond the doc's
suggested list — both are standard expectations for a production dump analyzer (WinDbg `!dumpheap`,
dotMemory's native/COM tracking) and surfaced clear gaps.

---

## Memory

| Capability | Covered | Owning Analyzer(s) | Quality | Overlap | Future Candidate |
|---|---|---|---|---|---|
| Heap summary | Yes | `MemoryAnalyzer` | Excellent | None | — |
| Type statistics | Yes | `MemoryAnalyzer`, `ObjectShapeAnalyzer` | Good | Partial (both touch type-level stats) | — |
| Object statistics | Yes | `MemoryAnalyzer` | Good | None | — |
| Object ownership (who allocated/holds it) | No | — | Missing | — | Yes — requires allocation-context tracking not generally available from a static snapshot |
| Duplicate objects (non-string) | No | — | Missing | — | Yes — value-equal duplicate instance detection (common in dotMemory/dotTrace) |
| Strings (duplication/waste) | Yes | `StringAnalyzer` | Excellent | None | — |
| LOH | Yes | `LohFragmentationAnalyzer` | Good | Partial (`AllocationPatternAnalyzer` also classifies LOH-heavy profiles) | — |
| POH (Pinned Object Heap) | No | — | Missing | — | Yes — no analyzer distinguishes POH from SOH/LOH |
| SOH | Partial | `GCGenerationAnalyzer`, `MemoryAnalyzer` | Partial | None | Dedicated SOH fragmentation view |
| Fragmentation (general) | Partial | `LohFragmentationAnalyzer` (LOH only), `SegmentReservationAnalyzer` (VM waste) | Partial | None | SOH fragmentation is not covered at all |
| Free objects / free-list space | No | — | Missing | — | Yes — ClrMD exposes free-space "objects"; no analyzer surfaces free-space ratio distinctly from segment reservation |
| GC generations | Yes | `GCGenerationAnalyzer` | Excellent | None | — |

## Retention

| Capability | Covered | Owning Analyzer(s) | Quality | Overlap | Future Candidate |
|---|---|---|---|---|---|
| Root analysis | Yes | `GCRootAnalyzer` | Good | None | — |
| Root categorization | Yes | `GCRootAnalyzer` | Good | None | — |
| Reference chains | Yes | `ReferenceChainAnalyzer` | Good | On-demand only, not part of batch report | — |
| Dominators | Yes | `DominatorAnalyzer` | Partial | None — `RetentionAnalyzer` merged in | Verify true dominator-tree vs. approximation in Deliverable 3 |
| Retention graphs (exportable/visualizable) | Partial | Scattered: `DominatorAnalyzer`, `StaticRootLeakDetector`, `EventLeakAnalyzer` | Partial | Yes — 3 analyzers each compute a partial retention view | Unify into a single retention/evidence service (Deliverable 5) |
| Largest retainers | Yes | `DominatorAnalyzer` | Good | None — was duplicated with `RetentionAnalyzer`, now merged | — |
| Object ownership graph | No | — | Missing | — | Same gap as Memory > Object ownership |

## GC

| Capability | Covered | Owning Analyzer(s) | Quality | Overlap | Future Candidate |
|---|---|---|---|---|---|
| Handles (general) | Yes | `GCHandleAnalyzer` (includes former `DependentHandleAnalyzer`) | Excellent | Partial (`WeakReferenceAnalyzer` covers a subset) | — |
| Finalizer queue (objects pending finalization) | Partial | `FinalizableObjectAnalyzer` | Partial | None | Confirm in Deliverable 3 whether the F-reachable/finalization queue itself (vs. "has a finalizer, not disposed") is actually enumerated |
| Pinned objects | Partial | `GCHandleAnalyzer` (pinned handle kind only) | Partial | None | Dedicated pinning report (GCHandle.Alloc(Pinned), interop `fixed`, buffer pinning) — a common GC-fragmentation root cause |
| Weak references | Yes | `WeakReferenceAnalyzer`, `GCHandleAnalyzer` (includes former `DependentHandleAnalyzer`) | Good, still fragmented | Yes — 2 analyzers, unclear boundary (flagged in Deliverable 1) | Consolidate ownership |
| Resurrection | No | — | Missing | — | Yes — no analyzer detects finalizer-resurrection patterns |
| Finalizable objects | Yes | `FinalizableObjectAnalyzer` | Good | None | — |

## Threads

| Capability | Covered | Owning Analyzer(s) | Quality | Overlap | Future Candidate |
|---|---|---|---|---|---|
| Managed threads | Yes | `ThreadAnalyzer` | Excellent | Partial (`HangAnalyzer` duplicates wait-pattern logic) | — |
| Native (non-managed) OS threads | No | — | Missing | — | Yes — ClrMD/DataReader can enumerate native OS threads independent of `ClrThread`; not surfaced today |
| Deadlocks | Partial | `LockGraphAnalyzer` | Good (candidate detection, not proven cycles) | None | Strengthen to a proven-cycle deadlock detector |
| Blocking | Yes | `HangAnalyzer`, `LockGraphAnalyzer` | Good | Yes — both analyze blocking threads independently | Consolidate stack-walk pass (Deliverable 4) |
| ThreadPool health | Partial | `HangAnalyzer` | Partial | None | Dedicated ThreadPool trend view (worker/IO counts, queue length over time) |
| Async state machines | Yes | `AsyncStateMachineAnalyzer` | Good | Partial (`AsyncTaskAnalyzer` overlaps on task/continuation state) | — |

## Exceptions

| Capability | Covered | Owning Analyzer(s) | Quality | Overlap | Future Candidate |
|---|---|---|---|---|---|
| Active exceptions (heap-resident) | Yes | `CrashAnalyzer` | Good | None | — |
| Historical crash evidence (why the process actually died) | Partial / Unclear | `CrashAnalyzer` | Partial | None | Verify in Deliverable 3 whether the minidump exception stream (faulting thread, SEH/AV record) is consumed, or only heap-scanned exception objects — these are architecturally different data sources |
| Exception pressure | Yes | `CrashAnalyzer` | Good | None | — |
| Aggregate/grouped exceptions | Yes | `CrashAnalyzer` | Good | None | — |

## Collections

| Capability | Covered | Owning Analyzer(s) | Quality | Overlap | Future Candidate |
|---|---|---|---|---|---|
| Dictionaries / Lists / Queues / HashSets | Yes | `CollectionAnalyzer` | Good | None | — |
| Concurrent collections | Yes | `CollectionAnalyzer` | Good (assumed — same monolith) | None | Verify coverage depth in Deliverable 3 |
| Immutable collections | Partial | `CollectionAnalyzer` | Partial (assumed — same monolith) | None | Verify coverage depth in Deliverable 3 |

*(All three capabilities are covered by one 1700+-line analyzer — see the scope-creep flag in
Deliverable 1. A production platform would likely want these split rather than consolidating
further.)*

## Framework-specific

| Capability | Covered | Owning Analyzer(s) | Quality | Overlap | Future Candidate |
|---|---|---|---|---|---|
| ASP.NET (HttpContext, Kestrel, SignalR) | No | — | Missing | — | Yes — high value for web-app dumps, currently absent entirely |
| WCF | Partial | `WcfChannelAnalyzer` | Partial (channel state only) | Duplicate infra shape with DbConnection/Http/Timer (Deliverable 1) | — |
| EF Core | No | — | Missing | — | Yes — `DbConnectionAnalyzer` sees raw ADO.NET connections but has no DbContext/change-tracker/compiled-query-cache awareness; EF Core leaks (untracked change tracker growth) are a very common real-world case |
| HttpClient | Partial | `HttpObjectAnalyzer` | Partial | Duplicate infra shape | — |
| Timers | Yes | `TimerLeakAnalyzer` | Good | Duplicate infra shape | — |
| Tasks | Yes | `AsyncTaskAnalyzer` | Good | Partial (`HangAnalyzer` continuation counting) | — |
| Channels (`System.Threading.Channels`) | No | — | Missing | — | Yes |
| Events | Yes | `EventLeakAnalyzer` | Good | Overlaps `StaticRootLeakDetector` static sweep | — |
| Dependency Injection (DI container leaks) | No | — | Missing | — | Yes — captured-scoped-service leaks via `IServiceProvider`/`ServiceProviderEngineScope` are one of the most common .NET leak patterns and are entirely absent |
| Reflection (cached MethodInfo/Type growth, dynamic assemblies) | No | — | Missing | — | Yes |
| Assembly / AssemblyLoadContext loading | Partial | `ModuleAnalyzer`, `AppDomainAnalyzer` | Partial | Yes — both overlap on module/type enumeration | Add explicit unloadable-`AssemblyLoadContext` leak detection (very common .NET Core leak pattern), not just static module listing |

## Platform Health

| Capability | Covered | Owning Analyzer(s) | Quality | Overlap | Future Candidate |
|---|---|---|---|---|---|
| Memory pressure | Yes | `MemoryAnalyzer`, `AllocationPatternAnalyzer` | Good | None | — |
| Allocation hotspots | Partial | `AllocationPatternAnalyzer` | Partial | None | Inherent snapshot limitation — true call-site hotspots need ETW, not a dump; document this boundary explicitly rather than treating it as a gap to close |
| Cache health (`IMemoryCache`, static caches, etc.) | No | — | Missing | — | Yes — distinct from `CollectionAnalyzer`'s generic waste detection; common leak/bloat source |
| Leak indicators (unified) | Partial | `DominatorAnalyzer`, `LeakCandidateAnalyzer`, `StaticRootLeakDetector`, `EventLeakAnalyzer`, `TimerLeakAnalyzer` | Good individually, poor in aggregate | Yes — 5 analyzers, no unified scoring (Deliverable 1 flag) | Shared confidence-scoring engine (Deliverable 5) |
| Runtime configuration (GC mode, heap count, TieredCompilation, env vars) | No | — | Missing | — | Yes — cheap to surface directly from `ClrRuntime`/`DacInfo`, high diagnostic value, currently not reported anywhere |

## Native / Interop *(added — not in the doc's suggested list)*

| Capability | Covered | Owning Analyzer(s) | Quality | Overlap | Future Candidate |
|---|---|---|---|---|---|
| Native/unmanaged memory usage | No | — | Missing | — | Yes — total committed vs. managed-heap bytes is standard in WinDbg/dotMemory triage |
| COM interop (RCW/CCW) leaks | No | — | Missing | — | Yes — classic COM-interop leak source (RCWs keeping native objects alive, or vice versa), not covered by any analyzer |
| Native heaps (per-segment native allocator stats) | No | — | Missing | — | Lower priority — usually needs `!heap`-equivalent native debugger support beyond ClrMD's scope |

## Dump & Process Metadata *(added — not in the doc's suggested list)*

| Capability | Covered | Owning Analyzer(s) | Quality | Overlap | Future Candidate |
|---|---|---|---|---|---|
| Process/module version info, OS, architecture, uptime | Partial | `ModuleAnalyzer` (modules only) | Partial | None | Dedicated dump-metadata section (PID, command line, OS build, CLR version, dump timestamp) — usually the first thing a triager wants and currently only partially surfaced via module info |
| Loaded modules & version conflicts | Yes | `ModuleAnalyzer` | Good | Overlaps `AppDomainAnalyzer` | — |
| Environment variables / runtime config knobs | No | — | Missing | — | Same as Platform Health > Runtime configuration |

---

## Summary of Gaps (feeds Deliverable 10)

**Entirely missing capabilities**, roughly in priority order for a production diagnostics platform:

1. Dependency Injection scoped-service leak detection
2. Runtime configuration / GC mode reporting (cheap, high value)
3. EF Core–aware analysis (change tracker, DbContext lifetime)
4. Cache health (`IMemoryCache`/static cache bloat)
5. Native/unmanaged memory and COM interop (RCW/CCW) tracking
6. AssemblyLoadContext leak detection (vs. today's static module listing)
7. Pinned object / POH reporting
8. ASP.NET-specific diagnostics
9. Object ownership / duplicate-object (non-string) detection
10. Resurrection detection, native thread enumeration, `System.Threading.Channels`

**Capabilities that are covered but fragmented across too many analyzers** (candidates for
consolidation rather than net-new work — see Deliverable 1 findings and Deliverable 5):

- Leak indicators (5 analyzers)
- Weak/dependent handle coverage (2 analyzers, was 3 before `DependentHandleAnalyzer` merged into `GCHandleAnalyzer`)
- Thread blocking/wait-pattern analysis (`HangAnalyzer` + `ThreadAnalyzer` + `LockGraphAnalyzer`)
- Module/assembly inventory (`ModuleAnalyzer` + `AppDomainAnalyzer`)
