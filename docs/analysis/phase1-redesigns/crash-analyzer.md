## Redesign Proposal

### Verdict: redesign, not patch-list

The audit's own evidence forces this: two silent P0 correctness bugs
(`BuildCrashThreadSnapshots` ignoring configured options, `_stackTrace` byte-buffer misread),
a duplicated dual-path implementation that "must be kept in sync manually" with no compiler
enforcement, an unbounded `SampleMessage` design that structurally cannot support message-
distribution (I-10) without rework, and five of fifteen improvement items (I-2, I-3, I-9, I-11,
E-1) all touching the same `ExtractExceptionInfo` call site. Patching these independently means
touching `ExtractExceptionInfo`, `ExtractExceptionStackTrace`, `OnHeapEntry`, and
`RunParallelExceptionScan.ProcessEntry` five separate times, each risking a fresh divergence
between the two paths. One structural pass fixes the bugs as a side effect and removes the
duplication that caused them.

### Design goal

Single per-object extraction function, called from exactly one place per object, that both scan
paths invoke — eliminating the dual-implementation drift risk (E-3) as a byproduct rather than a
separate project. Every new field this proposal adds (`GcGeneration`, `SizeBytes`, crash-bucket
hash, rethrow flag, AggregateException unwrap) is populated inside that one function, from data
already resident during the object visit — no second heap scan, no materialized graph.

### 1. Collapse the dual path into one accumulator

```csharp
// Called once per object from BOTH OnHeapEntry (participant) and
// RunParallelExceptionScan.ProcessEntry (fallback). No other call site
// may read exception fields directly.
private readonly struct ExceptionVisit
{
    public static void Process(
        ClrHeap heap,
        ulong address,
        ulong methodTable,
        uint generation,
        ulong size,
        ActiveExceptionContext? activeContext,
        ExceptionScanAccumulator acc,
        ILogger? logger)
}
```

`ExceptionScanAccumulator` is a plain mutable class (not a struct — it's carried across many
calls) holding exactly the fields `OnHeapEntry` currently holds as loose instance fields today
(`_exceptionsByType`, `_exceptionTypeCounts`, `_crashThreadCandidates`, ...) plus the new ones
below. `OnHeapEntry` becomes a 4-line wrapper that unpacks `HeapEntry` and calls
`ExceptionVisit.Process`. `RunParallelExceptionScan.ProcessEntry` becomes the same 4-line
wrapper over a per-worker `ExceptionScanAccumulator`, merged with the existing `MergePartial`
logic (unchanged — it already operates on accumulator-shaped data). This is the platform-level
pattern E-3 asks for, scoped here to prove it out before generalizing to other
`IHeapIndexScanParticipant` analyzers.

Fixes I-1 as a consequence: there is no longer a second code path that can reach for
`CrashAnalysisOptions.Default` — `ExceptionVisit.Process` takes the live `_options` by reference
from the single caller.

### 2. Replace field-walking with `ClrException`

`ExtractExceptionInfo` and `ExtractExceptionStackTrace` are deleted. `ExceptionVisit.Process`
calls `heap.GetObject(address).AsException()` once. This single change:

- Fixes the broken `_stackTrace` byte-buffer path (Bug 2) — `ClrException.StackTrace` returns
  `IEnumerable<ClrStackFrame>` already correctly parsed by ClrMD.
- Removes five `GetFieldByName` lookups (`_message`, `_HResult`, `_innerException`,
  `_stackTraceString`, `_stackTrace`) and their fragility against CLR layout changes.
- Gives `.Inner` as a typed `ClrException?` — walked directly for AggregateException unwrap and
  chain-depth (no separate field lookup for `_innerException`).

`ComputeExceptionChainDepth`'s `HashSet<ulong>` cycle guard is retained verbatim — it is correct
and cheap, and `ClrException` does not change the cycle risk on corrupted heaps.

### 3. AggregateException unwrapping, inline, no second scan

When `AsException()?.Type?.Name == "System.AggregateException"`, walk the `InnerExceptions`
array field (bounded: first 16 entries, matching `ComputeExceptionChainDepth`'s existing depth
cap) inside the same `Process` call, using the `ClrObject` already in hand. Each unwrapped inner
exception is attributed to the accumulator under its own type key exactly as if it had been
visited directly — same code path, same caps, so `MaxExceptionsPerType` still bounds it. No
additional `heap.GetObject` calls beyond what enumerating a `ClrValueType[]` costs; this is a
constant-size in-memory decode of an object already loaded, not a heap scan.

### 4. Crash bucket, computed opportunistically

`ExceptionInstance` gains a `readonly struct CrashBucketKey { string ExceptionType; string
TopUserFrame; }` computed at extraction time from `ClrException.StackTrace`'s first frame not
matching `IsFrameworkFrame`. Store a `Dictionary<CrashBucketKey, int>` count on the accumulator
(bounded implicitly — distinct `(type, frame)` pairs are far fewer than distinct instances in
every real workload). Ranked in the finding generator, not the hot path. This directly answers
I-11 / Audit Area 4 item 2 at effectively zero marginal cost, since the top frame is already
being read to build `topFrames` today.

### 5. GC generation and size — already free

Per [[project_persisted-generation-column-implemented]], `HeapEntry.Generation` is already
populated per-object in the index. `ExceptionVisit.Process` takes `entry.Generation` and
`entry.Size` as parameters (already in hand from the `HeapEntry` the caller unpacked) and
accumulates `Dictionary<string, GenerationHistogram>` and `Dictionary<string, ulong>
TotalSizeByType` alongside the existing type-count dictionaries. Zero additional reads. Answers
I-8 and I-14 together, and the generation histogram is what feeds the Gen2/LOH filter for
retention-path selection (§7 below).

### 6. Message distribution without unbounded growth

Today `SampleMessage` on `CrashThreadCandidate` holds exactly one string, discarding the rest —
this is the structural blocker for I-10, not just a missing feature. Replace with a bounded
per-type structure:

```csharp
internal sealed class MessageDistribution
{
    // Capped at 20 distinct messages per type; 21st+ distinct message increments Overflow
    // instead of growing unboundedly. This is the one place in the redesign that needs an
    // explicit cap beyond MaxExceptionsPerType, because message cardinality is unrelated to
    // instance cardinality (a SqlException type can have thousands of instances but 3 messages,
    // or three instances with three totally distinct messages).
    private readonly Dictionary<string, int> _counts = new(capacity: 20, StringComparer.Ordinal);
    public int DistinctOverflow { get; private set; }
    public void Record(string message) { ... }
}
```

One `MessageDistribution` per exception type in the accumulator, `Record`-ed inline during
`Process`. Report surfaces distinct-count, top message, and whether overflow occurred — enough
to distinguish "3 query failures" from "systemic connectivity failure" per Audit Area 4 item 4,
without an unbounded string retention risk on adversarial or pathological dumps.

### 7. Retention paths — deferred to Phase 2, on-demand only

E-1 / I-8's Gen2 root-path lookup is explicitly **not** run inside `Process`. It runs after the
heap scan completes, over the top-N suspects only (Gen2/LOH exceptions ranked by
`TotalSizeByType` from §5, capped at e.g. 10), each issued as an on-demand BFS (depth ≤ 5)
against the existing `ReverseReferenceIndex` — the same lazy, scoped pattern
`DominatorAnalyzer` already uses. This matches the project's Phase 1/Phase 2 split
(`docs/architecture.md`) exactly: streaming index build stays untouched; expensive graph work
runs only on the filtered subset the scan already ranked.

### 8. Rethrow flag and confidence honesty

`ClrException` does not expose `_remoteStackTraceString` directly (it's not part of the typed
surface), so this one field stays a `GetFieldByName("_remoteStackTraceString")` lookup —
deliberately kept outside the `ClrException` migration rather than forcing a workaround. Set
`ExceptionInstance.IsRethrow` when non-null; the inference-tier loop in
`BuildCrashThreadSnapshotsImpl` downgrades `MessageHResult`/`TypeInnerType` tier matches by one
confidence level when the matched instance `IsRethrow`. The lead finding's `ConfidenceScore`
(I-13) is derived by mapping the actual `InferenceConfidence` distribution across
`CrashThreadCandidateSnapshot`s to a score — `Exact`-heavy reports score high, `TypeInnerType`-
heavy reports score low — replacing the hardcoded 0.85.

### 9. Type check: `ClrType.IsException`, not substring match

`IsExceptionEntry`/`ResolveExceptionType`'s `typeName.Contains("Exception")` is replaced by
`clrObject.Type?.IsException == true` (I-5), fixing both the `ExceptionDispatchInfo` /
`ExceptionHandlingMiddleware` false-positive class and the `DatabaseError : Exception`
false-negative class in one change, since `IsException` walks the base-type chain.

### 10. Diagnostics

`CrashAnalyzer` gains an optional `ILogger<CrashAnalyzer>? logger` constructor parameter per the
platform's `ActivatorUtilities` convention (see `docs/architecture.md` § 14). The `catch {}`
around `ClrException` extraction logs at debug level with the object address and method table on
failure — replacing today's fully silent swallow, which is especially important now that a
single extraction function is the only place this can fail.

### What this removes

- `ExtractExceptionInfo`, `ExtractExceptionStackTrace` (replaced by `ExceptionVisit.Process` +
  `ClrException`)
- The dead `CreateFinding()` method (I-6)
- The `BuildCrashThreadSnapshots` static wrapper and its `new CrashAnalyzer()` instantiation (I-1)
- Independent field-lookup logic duplicated between `OnHeapEntry` and
  `RunParallelExceptionScan.ProcessEntry`

### What this does not change

- `IHeapIndexScanParticipant` / `IParallelHeapIndexScanParticipant` contracts and `MergePartial`
  merge semantics — the accumulator shape is compatible with the existing merge logic.
- The 4-tier stack-trace inference chain in `BuildCrashThreadSnapshotsImpl` — it is a genuine
  strength (Audit Area 2) and is preserved, only its confidence-to-score mapping changes (§8).
- `MaxExceptionsPerType`-bounded instance retention, `.Take`-based frame ceilings (raised to
  match `MaxCurrentThreadFramesToPrint` per I-4, not removed), and every other existing
  memory-bound in the analyzer.

### Sequencing

1. §1 (accumulator collapse) + §2 (`ClrException` migration) first — this is one PR that fixes
   Bug 1, Bug 2, I-1, I-2, I-3, I-6 simultaneously and is the foundation every other section
   builds on.
2. §9 (`IsException` check) — independent, trivial, can land same PR or immediately after.
3. §5 (generation/size, free) and §4 (crash bucket) next — both are additive fields on the
   already-migrated `Process` function.
4. §3 (AggregateException) and §6 (message distribution) — additive, moderate complexity.
5. §8 (rethrow/confidence) and §10 (logging) — polish, low risk.
6. §7 (retention paths) last — it is the only section touching platform infrastructure
   (`ReverseReferenceIndex`) outside this analyzer, and should follow the model
   `DominatorAnalyzer` already established rather than precede it.
