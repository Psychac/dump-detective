# TimerLeakAnalyzer P1-2 Scope: Implement ITypedResourceInstanceSampler

## Overview
Implement `ITypedResourceInstanceSampler<T>` interface to capture timer state fields (_period, callback owner) during the shared heap scan pass, eliminating need for post-scan re-traversal.

**Complexity:** Medium
**Effort:** ~4-6 hours implementation + testing
**Impact:** High - makes findings actionable (timer intervals, ownership attribution)

---

## Reference Implementation Pattern

**DbConnectionAnalyzer** demonstrates the pattern:
- Implements `ITypedResourceInstanceSampler<DbConnectionSnapshot>` (line 20)
- Defines snapshot class: `DbConnectionSnapshot(typeName, address, stateLabel, stateValue, etc.)`
- Provides `MaxStateSamplesPerType` (500) and `TopSampleCap` (50) properties
- Implements `TrySample()` method that reads state fields during the shared scan pass
- Uses `InstanceStateSampler<T>` helper to manage per-type sample slot allocation
- Integrates with `IParallelHeapIndexScanParticipant` (BeforeHeapIndexScan, OnHeapEntry) if available

---

## Work Breakdown

### 1. Define TimerStateSnapshot Class
**File:** `src/DumpDetective.Analysis/Models/InfrastructureDomainModels.cs`

```csharp
internal sealed record TimerStateSnapshot(
    string TypeName,
    ulong Address,
    uint Generation,
    string? CallbackOwnerType,        // Type of the delegate's _target
    long PeriodMs,                     // -1 if invalid/unreadable
    long DueTimeMs,                    // -1 if invalid/unreadable
    string? IntervalCategory           // "Recurring" / "One-shot" / "Suspended"
);
```

**Notes:**
- _period == Timeout.Infinite (-1) means one-shot or suspended
- _period == 0 means one-shot
- _period > 0 means recurring with interval in ms
- CallbackOwnerType extracted from TimerQueueTimer._timerCallback._target.Type.Name

### 2. Add ITypedResourceInstanceSampler Implementation to TimerLeakAnalyzer

**File:** `src/DumpDetective.Analysis/Analyzers/TimerLeakAnalyzer.cs`

**Interface signature:**
```csharp
public int MaxStateSamplesPerType => 200;  // Per-type sampled states (tunable)
public int TopSampleCap => 50;              // Max timer types to sample
TimerStateSnapshot? ITypedResourceInstanceSampler<TimerStateSnapshot>.TrySample(
    ClrHeap heap, in HeapEntry entry, string typeName);
```

**Implementation points:**
- Only sample for TimerQueueTimer and PeriodicTimer (the logical timer types)
- Read _period field (int/long, check System.Threading timeouts)
- Read _dueTime field (optional, for pending vs. suspended classification)
- Try to extract callback owner: _timerCallback → _target → Type.Name
- Return null if state can't be read (invalid object)
- Catch ClrMD exceptions gracefully (corrupt memory)

**Field resolution strategy:**
```
TimerQueueTimer fields:
  _period: int/long (System.Threading.Timer._period)
  _dueTime: int/long (System.Threading.Timer._dueTime)
  _timerCallback: delegate (TimerCallback)
  
TimerCallback structure:
  _target: object (the subscriber/owner object)
  _methodPtr: IntPtr
  
PeriodicTimer fields (similar but may differ):
  _period: TimeSpan (or int milliseconds)
  _callback: Delegate (inspect for owner)
```

### 3. Update Domain Model to Include Sampled Data

**File:** `src/DumpDetective.Analysis/Models/InfrastructureDomainModels.cs`

Add to `TimerObjectTypeSummary`:
```csharp
internal sealed record TimerObjectTypeSummary(
    string TypeName,
    int Count,
    ulong TotalBytes,
    Evidence? Evidence = null,
    IReadOnlyList<TimerStateSnapshot>? Samples = null,      // NEW: up to MaxSamples per type
    string? DominantIntervalCategory = null,                 // NEW: "Recurring" / "One-shot" / etc.
    IReadOnlyDictionary<string, int>? CallbackOwnerCounts = null  // NEW: top callback types
);
```

### 4. Integrate with Heap Scan Pipeline (Optional Path)

**If using `IParallelHeapIndexScanParticipant`:**
```csharp
public void BeforeHeapIndexScan(AnalysisContext context)
{
    _heap = context.Heap;
    _candidateMts = TypedResourceScanDriver.DiscoverCandidates(this, context.Heap, context.Cache);
    _sampler = TypedResourceScanDriver.CreateSampler(this);  // Creates per-type slot manager
}

public void OnHeapEntry(in HeapEntry entry, ulong methodTableAddress)
{
    if (!_candidateMts.TryGetValue(methodTableAddress, out var info))
        return;
    if (_sampler?.TryReserveSample(methodTableAddress) != true)
        return;  // Slot full for this type, skip
    
    var snapshot = (this as ITypedResourceInstanceSampler<TimerStateSnapshot>)
        .TrySample(_heap!, entry, info.TypeName);
    if (snapshot != null)
        _samples[methodTableAddress].Add(snapshot);
}

public async ValueTask<AnalyzerDomainResult> AnalyzeAsync(AnalysisContext context, CancellationToken ct)
{
    // Sampled data is already populated from heap scan
    // Integrate samples into TimerObjectTypeSummary before returning result
}
```

**Alternative (lightweight):**
- Skip IParallelHeapIndexScanParticipant
- Keep PopulateEvidence for root paths
- Call sampler within PopulateEvidence loop (single pass over existing sample addresses)
- Simpler integration, slightly less efficient (two passes instead of one)

### 5. Update Finding Generator & Section Builder

**File:** `src/DumpDetective.Reporting/FindingGenerators/TimerLeakFindingGenerator.cs`

Add to evidence string:
```
Top callback owners: [SomeClass: 45, OtherClass: 23, ...]
Recurring timers: 120 (avg interval: 150ms)
One-shot timers: 30
```

**File:** `src/DumpDetective.Reporting/SectionBuilders/TimerLeakSectionBuilder.cs`

Add section showing sampled state:
- Per-type interval distribution (< 100ms, 100-1000ms, > 1s, suspended)
- Top callback owner types (who's creating these timers)
- Sampled timer instances with their _period / _dueTime values

### 6. Testing Strategy

**Unit tests:**
- Test field reading with valid timer objects
- Test error handling for corrupt/invalid objects
- Test callback owner extraction with various delegate targets
- Test period categorization (recurring vs. one-shot vs. suspended)

**Integration tests:**
- Create small heap dump with mix of recurring, one-shot, suspended timers
- Verify sampler populates expected data per type
- Verify section builder renders interval distributions
- Verify callback owner attribution

**Performance:**
- Benchmark sampler overhead (should be minimal, ~1ms per type)
- Verify no memory bloat from storing samples

---

## Known Challenges

1. **Field name variability**: Timer field names may differ across .NET versions/implementations
   - Solution: Try array of known field names in priority order
   - Fallback: Return null if core fields unavailable

2. **Callback type extraction**: Delegate may have null _target or internal framework types
   - Solution: Filter for user types (check namespace, exclude System.*, internals)
   - Fallback: Label as "Framework" or "Unknown"

3. **Period units**: System.Threading.Timer uses milliseconds, PeriodicTimer uses TimeSpan
   - Solution: Detect type, normalize to milliseconds
   - Fallback: Store as-is, convert in display layer

4. **Sample slot management**: Sampler interface has strict pre-reservation contract
   - Ensure TryReserveSample called before TrySample
   - Use InstanceStateSampler<T> helper (handles reservation tracking)

---

## Deliverables

1. ✅ TimerStateSnapshot model
2. ✅ ITypedResourceInstanceSampler<T> implementation
3. ✅ Field reading logic (period, dueTime, callback owner)
4. ✅ Integration with heap scan or PopulateEvidence
5. ✅ Domain result updates (samples per type)
6. ✅ Finding generator evidence update
7. ✅ Section builder interval/callback display
8. ✅ Unit tests (field reading, error handling)
9. ✅ Integration tests (full end-to-end)
10. ✅ Audit doc update

---

## Success Criteria

- [ ] Sampler captures _period and callback owner for up to N samples per timer type
- [ ] Section builder displays interval distribution and top callback owners
- [ ] Finding evidence includes actionable callback attribution
- [ ] All tests pass, no performance regression
- [ ] Works on 10GB+ dumps without memory bloat
- [ ] Zero heap corruption from field reading attempts

---

## Dependencies

- `InstanceStateSampler<T>` helper class (already exists in codebase)
- ClrMD field reflection APIs (already used in codebase)
- Domain model updates to support samples per type

**No new external dependencies.**

---

## Estimated Timeline

| Phase | Effort | Notes |
|-------|--------|-------|
| Model + interface | 1h | Define snapshot, properties, signatures |
| Field reading | 1.5h | Implement TrySample with error handling |
| Integration | 1h | Wire sampler into heap scan or PopulateEvidence |
| Reporting | 1h | Update finding generator and section builder |
| Testing | 1-1.5h | Unit + integration tests |
| Documentation | 0.5h | Update audit, add implementation notes |
| **Total** | **5.5-6.5h** | With buffer for unknowns |

