# AsyncStateMachineAnalyzer — Design Spec

## Status
**New** · Implementation Priority **16** · Effort: Medium

## Report Sections Served
- §23.1 State Machine Population (count, size, state field distribution)
- §23.2 Captured Closure Analysis (captured ref size, problematic captures)
- §23.3 Suspended Method Map (originating method, declaring type, fire-and-forget detection)

## Rationale
Suspended async methods are a major hidden heap consumer in async-heavy services.
Each represents a live call frame held in memory for the duration of an await.

---

## Domain Result

```csharp
AsyncStateMachineDomainResult(
    int TotalStateMachines,
    ulong TotalStateMachineBytes,
    IReadOnlyList<StateMachineTypeProfile> TopStateMachineTypes,
    IReadOnlyList<HighCaptureStateMachine> TopByCapturedSize,
    IReadOnlyList<SuspendedMethodEntry> SuspendedMethodMap,
    bool ScanLimited)

StateMachineTypeProfile(
    string TypeName,
    string OriginatingMethod,
    string DeclaringType,
    int Count,
    ulong TotalBytes,
    int AvgStateValue,
    int ReferenceFieldCount)

HighCaptureStateMachine(
    ulong Address,
    string TypeName,
    ulong TotalCapturedRefBytes,
    IReadOnlyList<string> LargeCaptures)

SuspendedMethodEntry(
    string DeclaringType,
    string MethodName,
    int SuspendedCount,
    ulong TotalBytes)
```

---

## Implementation Strategy

State machine detection:
1. Type name pattern: `ClrType.Name` matches `<.*>d__\d+` regex
2. Interface check: `ClrType.Interfaces.Any(i => i.Name == "System.Runtime.CompilerServices.IAsyncStateMachine")`

Filter via `TypeAggregates` type names first — O(1) per type, no heap scan for detection.

- State field: `ClrType.Fields.FirstOrDefault(f => f.Name == "<>1__state")` — read integer
- Captured refs: `ClrType.Fields` — count `IsObjectReference` fields; for top-N instances,
  read each reference field and accumulate `ClrObject.Size`
- Method name decode: strip `<` prefix and `>d__N` suffix from `ClrType.Name`
- **Bounded**: top 200 state machine types by count; deep field read on top 50 instances only

---

## Phase Assignment — Entirely Phase 2

State machine detection is based on `ClrType.Name` pattern and `ClrType.Interfaces` — both
available from `TypeAggregates` without a heap scan. Field reading for closure analysis
requires Phase 2 ClrMD access but is bounded to top-N types.

```
Phase 2:
  1. Scan TypeAggregates type names for <.*>d__\d+ pattern — O(types) string match
  2. For matched MTs: read ClrType.Interfaces to confirm IAsyncStateMachine
  3. Field analysis: ClrType.Fields for <>1__state and reference fields
  4. Instance count from TypeAggregates.Count (zero heap scan for basic summary)
  5. Deep capture analysis: ClrObject field reads for top 50 instances only
```

No new disk file required. Pure Phase 2 — uses `TypeAggregates` name index.

---

## Related Analyzers
- **`AsyncTaskAnalyzer`** (new) — §23.3 cross-reference: state machines with associated faulted Task objects
- **`InsightEngine`** — fire-and-forget leak finding: `SuspendedCount > 100` for same method
