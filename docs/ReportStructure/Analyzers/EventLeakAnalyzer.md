# EventLeakAnalyzer — Coverage & Change Spec

## Status
**Existing** · Modify · Implementation Priority **13** · Effort: Medium

## Report Sections Served
- §4.3 Retention Patterns (event chain detection — partial)
- §12.1 Subscription Graph (publisher → subscriber, leaking events only today)
- §12.2 Event Leaks (retained subscriber count, severity, static vs instance split)

---

## Currently Produces
- `EventLeakDomainResult`: total leaks, subscriber counts, static vs instance split
- `EventLeakGroupSnapshot`: publisher type, event name, severity, subscriber types
- Filters by `MinSubscribers` threshold — only scans delegates above the threshold

---

## What Is Missing

| Gap | Report Section | Priority |
|-----|---------------|----------|
| Subscription graph for **non-leaking** events | §12.1 | Medium |
| Publisher object count (how many publisher instances exist) | §12.1 | Low |
| `DelegateHelper` usage for non-multicast delegate chains | §12.1 | Low |
| `_invocationList` depth / nested multicast | §12.1 | Medium |
| Publisher lifetime (Gen0/1 vs Gen2/static) | §12.2 | Medium |
| `EventHandler<T>` vs `Action` vs custom delegate classification | §12.2 | Low |
| Subscriber shallow size sum per event (`EstimatedSubscriberRetainedBytes`) | §12.2 | Medium |

---

## Required Changes

1. **Add `SubscriptionGraphMode` option** — `bool IncludeNonLeakingEvents` on `EventLeakOptions`.
   When enabled, scan all `MulticastDelegate` fields, not just those above `MinSubscribers`.
   This fills §12.1 "full subscription graph". Default off for performance.
2. **Add `TotalEventsScanned`** and `TotalPublisherInstances` to `EventLeakDomainResult` —
   gives context for the subscription graph section even without full mode.
3. **Add `EstimatedSubscriberRetainedBytes`** per `EventLeakGroupSnapshot` — multiply
   subscriber count by average subscriber type size from the heap index. Fills §12.2
   retention impact data.

---

## Phase Assignment

### Current Phase Assignment
| Step | Current Phase | Location |
|------|--------------|----------|
| Find MulticastDelegate objects | Phase 2 | Full `heap.EnumerateObjects()` with MT filter |
| Inspect delegate fields | Phase 2 | Per-object ClrMD field access |

### Proposed Phase Assignment
| Step | Proposed Phase | Notes |
|------|---------------|-------|
| Tag delegate MTs | **Phase 1** | `Flags` byte bit 2 = `IsDelegateType` in TypeAggregateIndexEntry |
| `EventCandidateIndex.bin` | **Phase 1** | Capture all delegate object addresses during segment scan |
| Delegate field inspection | Phase 2 | Reads from EventCandidateIndex (not full heap scan) |

### Phase 1 Extension — `EventCandidateIndex.bin`

During the parallel segment scan, when `obj.Type.IsDelegate` (or type name ends in `Delegate`
or `EventHandler`), write to `EventCandidateIndex.bin`:

```
EventCandidateIndex.bin
Header (16 bytes): Magic(4) | Version(4) | RecordCount(8)
Per record (16 bytes): Address(8) | MethodTable(8)
```

Size estimate: 500K delegates × 16 bytes = 8MB. Conservative for large apps.
Phase 2 `EventLeakAnalyzer` reads this index instead of walking the full heap.

---

## Related Analyzers
- **`DependentHandleAnalyzer`** — `IsPotentialEventSource` flag links CWT entries to event patterns
- **`StaticRootLeakDetector`** — static field retention; event chains via static fields overlap with §4.3
- **`CollectionAnalyzer`** — cache retention patterns complementary to event chain retention
