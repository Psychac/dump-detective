# NativeInteropAnalyzer — Design Sketch

> Priority: **P2 item 3** — independent net-new capability with no blocking dependency.
> Ships after DI and EF/Cache because it requires a feasibility spike: ClrMD's public API
> surface for RCW/CCW enumeration is thin, and the path to reliable COM-wrapper data needs
> empirical validation against a real dump before committing implementation effort.
>
> Feasibility: **Low–Medium**. No first-class "enumerate all CCWs" ClrMD API exists comparable
> to `ClrHeap.EnumerateObjects()`. Likely requires raw sync-block-table parsing or undocumented
> DAC calls. The spike must answer "can we do this reliably?" before full implementation starts.
>
> Effort: **L–XL** (~4–6 wk including spike, ~2 wk if spike confirms a clean path).

---

## 1. Problem statement

.NET applications that interop with native code via COM or P/Invoke retain unmanaged memory and
COM wrapper objects (Runtime-Callable Wrappers and COM-Callable Wrappers) that are invisible to
purely managed-heap analysis. Two distinct failure modes:

1. **RCW (Runtime-Callable Wrapper) accumulation** — each `Marshal.GetObjectForIUnknown` or COM-
   interop activation creates an RCW. If RCWs are not released (via `Marshal.ReleaseComObject`
   or disposal) and GC does not collect their wrappers, the native COM objects they hold live
   beyond their useful life. This is a native-memory leak driven by managed-side retention.

2. **CCW (COM-Callable Wrapper) leaks** — when a managed object is passed to native code as a COM
   object, the runtime creates a CCW holding a reference count. If the native caller does not
   `Release()` all its references, the CCW keeps the managed object alive even after all managed
   references are gone. This prevents GC collection of the managed object.

3. **Native committed memory** — the process's total native committed memory (non-GC heap
   committed pages) is not reported by any existing analyzer. Knowing that a process has 4 GB
   of native committed memory against a 1 GB managed heap immediately indicates a native-side
   leak regardless of RCW/CCW details.

---

## 2. ClrMD API surface — feasibility assessment

This section is the primary output of the spike. Until the spike runs, all paths below are
**hypotheses**, not confirmed implementation plans.

### 2.1 Hypothesis A — `ClrRuntime.EnumerateHandles()` with `GCHandleKind`

`ClrRuntime.EnumerateHandles()` (the same API `GCHandleAnalyzer` already uses) returns
`ClrHandle` records. `ClrHandle.HandleKind` includes `RefCounted` — the handle kind associated
with RCW/CCW reference-counted handles. This is already public and enumerated by `GCHandleAnalyzer`.

**What's missing**: `ClrHandle` for a `RefCounted` handle does not directly expose the RCW or CCW
wrapper object, the wrapped `IUnknown` pointer, the native reference count, or the managed type
being wrapped. Inferring "this is a CCW" vs "this is an RCW" from a bare `ClrHandle` may not be
possible without reading the internal `SyncBlock` or `InteropSyncBlockInfo` data behind it.

**Spike question A**: Does `ClrHandle.Object` for a `RefCounted` handle resolve to the managed
wrapper type, and is that type's name a reliable indicator of RCW vs CCW?

### 2.2 Hypothesis B — Sync-block table parsing

Each managed object that has been pinned or used for COM interop has a sync-block header (the
word before the method table pointer). The sync-block table (accessible at a known DAC offset)
contains `InteropSyncBlockInfo` for objects with CCWs. ClrMD does not expose this directly, but
the `ClrRuntime` DAC client can be interrogated via `ISOSDacInterface` methods if ClrMD's
underlying `DataTarget` exposes a custom data accessor.

**Spike question B**: Can we enumerate the sync-block table via ClrMD 4's public or semi-public
API surface without P/Invoking into DBGHELP or the DAC DLL directly?

### 2.3 Hypothesis C — `Process.PrivateMemorySize64` via dump metadata

For native committed memory totals, the dump itself may contain a system-info stream
(`MINIDUMP_SYSTEM_INFO` / `MINIDUMP_MEMORY_INFO_LIST`) if it was captured as a heap dump rather
than a mini dump. ClrMD's `DataTarget` exposes `DataReader.GetMemoryInfo()` or similar on
`WindowsProcessDataReader` — this gives committed/reserved page ranges, from which total committed
native memory (excluding GC segments, which are already tracked by `HeapTopologyAnalyzer`) can be
derived.

**Spike question C**: Does ClrMD 4's `DataTarget` / `DataReader` expose the process memory
information list for full-heap Windows dumps?

### 2.4 Spike scope

The spike should be a small standalone tool (like the existing `tools/Phase0/` and
`tools/ProfileRootPathBackfill/` probes) that runs against a known COM-interop dump and:
- Confirms which of A/B/C yields usable data.
- Reports the raw field values found and which are ClrMD-accessible without private API use.
- Estimates the per-object read cost for the chosen path.
- Identifies any .NET-version-specific offsets or API differences.

**Do not start full implementation until the spike result is recorded.**

---

## 3. Scan design (post-spike, assuming Hypothesis A or B confirmed)

### 3.1 If Hypothesis A is confirmed (`RefCounted` handles via `EnumerateHandles`)

`NativeInteropAnalyzer` does **not** implement `IHeapIndexScanParticipant` — its data source is
`ClrRuntime.EnumerateHandles()`, not the object index. It follows the same pattern as
`GCHandleAnalyzer` (non-participant, reads handle table directly in `AnalyzeAsync`).

**`AnalyzeAsync`**:
1. Enumerate all handles; filter to `HandleKind.RefCounted`.
2. For each, resolve `handle.Object` and read its type name. Classify as RCW / CCW / Unknown based
   on type-name heuristics (`__ComObject`, user-defined COM-import types, etc.).
3. Accumulate per-type counts and total retained size.
4. For top-K by instance count: resolve a sample root path via `SampleRootPathFinder`.

### 3.2 If Hypothesis B is confirmed (sync-block table walk)

Implement a dedicated `SyncBlockTableReader` (`Analyzers/Interop/SyncBlockTableReader.cs`) that
wraps the DAC/sync-block API and exposes:

```csharp
internal readonly struct ComWrapperInfo
{
    public readonly ulong ManagedObjectAddress;
    public readonly bool  IsCcw;
    public readonly int   NativeRefCount;   // -1 if not readable
    public readonly string? WrappedTypeName;
}

IEnumerable<ComWrapperInfo> EnumerateComWrappers(ClrRuntime runtime);
```

`NativeInteropAnalyzer.AnalyzeAsync` calls `SyncBlockTableReader.EnumerateComWrappers` and
accumulates results — no heap scan, just the sync-block enumeration.

### 3.3 Native committed memory (Hypothesis C)

Regardless of which COM-wrapper path is chosen, add native-memory totals as a secondary output:

```csharp
// In AnalyzeAsync, after handle/sync-block enumeration:
ulong totalNativeCommitted = ReadNativeCommittedMemory(context.Runtime);
```

Where `ReadNativeCommittedMemory` reads memory-info-list records and sums committed bytes for
non-GC-managed page ranges (subtract GC segment extents already available from
`HeapTopologyDomainResult` via the inter-analyzer result bus).

---

## 4. Domain result and output model

```
NativeInteropDomainResult : AnalyzerDomainResult
  IsPresent                          bool           // false if no COM interop detected
  ApiPath                            string         // "RefCountedHandles" | "SyncBlockTable" | "None"
  RcwCount                           int
  CcwCount                           int
  TotalRcwRetainedBytes              ulong          // estimated
  TotalCcwRetainedBytes              ulong
  NativeCommittedBytes               ulong?         // null if dump doesn't expose memory info
  ScanCapped                         bool
  TopRcwTypes                        IReadOnlyList<InteropTypeEntry>
  TopCcwTypes                        IReadOnlyList<InteropTypeEntry>

InteropTypeEntry
  TypeName                           string
  InstanceCount                      int
  EstimatedRetainedBytes             ulong
  SampleRootPath                     string?
```

---

## 5. Infrastructure reuse

| Need | Existing infrastructure |
|------|------------------------|
| Handle enumeration (Hypothesis A) | `ClrRuntime.EnumerateHandles()` — already used by `GCHandleAnalyzer` |
| Type-name matching | `TypeNamePatternMatcher.HasAnyPrefix` / `ContainsAny` |
| Root path for top-K items | `SampleRootPathFinder` |
| Native memory cross-reference | `HeapTopologyDomainResult` via `AnalyzerRunResultsExtensions.GetResult<T>` (to subtract GC segment bytes) |

---

## 6. Registration fan-out

| Artifact | Class name |
|----------|-----------|
| Domain result | `NativeInteropDomainResult` |
| Finding generator | `NativeInteropFindingGenerator : IFindingGenerator<NativeInteropDomainResult>` |
| Trend comparer | `NativeInteropTrendComparer` — delta on `RcwCount`, `CcwCount`, `NativeCommittedBytes` |
| Section builder | `NativeInteropSectionBuilder : ISectionBuilder<NativeInteropDomainResult>` |

---

## 7. Scan caps

```
MaxHandlesToInspect        5000    // RefCounted handle accumulator cap (rare to exceed)
MaxTypesToReport             30    // top-K types in RCW and CCW breakdowns separately
```

---

## 8. Key risks and mitigations

| Risk | Mitigation |
|------|-----------|
| **No clean API path exists** (all hypotheses fail the spike) | Defer implementation entirely; document the gap and the spike findings; do not attempt private DAC manipulation. Report native committed memory only (Hypothesis C is independent). |
| Hypothesis A gives only `RefCounted` handles without RCW/CCW distinction | Fall back to `HandleKind.RefCounted` aggregate count with no RCW/CCW breakdown; still useful |
| Sync-block table offsets shift across CLR versions | Per-version offset table; graceful degradation to Hypothesis A or count-only mode |
| Large number of COM wrappers (rare, but possible in COM-heavy apps) | Hard cap at `MaxHandlesToInspect`; set `ScanCapped = true` |
| Native memory info not present in dump | `NativeCommittedBytes = null`; mention in section builder that memory info is only available in full-heap dumps |

---

## 9. Spike deliverable

The spike should produce a short investigation note (analogous to
[p1-item-11-minidump-exception-stream-investigation.md](../phase-0/p1-item-11-minidump-exception-stream-investigation.md))
recording:
- Which hypothesis was confirmed, partially confirmed, or rejected.
- Exact ClrMD API calls used and their output on a representative COM-interop dump.
- Per-item cost estimate (how long does enumerating 10,000 handles take?).
- Any version-specific differences found.
- A "proceed / defer" recommendation.

Until that note exists, `NativeInteropAnalyzer` is not in the implementation queue.

---

## 10. What this analyzer does NOT do

- Detect P/Invoke signatures or call sites (static analysis concern).
- Inspect SafeHandle subclasses beyond their object address and type name (the wrapped native
  handle value is opaque in a managed dump).
- Enumerate native heap allocations (malloc/VirtualAlloc blocks outside GC segments) — these
  are not tracked in a managed dump unless the dump was captured with full native heap included,
  and even then require native symbol resolution.
- Replace a dedicated native memory profiler (Dr. Memory, Application Verifier) for native-leak
  diagnosis; the goal is surfacing the managed-side COM-wrapper signal, not full native analysis.
