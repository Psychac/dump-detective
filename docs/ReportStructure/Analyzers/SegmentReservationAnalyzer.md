# SegmentReservationAnalyzer — Design Spec

## Status
**New** · Implementation Priority **19** · Effort: Low

## Report Sections Served
- §25.1 Committed vs Reserved Memory (total committed, total reserved, reservation gap)
- §25.2 Segment Lifecycle (ephemeral fill %, non-ephemeral SOH count, logical heap assignment)
- §25.3 Address Space Pressure (32-bit exhaustion risk, fragmented VA space detection)

## Rationale
The process may appear healthy by object count but be consuming vast virtual address space
due to GC reservation. Container environments with `RLIMIT_AS` constraints fail silently.
`SegmentAnalyzer` has per-segment committed bytes but lacks reserved memory, ephemeral fill %,
and logical heap grouping.

---

## Domain Result

```csharp
SegmentReservationDomainResult(
    ulong TotalCommittedBytes,
    ulong TotalReservedBytes,
    ulong ReservationGapBytes,
    double ReservedToCommittedRatio,
    int EphemeralSegmentCount,
    double AvgEphemeralFillPct,
    int NonEphemeralSohSegmentCount,
    IReadOnlyList<SegmentReservationEntry> SegmentTable,
    IReadOnlyDictionary<int, ulong> ReservedByLogicalHeap,
    bool AddressSpacePressureRisk,
    string PressureRiskReason)

SegmentReservationEntry(
    ulong Address,
    HeapSegmentKind Kind,
    ulong CommittedBytes,
    ulong ReservedBytes,
    bool IsEphemeral,
    int LogicalHeap,
    double FillPct)
```

---

## Implementation Strategy

- Enumerate `ClrHeap.Segments` — entirely Phase 2; no heap object scan
- `ClrSegment.CommittedMemory` and `ClrSegment.ReservedMemory` — direct property access
- `ClrSegment.IsEphemeral` — ephemeral fill = `CommittedMemory ÷ Length`
- `ClrSegment.LogicalHeap` — group by logical heap index for server GC per-CPU breakdown
- Address space pressure conditions:
  - `TotalReservedBytes > 1_500_000_000` on 32-bit process (`IntPtr.Size == 4`)
  - OR `ReservedToCommittedRatio > 10.0`
  - `PressureRiskReason` describes which condition triggered
- **No heap scan** — purely `ClrHeap.Segments` iteration

---

## Phase Assignment — Entirely Phase 2

`ClrHeap.Segments` enumeration is available in Phase 2. `ClrSegment.CommittedMemory`,
`ReservedMemory`, `IsEphemeral`, and `LogicalHeap` are direct property reads — no heap object scan.

```
Phase 2:
  1. foreach ClrSegment in heap.Segments:
       read CommittedMemory, ReservedMemory, IsEphemeral, LogicalHeap, Kind
  2. Sum committed/reserved globally; compute ReservationGapBytes
  3. Group by LogicalHeap → ReservedByLogicalHeap for server GC per-CPU breakdown
  4. Compute FillPct per IsEphemeral segment (CommittedMemory ÷ Length)
  5. Evaluate address space pressure flags
```

No new disk file required. Segments are enumerated fresh in Phase 2.

---

## Related Analyzers
- **`SegmentAnalyzer`** — per-kind byte counts and `HeapSegmentSnapshot` list; this analyzer adds reserved memory, ephemeral fill, and logical heap grouping that `SegmentAnalyzer` does not expose
- **`InsightEngine`** — address space pressure warning (risk flag), ephemeral segment fill critical (> 90%) alert
