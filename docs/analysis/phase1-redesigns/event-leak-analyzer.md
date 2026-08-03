## Redesign from Scratch

> What would a ground-up rewrite look like, given the project's hard constraints:
> streaming-only heap traversal, no materialization, bounded memory, disk-backed indices,
> single allocation budget on the hot path?

The current implementation accreted incrementally — the fast scanner was added on top of
a ClrMD-first design, the participant pattern was bolted on later, orphaned-subscriber and
retained-bytes estimates were added without correcting their conceptual foundations. A clean
design starts from the data model and works outward.

---

### 1. Separate the Three Jobs That Are Currently Mixed Together

The analyzer currently conflates three fundamentally different responsibilities inside a
single class and scan loop:

- **A. Field discovery** — which types have event backing fields? (metadata, done once)
- **B. Subscription counting** — for each publisher instance, how many subscribers does each
  field hold? (hot scan, runs on every object)
- **C. Evidence enrichment** — where is this publisher retained? who are the subscribers?
  (expensive, runs post-scan on a small candidate set)

In the current implementation all three interleave. The fast scanner does B and initiates
part of C (root hints, subscriber type resolution). Evidence is then enriched again in
`PopulateEvidence`. The result is that the cost of C is paid per-object instead of per-candidate.

**Redesign: strict three-phase pipeline.**

```
Phase A  (pre-scan, once)   →  EventFieldRegistry       (MT → FieldDescriptor[])
Phase B  (heap scan, hot)   →  GroupCounterTable         (GroupKey → RawGroupCounter)
Phase C  (post-scan, top-K) →  EvidenceEnricher          (top-K candidates only)
```

Each phase has a clear interface contract and no knowledge of the others.

---

### 2. Ground the Group Key in Numbers, Not Strings

The current group key is `(string PublisherType, string EventFieldName, bool IsStatic)`.
This means every publisher object encountered on the hot path either looks up or allocates
two strings. On a heap with 500k publisher instances the string interning cost is non-trivial.

**Redesign: key by `(ulong PublisherMT, int FieldOffset, bool IsStatic)`.**

These are the exact values available from the pre-built `EventFieldRegistry` without any
string access. All human-readable names (type name, field name) are resolved lazily in
Phase C, after candidates are selected by count and severity.

```csharp
internal readonly struct GroupKey : IEquatable<GroupKey>
{
    public readonly ulong PublisherMT;
    public readonly int   FieldOffset;   // uniquely identifies the field within the type
    public readonly bool  IsStatic;
}
```

The registry maps `GroupKey → (string TypeName, string FieldName)` and is only consulted
when building the final result — never during the scan.

---

### 3. Replace GroupAccumulator with a Fixed-Size Value-Type Counter

`GroupAccumulator` is a class with `List<EventLeakInfo> TopInstances` — a heap-allocated
linked structure per group. The top-instance replacement logic (linear scan for minimum)
runs per publisher object on the hot path.

**Redesign: split into two tiers.**

**Tier 1 — hot-path counter (value type, fixed size):**
```csharp
internal struct RawGroupCounter
{
    public int  InstanceCount;
    public int  TotalSubscribers;
    public int  MinSubscribers;
    public int  MaxSubscribers;
    public ulong TotalSubscriberMTSum;   // for avg-size estimate without type lookup
}
```

No `List<>`, no strings, no allocation. Updated via `Interlocked` ops if parallelism
is ever introduced.

**Tier 2 — detail capture (reference type, allocated only for top-K candidates):**

After Phase B completes, select the top-K groups by `TotalSubscribers`. Only then
allocate `EventLeakGroup` objects with full subscriber detail. The detail pass re-reads
the heap for only those groups — a tiny fraction of the full scan.

This eliminates the current min-heap replacement logic from the hot path entirely.

---

### 4. A Single, Honest Retained-Bytes Strategy

Two options; pick one and be explicit about it.

**Option A — type-average estimate, clearly labelled.**
Compute `TotalSubscribers × avgSubscriberSizeByMT` across ALL instances (not just TopN).
The `avgSubscriberSizeByMT` comes from the pre-built type statistics cache. Label the
metric `"Estimated (type-average, all instances)"` in the report. Never suggest it is
accurate.

**Option B — dominator-backed, opt-in.**
If `DominatorAnalyzer` has run and its result is present in the `AnalysisContext`,
look up each unique subscriber MT in the dominator index and use its average
dominated-set size. Much closer to the real retained cost. Label as
`"Estimated (dominator-average)"`. Fall back to Option A silently when dominators are
unavailable.

The current design uses Option A but applies it only to stored TopN instances and calls
the result `"Est. Retained"` without caveat. That is worse than either option.

---

### 5. Fix the Orphaned-Subscriber Model or Drop It

The `CountOrphanedSubscribers` definition — "subscriber address not in rootHints" — is
wrong. Nearly every live object fails this test because `rootHints` contains only
direct GC root objects.

A correct "orphaned subscriber" means a subscriber whose only retention path runs
through the delegate chain — i.e., if the event were unwired, the subscriber would
become unreachable. That requires a reverse reference check, not a root-presence check.

**Redesign option A (correct, expensive):** After selecting top-K candidate groups,
use the existing `ReverseReferenceIndex` (if available) to count incoming references to
each subscriber address from outside the delegate chain. A subscriber with zero
non-delegate inbound references is genuinely orphaned.

**Redesign option B (correct, cheap heuristic):** A subscriber is "likely orphaned" if:
- It is NOT in the root map AND
- Its type is NOT a known long-lived type (services, singletons, registered DI components).

This is still a heuristic but it excludes the 99% of live objects that are correctly
retained transitively.

**Redesign option C (honest):** Remove the metric. Replace with "subscriber types not
seen as static roots" — a much narrower claim that is defensible.

---

### 6. Static Field Handling in One Pass

Currently static fields are processed twice: once per heap instance of the type
(via `ProcessPublisherEntry`) and once via `SweepModuleStaticFields` for instance-free
types. The deduplication using `processedStaticMTs` works but the two-pass structure
is fragile — if the order of operations changes, deduplication can break.

**Redesign: one static sweep, unconditionally after the heap scan.**

Phase A builds the `EventFieldRegistry` from all module types (including instance-free
ones). At the end of Phase B, iterate every MT in the registry that has static event
fields — regardless of whether heap instances were seen. This is a single foreach over
the registry's static-publisher entries:

```
foreach MT in registry.StaticPublisherMTs:
    if already processed: continue
    read static delegate fields
    accumulate into GroupCounterTable
```

No `processedStaticMTs` hash set needed during the heap scan. The scan only touches
instance fields.

---

### 7. Instance-Scoped Caches With Explicit Lifetimes

The current implementation has two scope-leaking caches:

- `static ConcurrentDictionary<ulong, HashSet<string>> _eventNameCache` on
  `EventLeakAnalyzer` — lives for the process lifetime.
- `_mtIndex` inside `EventLeakFastScanner` — scoped to the scanner instance (correct),
  but the scanner is created inside `BeforeHeapIndexScan` and inside `FindEventLeaks`,
  so its lifetime depends on call path.

**Redesign: all caches owned by `EventFieldRegistry`**, which is constructed once per
analysis context and disposed with it:

```csharp
internal sealed class EventFieldRegistry : IDisposable
{
    // MT → FieldDescriptor[] : built once, immutable after construction
    private readonly Dictionary<ulong, EventFieldDescriptor[]?> _byMT;
    // MT → (TypeName, FieldName) for lazy string resolution
    private readonly Dictionary<ulong, string> _typeNames;
    private readonly Dictionary<ulong, string> _fieldNames;
    // EventName sets per MT
    private readonly Dictionary<ulong, HashSet<string>> _eventNames;

    public void Dispose() { _byMT.Clear(); _typeNames.Clear(); ... }
}
```

The registry is built in Phase A, passed into Phase B (read-only), and disposed after
Phase C. No static state. No accumulation across dump sessions.

---

### 8. Cancellation as a First-Class Contract

Every phase accepts `CancellationToken` and checks it at a fixed granularity:

```csharp
private const int CancelCheckInterval = 10_000;  // objects

// inside the hot loop:
if ((++_scanned & (CancelCheckInterval - 1)) == 0)
    cancellationToken.ThrowIfCancellationRequested();
```

The bitwise mask check (`& mask`) avoids a modulo per iteration. This is one
branch per 10k objects — negligible, and it makes the analyzer responsive to
pipeline cancellation on large dumps.

---

### 9. EventHandlerList Support as a First-Class Code Path

WinForms components store delegate chains in `System.ComponentModel.EventHandlerList`
— a linked list of `(object key, Delegate handler)` pairs held in `Control.Events`.
This pattern is completely invisible to field-offset scanning.

**Redesign: `IEventExtractorStrategy` abstraction with two implementations.**

```csharp
internal interface IEventExtractorStrategy
{
    bool Matches(ClrType type);
    IEnumerable<(string FieldName, ulong DelegateAddr)> ExtractEvents(
        ulong objectAddress, IMemoryReader reader, int ptrSize);
}
```

- `DelegateFieldStrategy` — current logic, covers standard C# event backing fields.
- `EventHandlerListStrategy` — reads `Control.Events` linked list by walking the
  `System.ComponentModel.EventHandlerList` chain at known offsets.

Phase A registers all strategies; Phase B dispatches per type. Adding new patterns
(e.g. `Reactive.Subject` wrapper leaks, `DispatcherObject.Events`) is a one-class
addition.

---

### 10. Severity as a Continuous Score

The current formula is:

```
score = subscriberCount
      + (subscriberCount >= threshold ? bonus : 0)   ← step function
      + isStatic ? bonus : 0
      + ...
```

A publisher with 10 subscribers scores 15 (`10 + 5`). One with 9 scores 9. A 67% score
jump for 1 subscriber is not defensible.

**Redesign: log-scaled subscriber score + continuous bonuses.**

```csharp
double baseScore = Math.Log2(subscriberCount + 1) * 10.0;  // ~10 pts per doubling
double staticBonus    = isStatic          ?  8.0 : 0.0;
double gen2Bonus      = pubGen == 2       ?  5.0 : 0.0;
double dupBonus       = dupCount > 0      ? Math.Min(dupCount * 2.0, 10.0) : 0.0;
double mismatchBonus  = lifetimeMismatch  ?  6.0 : 0.0;
int    score = (int)Math.Round(baseScore + staticBonus + gen2Bonus + dupBonus + mismatchBonus);
```

| Subscribers | Old score | New score |
|---|---|---|
| 1 | 1 | 10 |
| 9 | 9 | 31 |
| 10 | 15 (+67%) | 33 (+6%) |
| 50 | 55 | 57 |
| 1000 | 1010 | 100 |

The new formula is capped at reasonable values (log₂(1000) × 10 ≈ 100), making
severity thresholds meaningful across the full range of subscriber counts.

---

### 11. What the Redesigned Class Surface Looks Like

```csharp
public sealed class EventLeakAnalyzer : IAnalyzer, IHeapIndexScanParticipant, IDisposable
{
    // Phase A: built once, immutable
    private EventFieldRegistry? _registry;

    // Phase B: hot-path state, value types only
    private GroupCounterTable? _counters;    // Dictionary<GroupKey, RawGroupCounter>

    // Phase B → C handoff
    private bool _scanCompleted;

    void IHeapIndexScanParticipant.BeforeHeapIndexScan(AnalysisContext ctx)
    {
        _registry = EventFieldRegistry.Build(ctx.Heap, ctx.AnalysisOptions.EventLeak);
        _counters = new GroupCounterTable(capacity: 4096);
        _scanCompleted = false;
    }

    void IHeapIndexScanParticipant.OnHeapEntry(in HeapEntry entry)
        => _counters!.Accumulate(entry, _registry!);   // no allocations

    void IHeapIndexScanParticipant.OnHeapIndexScanCompleted(bool succeeded)
        => _scanCompleted = succeeded;

    public ValueTask<AnalyzerDomainResult> AnalyzeAsync(
        AnalysisContext context, CancellationToken cancellationToken)
    {
        // Use participant counters if the shared scan ran; otherwise run a fresh scan.
        GroupCounterTable counters = _scanCompleted && _counters is not null
            ? _counters
            : RunFreshScan(context, cancellationToken);

        // Static sweep (once, unconditional)
        counters.AccumulateStaticFields(_registry!, context.Heap, context.AnalysisOptions.EventLeak, cancellationToken);

        // Phase C: enrich top-K candidates
        var topK = counters.SelectTopK(context.AnalysisOptions.EventLeak.MaxGroupsToEnrich);
        var enriched = EvidenceEnricher.Enrich(topK, context, cancellationToken);

        return ValueTask.FromResult(BuildResult(counters, enriched).Stamp(this));
    }

    public void Dispose() { _registry?.Dispose(); _counters?.Dispose(); }
}
```

No static fields. No `Console.Error.WriteLine`. Cancellation threaded through every
phase. The hot path (`OnHeapEntry`) is a single method call with no allocation.

---

### Summary of Key Design Decisions

| Decision | Current | Redesigned |
|---|---|---|
| Group key type | `(string, string, bool)` | `(ulong MT, int offset, bool)` |
| Hot-path allocation | `EventLeakInfo`, `List<SubscriberInfo>` per publisher | Zero — only `RawGroupCounter` update |
| String resolution | During scan | Post-scan, top-K only |
| Type name cache scope | Static, process-lifetime | Instance, disposed with analyzer |
| Static field sweep | Two passes, interleaved | One pass, post-scan, registry-driven |
| OrphanedSubscribers | Incorrect (non-root = orphaned) | Reverse-ref check (opt-in) or removed |
| Retained bytes | TopN instances only, no caveat | All-instances aggregate, explicit label |
| Severity formula | Step function | Log-scaled, continuous |
| Cancellation | Entry point only | Every phase, every 10k iterations |
| EventHandlerList | Not detected | `IEventExtractorStrategy` plugin slot |
| WinForms coverage | Zero | `EventHandlerListStrategy` implementation |
| Dominator integration | None | Optional `DominatorAnalyzer` result lookup |
