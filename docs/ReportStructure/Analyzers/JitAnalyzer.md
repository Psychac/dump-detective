# JitAnalyzer — Design Spec

## Status
**New** · Implementation Priority **22** · Effort: Low · ⏳ **Pending**

## Report Sections Served
- §19.1 JIT Heap Usage (total JIT code heap size, manager count)
- §19.2 Compiled Method Analysis (active method hotspot map, native code size, unmanaged frame ratio)
- §19.3 Tiered Compilation & ReadyToRun (tiered method detection, R2R pre-compiled methods)

## Rationale
JIT code heap size and native method analysis expose memory cost not attributable to managed
objects. No current analyzer touches `ClrRuntime.GetJitManagers()` or `ClrMethod.HotColdInfo`.

---

## Domain Result

```csharp
JitDomainResult(
    ulong TotalJitHeapBytes,
    int JitManagerCount,
    double JitHeapPctOfTotalProcess,
    int ActiveMethodsOnStacks,
    IReadOnlyList<JitMethodSnapshot> TopLargestMethods,
    IReadOnlyList<NameCountEntry> TopActiveFrameTypes,
    int UnmanagedFrameCount,
    int ManagedFrameCount,
    int TieredMethodCount)

JitMethodSnapshot(
    string Signature,
    string DeclaringType,
    ulong NativeCodeAddress,
    uint HotSize,
    uint ColdSize,
    bool IsTiered)
```

---

## Implementation Strategy

- `ClrRuntime.GetJitManagers()` — iterate managers, sum `HeapBytes` per manager
- Active methods: walk `ClrStackFrame.Method` across all `runtime.Threads` stacks
- Large methods: `ClrMethod.HotColdInfo` — `HotSize + ColdSize > 64 KB` threshold
- Tiered detection: maintain `seen MetadataToken` set; second occurrence = tiered compile
- Unmanaged frame ratio: count `ClrStackFrame.Kind == FrameKind.Unmanaged` per thread
- **No heap scan required** — purely `runtime.Threads`, `GetJitManagers()`, method metadata

---

## Phase Assignment — Entirely Phase 2

JIT managers and method metadata are runtime state — not streamable in Phase 1
(separate heap from managed objects, requires live runtime).

```
Phase 2:
  1. runtime.GetJitManagers() — sum HeapBytes across all managers
  2. Walk runtime.Threads — collect ClrStackFrame.Method for all frames
  3. Per method: ClrMethod.NativeCode, HotColdInfo, MetadataToken
  4. Detect tiered: MetadataToken seen twice with different NativeCode addresses
  5. ClrStackFrame.Kind distribution (Managed / Runtime / Unmanaged) per thread
```

No new disk file required. Purely Phase 2 runtime metadata reads.

---

## Related Analyzers
- **`ThreadAnalyzer`** — `TopFrameHotspots` partially covers active methods; `JitAnalyzer` adds `NativeCode` size and tiered flag
- **`ModuleAnalyzer`** — `ClrModule.IsPEFile` R2R detection complements `JitAnalyzer`'s tiered analysis (§19.3)
- **`InsightEngine`** — JIT heap bloat finding (> 500 MB threshold), high unmanaged frame ratio warning
