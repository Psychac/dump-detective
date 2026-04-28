# 🔥 Dump Analyzer Report (Professional Tier)

## 🎯 Goal
Provide deep, structured, and explainable diagnostics for large-scale .NET memory dumps, enabling root cause analysis and actionable insights.

---

# 🧾 1. Executive Summary (Decision Layer)

## 📦 Contains
- Total managed memory + % of process
- Top memory consumers (by retained size)
- Key anomalies:
  - Memory leak likelihood
  - GC pressure
  - Thread contention
- Top 3 actionable recommendations

---

## 💡 Purpose
Enable quick decision-making without reading the full report.

---

# 🧠 2. Memory Topology

## 🔹 2.1 Heap Composition
### 📦
- SOH / LOH / POH / **FOH** (Frozen Object Heap) proportions
  - FOH holds immutable objects frozen by the runtime (string literals, `FrozenDictionary`, `MemoryMarshal`-pinned arrays); tracked via `HeapSegmentKind.Frozen`
- Object size distribution (histogram bucketed by size range)
- **GC mode**: Workstation vs Server GC (`ClrHeap.IsServerGC`)
- **Server GC heap count** (`ClrHeap.HeapCount`) — logical heaps, one per CPU
- **Per-logical-heap segment breakdown**: size and object count per heap index to detect cross-heap imbalance
  - Imbalanced heaps indicate thread affinity or allocation hotspot problems

---

## 🔹 2.2 Generation Pressure
### 📦
- Gen0/1/2 distribution
- Promotion patterns

---

## 🔹 2.3 Allocation Patterns
### 📦
- Gen0 object count as a proxy for recent allocation pressure
- Ratio of Gen0 : Gen2 object counts — high ratio indicates rapid churn; low ratio indicates accumulation
- Ephemeral segment fill % (`ClrSegment.IsEphemeral`) — high fill warns of imminent GC trigger
- Heuristic classification: **Accumulating** (large Gen2, low Gen0) vs **Churning** (large Gen0, high promotion rate) vs **Balanced**

> ⚠️ Allocation site granularity (exact call sites) requires ETW traces, not available from `.dmp` files. Classifications here are dump-snapshot heuristics only.

---

## 💡 Purpose
Understand how memory is structured and evolving.

---

# 🧱 3. Type System Analysis

## 🔹 3.1 Detailed Type Table
### 📦
Per type, sourced from `ClrType` metadata and heap index aggregates:

| Column | Source | Notes |
|---|---|---|
| Object count | Heap scan aggregate | Total instances on heap |
| Shallow size (total) | `ClrObject.Size` sum | Memory consumed by instances alone |
| Shallow size (avg) | total ÷ count | Identifies size variance within a type |
| Estimated retained size | Bounded BFS (§4.1) | Conservative upper bound |
| GC generation distribution | `ClrSegment` correlation | Gen0 / Gen1 / Gen2 / LOH % breakdown per type |
| Is finalizable | `ClrType.IsFinalizable` | Finalizable types incur two-pass collection cost |
| Is value type | `ClrType.IsValueType` | Boxed value types inflated on heap |
| Is array | `ClrType.IsArray` | Component element type and rank |
| Base type chain depth | `ClrType.BaseType` traversal | Deep inheritance trees (> 8 levels) flagged |
| Interface count | `ClrType.Interfaces` | High counts indicate heavy abstraction overhead |
| Field count (ref / value) | `ClrType.Fields` | Feeds §3.3 shape analysis |
| Module | `ClrType.Module.Name` | Owning assembly |
| Method table address | `ClrType.MethodTable` | For cross-referencing with native tooling |

---

## 🔹 3.2 Dominator Candidates
### 📦
Candidates selected by combining signals from `ClrType`, heap index, and generation data:
- **Criteria for nomination**:
  1. Type total size > 1 % of total heap (`ClrObject.Size` sum)
  2. OR type is predominantly Gen2 (> 80 % of instances in Gen2)
  3. OR type has `IsFinalizable = true` and instance count > 500
  4. OR type is a well-known container (`Dictionary`, `List`, `ConcurrentQueue`, arrays) with total size > 50 MB
- **Per candidate**:
  - Instance count and total shallow size
  - Largest single instance: address + size (`ClrObject.Size`)
  - Gen2 % of instances
  - Estimated retained size (bounded BFS from §4.1)
  - Whether any instance is directly reachable from a GC root (`ClrHeap.EnumerateRoots()`)
- Top 30 candidates ranked by estimated retained size

---

## 🔹 3.3 Object Shape Analysis
### 📦
Derived by walking `ClrType.Fields` for each type in the index:
- **Reference field count** vs **value-type field count** per type
- Classification: `ReferenceHeavy` (≥50 % ref fields), `ValueHeavy` (0 ref fields), `Mixed`
- **Pure value containers**: types with zero reference fields (safe for stack allocation / pooling)
- **Oversized value types**: structs with unexpectedly large shallow size (boxing pressure candidates)
- Top 20 types ranked by reference field density (reference field count ÷ total field count)

---

## 💡 Purpose
Identify structural memory issues.

---

# 🔗 4. Retention & Dominator Analysis

## 🔹 4.1 Retention Hotspots
### 📦
Scoped to top-N suspicious types by total shallow size (from heap index):
- Per-type **estimated retained size**: sum of shallow sizes of all objects reachable exclusively through a given candidate, computed via bounded BFS using `ClrObject.EnumerateReferences()`
- **Retention ratio**: retained size ÷ shallow size — high ratios flag objects holding large sub-graphs
- Top 20 candidates ranked by retention ratio
- Breadth limit: 10 000 objects per candidate; depth limit: 20 hops (safe for large dumps)

---

## 🔹 4.2 Dominator Tree (Approx)
### 📦
Lightweight approximation — full Lengauer-Tarjan is unsafe for 25 GB+ dumps:
- For each candidate from 4.1, perform a bounded BFS and collect all exclusively-reachable objects
- **Exclusive retained bytes**: memory freed if this object were removed (objects not reachable via any other live path)
- **Dominator impact score** = exclusive retained bytes ÷ total heap size × 1000 (per-mille)
- Top 15 dominators ranked by exclusive retained bytes
- Candidates with overlapping reachable sets are flagged as **shared dominators** (co-retention)

> ⚠️ True dominance requires the full object graph. These are conservative approximations scoped to bounded traversals.

---

## 🔹 4.3 Retention Patterns
### 📦
Pattern-matched from retention hotspot results and cross-referenced with analyzer outputs:
- **Cache chains**: `Dictionary` / `ConcurrentDictionary` → value chain with Gen2 objects
- **Event chains**: `EventHandler` / `Delegate` target lists retaining subscriber graphs (from `EventLeakAnalyzer`)
- **Static chains**: static field root → long object chain (from `StaticRootLeakDetector`)
- **Thread-local chains**: `ThreadLocal<T>` or per-thread state holding Gen2 objects
- **Finalizer chains**: objects in the finalizer queue retaining large sub-graphs
- Each pattern includes the root type, depth of chain, and total retained bytes

---

## 💡 Purpose
Explain *why memory is not being freed*.

---

# 🌳 5. GC Root Intelligence

## 🔹 5.1 Root Distribution
### 📦
Aggregated across ALL root kinds via `ClrHeap.EnumerateRoots()` and `ClrRuntime.EnumerateHandles()`:

| Root Kind | Source API | Shallow Size Retained |
|---|---|---|
| Static fields | `ClrStaticField` | ✅ |
| Stack variables | `ClrRoot` (stack) | ✅ (shallow) |
| Strong GC handles | `ClrHandle` (Strong, Pinned, RefCounted) | ✅ |
| Weak GC handles | `ClrHandle` (Weak, WeakLong) | counted only |
| Finalizer queue | `ClrRoot` (finalizer) | ✅ |
| Dependent handles | `ClrHandle` (Dependent) | ✅ |

- Total memory retained per root kind (bar chart–friendly)
- Root kind count distribution

---

## 🔹 5.2 Root Severity Ranking
### 📦
- Top 20 individual roots ranked by shallow size of their directly reachable objects
- Each entry: root kind, declaring type / field name, object type at root, retained bytes
- **Severity tier**:
  - 🔴 Critical — root retains > 100 MB
  - 🟠 Warning — root retains 10–100 MB
  - 🟡 Info — root retains < 10 MB
- Finalizer roots with large retained sets are flagged separately (finalizer thread starvation risk)

---

## 🔹 5.3 Root Paths
### 📦
Computed by `BoundedRootPathFinder` (BFS, depth ≤ 20, visited `HashSet<ulong>`):
- Root → object chains for top suspicious types from Section 6.1 (leak candidates)
- Each path: `[RootKind] RootType.FieldName → TypeA → TypeB → ... → LeakCandidate`
- Max 3 paths per type (shortest paths prioritised)
- Paths that pass through shared infrastructure (e.g., `object[]`, `List<T>`) are annotated as **indirect**
- Truncated paths (depth limit reached) are marked `[TRUNCATED]`

---

## 💡 Purpose
Trace retention to actual causes.

---

# 🧪 6. Memory Leak Analysis

## 🔹 6.1 Leak Candidates
### 📦
Scoring model — each type receives a **suspicion score** (0–100) from combined signals:

| Signal | Score Contribution | ClrMD Source |
|---|---|---|
| > 80 % instances in Gen2 | +30 | `ClrSegment` generation correlation |
| Type total size > 100 MB | +20 | Heap index aggregate |
| Instance count growing (trend mode) | +15 | `TrendAnalyzer` delta |
| `IsFinalizable` + Gen2 count > 1 000 | +15 | `ClrType.IsFinalizable` |
| Reachable from static root | +10 | `ClrStaticField` traversal |
| Reachable from GC handle (Strong/Pinned) | +10 | `ClrRuntime.EnumerateHandles()` |
| Is a known container type | +5 | Type name pattern |
| High reference field density (§3.3) | +5 | `ClrType.Fields` |

- Top 30 types ranked by suspicion score
- Each entry: type name, score, total size, instance count, Gen2 %, root kind if found

---

## 🔹 6.2 Leak Classification
### 📦
Classification is assigned per leak candidate based on its root path and structural patterns:

| Class | Detection Method | Pattern |
|---|---|---|
| `StaticRetention` | `ClrStaticField` root → candidate reachable | Static field holds reference to growing container |
| `EventLeak` | `Delegate._invocationList` chain → candidate reachable | Publisher alive, subscriber not disposable |
| `CacheLeak` | Known cache type (`Dictionary`, `MemoryCache`, `ConcurrentDictionary`) in Gen2 with no eviction | Container grows unbounded |
| `ThreadLocalLeak` | `ThreadLocal<T>._linkedSlot` chain → candidate | Thread-static or `ThreadLocal` holding Gen2 objects |
| `FinalizerRetention` | Candidate in finalizer queue retaining sub-graph | Object awaiting finalization keeping large graph alive |
| `GCHandleRetention` | `ClrHandle` (Strong/Pinned/RefCounted) → candidate | Explicit handle preventing collection |
| `DependentHandleLeak` | `ClrHandle` (Dependent) source alive, target grown | Conditional weak table keeping target alive through source |
| `Unknown` | Reachable from root but pattern unrecognised | Manual investigation required |

---

## 🔹 6.3 Leak Explanation
### 📦
Per candidate, a structured natural-language explanation assembled from:
- **Root cause sentence**: "Type `X` is retained by a static field `Y.Z` of type `Dictionary<string, X>`. The dictionary has `N` entries and has been accumulating since process start with no eviction logic."
- **Evidence list**: root kind, root declaring type, root field name, retention path depth, total retained bytes
- **Corroborating signals**: finalizer queue presence, high Gen2 %, thread-static involvement
- Template-based generation — one template per leak classification from §6.2, parameterised with actual type/field names resolved via `ClrType.Name`, `ClrField.Name`, `ClrStaticField.Name`

---

## 🔹 6.4 Leak Impact
### 📦
- **Memory impact**: total shallow size + estimated retained size; % of total heap
- **GC impact**: finalizable leak candidates force two-pass collection — estimated extra GC work per collection cycle
- **Fragmentation impact**: Gen2 accumulation blocks compaction; LOH leaks directly fragment LOH
- **Thread impact**: finalizer queue backlog from `IsFinalizable` leaks can starve the finalizer thread
- **Process stability risk**: classification — `Low` (< 50 MB retained), `Medium` (50–500 MB), `High` (500 MB–2 GB), `Critical` (> 2 GB)

---

## 💡 Purpose
Identify and explain memory leaks clearly.

---

# 🧵 7. Thread & Concurrency Analysis

## 🔹 7.1 Thread Lifecycle
### 📦
- Total / alive / inactive / background thread counts
- GC thread count and finalizer thread status (blocked / not blocked)
- Async chain threads (`AsyncChainThreadCount`) and max async chain depth
- **Thread pool state** (via `ClrRuntime.ThreadPool`):
  - `MinThreads` / `MaxThreads` (configured limits)
  - `ActiveWorkerThreads` / `IdleWorkerThreads` / `RetiredWorkerThreads`
  - `QueueLength` (pending work items — starvation signal when near `MaxThreads`)
  - `CpuUtilization` %
  - ⚠️ Starvation flag: `QueueLength > 0` AND `ActiveWorkerThreads == MaxThreads`
- Per-thread stack size: `ClrThread.StackBase - ClrThread.StackLimit` — oversized stacks flag stack overflow risk

---

## 🔹 7.2 Synchronization Patterns
### 📦
- Wait pattern breakdown by category: `MonitorWait`, `MonitorContention`, `TaskBlocking`, `Sleep`, `Semaphore`, `Mutex`, `WaitHandle`, `ThreadJoin`, `BlockingIO`
- Top 10 blocked threads with: OS thread ID, wait category, wait reason, lock count, top stack frame
- Top 10 lock-holding threads with: lock count, GC mode, top frames
- **Frame hotspots**: top 10 stack frames most frequently appearing across all blocked threads (aggregated frame frequency)
- `GcMode` distribution: Cooperative vs Preemptive — threads stuck in Cooperative mode block GC

---

## 🔹 7.3 Deadlock Detection
### 📦
Via `LockGraphAnalyzer` — builds a directed wait-for graph from `ClrThread.BlockingObjects`:
- Thread → lock → owner thread edges
- Cycle detection (DFS over wait-for graph)
- Each detected cycle: full thread chain with lock addresses and owning type names
- **Suspected deadlock** (no confirmed cycle but mutual lock holding): two or more threads each holding a lock the other is waiting for

---

## 💡 Purpose
Diagnose concurrency issues affecting memory and performance.

---

# ⚡ 8. Async & Task Analysis

## 🔹 8.1 Task Summary
### 📦
Scanned by `HangAnalyzer` via `Task` and `Task<T>` object inspection on heap:
- Total `Task` objects on heap
- Status breakdown: `Pending` / `Running` / `Faulted` / `Canceled` / `RanToCompletion`
- `QueuedWorkItems` count from `ClrRuntime.ThreadPool.QueueLength`
- `TotalTaskContinuations`: sum of non-null `m_continuationObject` fields
- `RuntimeThreadPoolDataAvailable` flag — surfaces when thread pool introspection is incomplete

---

## 🔹 8.2 Orphaned Tasks
### 📦
Detected by field inspection on `Task` objects (`m_continuationObject == null`, status = `RanToCompletion` or `Faulted`):
- **Faulted + no continuation**: task threw an exception that was never observed — silently swallowed
- **Completed + no continuation + not in any thread's stack roots**: fire-and-forget task (no await)
- Count and top types of objects held by orphaned faulted tasks
- Top 10 orphaned faulted tasks: address, exception type, exception message

---

## 🔹 8.3 Continuation Chains
### 📦
Chain depth computed by following `m_continuationObject` references on `Task` objects:
- `MaxAsyncChainDepth`: deepest continuation chain found
- `AsyncChainThreadCount`: threads participating in async chains
- Top 5 deepest chains: root task type → continuation type sequence
- Deep chains (depth > 50) flag `async` state machine leaks or unbounded continuations
- `TopContinuationTypes`: most common types appearing as continuation targets (from `HangDomainResult`)

---

## 💡 Purpose
Understand async behavior and hidden retention.

---

# 🧷 9. GC & Allocation Pressure

## 🔹 9.1 Allocation Patterns
### 📦
Derived from generation distribution of all heap objects (`ClrSegment` correlation per object):
- **Short-lived objects** (Gen0): object count and total size; top 10 types by Gen0 count
- **Medium-lived objects** (Gen1): surviving at least one GC cycle; top 10 types by Gen1 count
- **Long-lived objects** (Gen2 / LOH): accumulated survivors; top 10 types by Gen2 count and size
- **Survival ratio per type**: Gen2 count ÷ total count — values near 1.0 indicate permanent allocation
- **Allocation pressure indicator**: ephemeral segment fill % (`ClrSegment.IsEphemeral`, `CommittedMemory ÷ Length`) — above 80 % means the next Gen0 GC is imminent
- **Allocation density by type**: objects per KB of total size — high density = small objects allocated in volume (pressure on allocator)
- Object count histogram by size bucket: < 64 B / 64–256 B / 256 B–1 KB / 1–85 KB / > 85 KB (LOH)

---

## 🔹 9.2 GC Efficiency
### 📦
- **Promotion rate per type**: Gen1 count ÷ (Gen0 + Gen1 + Gen2) count — high promotion = types surviving too many GCs
- **Gen2 accumulation rate**: Gen2 count ÷ total count per type — high values flag permanent allocations
- **Finalizable object overhead**: count of `IsFinalizable` types in Gen2 — each requires two GC passes to collect; total size of all finalizable Gen2 objects
- **Segment utilisation**: per segment, `UsedBytes ÷ CommittedMemory` — under-utilised segments indicate wasted committed memory
- **Segment committed vs reserved gap** (`ClrSegment.CommittedMemory` vs `ClrSegment.ReservedMemory`): large gaps indicate virtual memory reserved but not yet paged in — relevant for address space pressure on 32-bit hosts
- **Cross-heap object distribution** (Server GC): object count and bytes per logical heap index — skew > 2× across heaps indicates thread affinity problems
- **Heap compaction blockage signals**: pinned handle count (from §9.3) + POH object count (from §10.4) — higher combined values mean more segments cannot be compacted

---

## 🔹 9.3 Pinning Impact
### 📦
Derived from `GCHandleAnalyzer` (`ClrHandle` of kind `Pinned`) and `SegmentAnalyzer`:
- Total pinned handle count and top pinned target types
- **Gen0/Gen1 pinned objects**: pinned objects residing in ephemeral segments — most disruptive to compaction; detected by correlating `ClrHandle` address with `ClrSegment` generation
- **Pinned object clustering**: are pinned objects concentrated in few segments (low impact) or spread across many (high fragmentation impact)?
- **POH vs GC-handle pinning**: objects in the Pinned Object Heap (allocated with `GC.AllocateArray<T>(size, pinned: true)`) vs objects pinned ad-hoc via `GCHandle.Alloc(..., GCHandleType.Pinned)`
- Fragmentation attributable to pinning: estimated gap bytes caused by pinned objects blocking compaction

---

## 💡 Purpose
Evaluate runtime efficiency.

---

# 🔥 10. LOH / POH / FOH Diagnostics

## 🔹 10.1 LOH Summary
### 📦
- Total LOH size, segment count, object count
- Top LOH types by size and count (from `GCGenerationDomainResult.TopLohTypes`)
- LOH threshold: default 85 000 bytes — flag types just over threshold that could be redesigned to avoid LOH
- LOH objects by GC generation (LOH is always Gen2; flag any incorrectly categorised entries)

---

## 🔹 10.2 Fragmentation
### 📦
- Per-segment fragmentation %: `FreeBytes ÷ TotalBytes × 100` (from `LohFragmentationDomainResult`)
- Free block count and largest free block size
- Top 5 most fragmented LOH segments by address, free bytes, largest contiguous free block
- **Fragmentation severity**: 🔴 > 60 %, 🟠 30–60 %, 🟢 < 30 %

---

## 🔹 10.3 Large Object Lifetimes
### 📦
- Long-lived LOH allocations (Gen2 objects, no finalizer — likely permanent residents)
- Top 10 largest individual LOH objects: address, type, size
- Arrays > 1 MB: element type, length, size — common source of LOH pressure

---

## 🔹 10.4 POH Diagnostics
### 📦
Pinned Object Heap — holds objects allocated with `GC.AllocateArray<T>(pinned: true)` (.NET 5+):
- POH segment count, total size, object count (via `HeapSegmentKind.PinnedObjectHeap`)
- Top POH types by size
- POH objects are never compacted and never promoted — flag long-lived POH objects that are no longer referenced by native code
- Comparison: POH size vs GC-handle-pinned size (from section 9.3) to identify which pinning strategy dominates

---

## 🔹 10.5 FOH Diagnostics
### 📦
Frozen Object Heap — holds immutable objects that will never be collected:
- FOH segment count, total size, object count (via `HeapSegmentKind.Frozen`)
- Top FOH types by count (typically `System.String`, `System.Byte[]`, `FrozenDictionary` internals)
- Unexpectedly large FOH size may indicate over-use of `RuntimeHelpers.GetUninitializedObject`, `MemoryMarshal`, or third-party frozen collection libraries
- FOH is informational (no GC cost) but contributes to total process address space

---

## 💡 Purpose
Detect large memory inefficiencies across all non-compactable heap regions.

---

# 🧬 11. String & Data Analysis

## 🔹 11.1 Duplicate Strings
### 📦
String objects located via `ClrType.IsString` during heap scan; deduplication by value hash:
- Total `System.String` object count and total bytes (`ClrObject.Size` sum)
- Unique string count (after value deduplication)
- **Duplication ratio**: `(total count - unique count) ÷ total count` — values > 0.5 indicate heavy redundancy
- Top 20 most-duplicated string values: preview (first 80 chars), duplicate count, wasted bytes
- Wasted bytes = `(count - 1) × ClrObject.Size` per string value
- **String length histogram**: < 16 chars / 16–64 / 64–256 / 256–1 KB / 1–85 KB / > 85 KB (LOH)
- **Very long strings** (> 85 KB): individual entries with address, length, size — these are LOH residents
- **Interned strings** (live in FOH): count and total size; over-use of `string.Intern()` bloats FOH permanently since interned strings are never collected
- **Strings in Gen2**: count and size — strings that have survived multiple GC cycles without being collected

---

## 🔹 11.2 Memory Waste & Optimisation Potential
### 📦
- **Total duplicate waste**: sum of wasted bytes across all duplicated string values — potential saving if `string.Intern()` or a string pool were applied
- **LOH string pressure**: total size of strings > 85 KB — candidates for `StringBuilder`, `ReadOnlyMemory<char>`, or chunked storage
- **Encoding waste detection**: strings containing only ASCII characters stored as UTF-16 — potential saving by switching to `byte[]` / `Utf8JsonWriter` patterns where applicable
- **Potential saving from `string.Intern()`**: estimated bytes recoverable if top-20 duplicate strings were interned (with caveat: interned strings never collected — see §10.5)
- **Recommended approach per finding**:
  - High duplicate count, short strings → `string.Intern()` or custom string pool
  - High duplicate count, long strings → cache single canonical instance
  - Very long strings in LOH → `ReadOnlyMemory<char>` slicing, avoid materialising full string
  - Strings in Gen2 with high count → review caching logic holding references

---

## 💡 Purpose
Optimize data usage.

---

# 🔗 12. Event & Delegate Analysis

## 🔹 12.1 Subscription Graph
### 📦
Constructed by walking `Delegate` and `MulticastDelegate` field layouts via `ClrType.Fields`:
- **`_target` field** (`ClrInstanceField` on `Delegate`): the object the delegate is bound to — the subscriber
- **`_invocationList` field** (`ClrInstanceField` on `MulticastDelegate`): `object[]` array holding all subscribers for a multicast event
- **`_invocationCount` field**: number of subscribers attached to a multicast delegate
- For each event field found on heap objects:
  - Publisher type and address
  - Subscriber count (`_invocationCount`)
  - Top subscriber types by frequency
  - Total shallow size of all subscriber objects reachable from `_invocationList`
- **Subscription depth**: `_invocationList` arrays containing other `MulticastDelegate` entries (nested multicast) — uncommon but possible
- Top 20 publisher types by total subscriber count

---

## 🔹 12.2 Event Leaks
### 📦
Leak detected when: publisher is alive (reachable from GC root) AND subscriber's `_target` is in Gen2 with no other strong root:
- **Retained subscriber count**: number of subscriber objects kept alive only via the delegate chain
- **Retained subscriber bytes**: `ClrObject.Size` sum of objects exclusively reachable through `_target` fields
- **Publisher lifetime**: is publisher itself short-lived (Gen0/1) or long-lived (Gen2/static)? Long-lived publishers with short-lived subscribers are the primary leak pattern
- **Static event fields**: events declared as `static` fields on a class — any subscriber added to a static event is retained indefinitely; detected via `ClrStaticField` scan + type name containing `EventHandler`
- **`EventHandler<T>` vs `Action` vs custom delegate**: type name classification to distinguish .NET event pattern from ad-hoc delegates
- Per publisher type: event field name, subscriber count, retained bytes, leak severity

---

## 💡 Purpose
Detect subtle memory leaks.

---

# 🧾 13. Exception Analysis

## 🔹 13.1 Exception Frequency
### 📦
- Most common exceptions

---

## 🔹 13.2 Failure Hotspots
### 📦
- **Exception-specific frame hotspots**: top N stack frames aggregated exclusively across threads with active exceptions (reuses `TopFrameHotspots` pattern from `ThreadAnalyzer`, scoped to exception threads)
  - Example: `JsonSerializer.Deserialize` appearing in 14 / 20 exception threads → serialization is the failure origin
- Top 10 frames by exception-thread frequency: frame name, exception type most associated, occurrence count
- **Exception origin classification**:
  - `UserCode` — frame in a non-system assembly
  - `FrameworkCode` — frame in `System.*` / `Microsoft.*`
  - `ThirdParty` — frame in other assemblies (resolved via `ModuleAnalyzer` loaded module list)
- InnerException chain depth histogram — deep chains (> 5 levels) obscure root cause

---

## 💡 Purpose
Correlate failures with memory issues.

---

# 🔁 14. Temporal / Diff Analysis

## 🔹 14.1 Growth Trends
### 📦
Powered by `TrendAnalyzer` + `AnalyzerTrendComparers` + `TrendReportComposer`:
- Per-type object count delta and byte delta between two dump snapshots
- Types growing fastest by byte delta (top 20)
- Types growing fastest by count delta (top 20) — count growth without byte growth indicates shrinking objects or pooling changes
- New types present in snapshot B but absent in snapshot A (newly introduced code paths)
- **Growth rate classification**: Stable (< 5 % delta), Growing (5–50 %), Exploding (> 50 %)

---

## 🔹 14.2 Regression Detection
### 📦
- Types that were absent or negligible in snapshot A but are leak candidates in snapshot B (cross-referenced with Section 6.1)
- `FindingLifecycleComparer`: findings present in B but not in A — new regressions
- Findings present in A but not in B — resolved issues (surfaced as positive signal)
- Severity escalations: findings that existed in both but moved from Warning → Critical between snapshots
- **Requires**: two separate analysis runs with `--compare` mode; single-dump runs skip this section

---

## 💡 Purpose
Track memory evolution over time.

---

# 📊 15. Visualization

## 📦
Data-export layer — all structured data from sections 1–14 and 18–25 is available as machine-readable output to feed visualisation tools:
- **Memory pie/bar charts**: SOH / LOH / POH / FOH proportions, Gen0/1/2 distribution (§2.1, §2.2)
- **Type treemap**: nested rectangles sized by retained bytes per type (§3.1, §4.1)
- **Retention graph**: directed object graph for top dominator candidates, exportable as Graphviz `.dot` or JSON adjacency list (§4.2)
- **Thread timeline**: thread states (blocked / running / waiting) laid out horizontally per thread ID (§7)
- **LOH fragmentation heatmap**: segment address range with free blocks overlaid (§10.2)
- **Leak score bar chart**: suspicion scores per candidate type (§6.1)
- **Diff waterfall chart**: byte delta per type between two snapshots (§14.1)

> ⚠️ ClrMD provides the raw data; rendering requires a separate UI or charting layer (e.g., a web report, DGML, or exported CSV for Excel). The CLI report emits all structured values as JSON alongside the human-readable output.

---

## 💡 Purpose
Improve interpretability and enable tooling integration.

---

# 🤖 16. Insights & Recommendations

## 🔹 16.1 Findings
### 📦
Emitted by `InsightEngine` — cross-cutting pattern detection across all `AnalyzerRunResult[]`:
- Ranked by severity: 🔴 Critical → 🟠 Warning → 🔵 Info
- Each finding: `Source`, `Title`, `Detail`, `Severity`, `ConfidenceScore` (0.0–1.0), `Caveats[]`
- Cross-analyzer correlations surfaced (e.g., high LOH % + high pinned handle count → compaction blocked)
- Analyzer failure count flagged (≥ 3 failed analyzers = Warning finding)

---

## 🔹 16.2 Root Cause Narratives
### 📦
Generated for each Critical/Warning finding by correlating evidence from multiple sections:
- **Cause**: the specific pattern detected (e.g., static `Dictionary<string, T>` growing unbounded)
- **Effect**: measured impact (e.g., 4.2 GB retained, Gen2 promotion spike, GC pause increase)
- **Evidence chain**: list of contributing findings with section references (e.g., §6.1 leak candidate + §5.1 static root + §4.1 retention hotspot)
- **Confidence**: derived from `ConfidenceScore` — High (≥ 0.8), Medium (0.5–0.8), Low (< 0.5)

---

## 🔹 16.3 Suggested Fixes
### 📦
Per-finding actionable remediation:
- `Cache leak` → add eviction policy (`MemoryCache` with size limit, `WeakReference` values)
- `Event leak` → unsubscribe in `Dispose`, use `WeakEventManager`, or switch to `IObservable`
- `Static root` → review static field lifetime; consider scoped DI registration
- `LOH pressure` → pool large arrays with `ArrayPool<T>`, use `RecyclableMemoryStream`
- `Thread pool starvation` → avoid `.Result` / `.Wait()`, use `async`/`await` throughout
- `Pinning fragmentation` → migrate to POH (`GC.AllocateArray<T>(pinned: true)`) or `MemoryPool<T>`
- `Finalizer backlog` → implement `IDisposable`, call `GC.SuppressFinalize`
- Each fix includes a difficulty rating: Easy / Medium / Hard

---

## 💡 Purpose
Turn analysis into action.

---

# 🧾 17. Confidence & Limitations

## 📦

### Confidence Scores
Each `InsightFinding` carries a `double ConfidenceScore` (0.0–1.0) and a `string[] Caveats` array:
- **1.0** — directly measured (e.g., confirmed GC root via `ClrHeap.EnumerateRoots()`)
- **0.8** — high-confidence heuristic (e.g., static field retaining Gen2 object chain)
- **0.5** — moderate heuristic (e.g., type name pattern match for cache detection)
- **< 0.5** — speculative (e.g., allocation pattern classification from Gen0/Gen2 ratio alone)

### Per-Analyzer Status
Each `AnalyzerRunResult` records:
- `Status`: Completed / Failed / Skipped / TimedOut
- `ElapsedMs`: wall-clock time
- `ObjectsScanned`: heap objects processed
- `SkipReason` / `ErrorMessage` if not completed

A summary table of all 16 analyzers and their run status is printed at the end of the report.

### Known Heuristic Limitations
| Limitation | Affected Sections |
|---|---|
| Retained size is bounded BFS approximation, not true dominator | §3.1, §4.1, §4.2 |
| Allocation sites unavailable from `.dmp` (require ETW) | §2.3 |
| Task orphan detection relies on field name stability across CLR versions | §8.2 |
| FOH/POH sizes include runtime-internal objects not controllable by user code | §10.4, §10.5 |
| `ClrThread.StackBase/StackLimit` may be 0 for GC/finalizer threads | §7.1 |
| Deadlock detection is best-effort; cooperative waits without `BlockingObjects` are missed | §7.3 |

---

## 💡 Purpose
Ensure transparency and trust.

---

# 🚀 Summary

This report:
- Explains problems deeply
- Identifies root causes
- Suggests fixes

👉 Suitable for:
- Production debugging
- Performance audits
- Senior-level diagnostics

---

# 🏠 18. AppDomain & Assembly Analysis

## 🔹 18.1 AppDomain Inventory
### 📦
Via `ClrRuntime.AppDomains` — each `ClrAppDomain` exposes:
- Domain name, address, and numeric ID
- Module count: number of assemblies loaded into this domain
- **Module list** per domain: assembly name, path, size (`ClrModule.Size`), dynamic flag (`ClrModule.IsDynamic`), PE file flag (`ClrModule.IsPEFile`)
- Total managed memory attributable to types defined in modules loaded per domain (cross-reference with heap index by `ClrType.Module`)
- Multi-domain presence: flag any type loaded in more than one domain (name collision risk; relevant in plugin/MEF scenarios and legacy .NET Framework hosts)

---

## 🔹 18.2 Assembly Version Conflicts
### 📦
- Multiple `ClrModule` instances with the same `AssemblyName` but different `FileName` or `MetadataToken` — assembly binding redirect failures or side-by-side load
- Groups: assembly name → list of conflicting module instances with paths and addresses
- Dynamic assemblies (`ClrModule.IsDynamic = true`): generated at runtime (e.g., Roslyn, `Emit`, `System.Reflection.Emit`) — these are never unloaded and accumulate in memory; total dynamic module count and size
- Anonymous hosted modules (no file path) — indicate in-memory-only code generation

---

## 🔹 18.3 Type Density per Module
### 📦
Via `ClrModule.EnumerateTypes()` — iterate all defined types per module:
- Type count per module (unique `MethodTable` count)
- Modules with unexpectedly high type counts (> 5 000 types) — indicators of source generators, AOT precompilation, or reflection-heavy frameworks
- Modules contributing the most heap objects (cross-reference type list with heap index)
- **Heap footprint per module**: sum of `ClrObject.Size` for all instances whose `ClrType.Module` matches
- **Type-to-object ratio**: a module defining 10 000 types with only 20 live instances is purely loaded overhead vs one defining 50 types with 500 000 instances

---

## 💡 Purpose
Understand assembly loading strategy, identify plugin/isolation issues, and attribute heap memory back to originating assemblies.

---

# ⚙️ 19. JIT & Native Code Footprint

## 🔹 19.1 JIT Heap Usage
### 📦
Via `ClrRuntime.GetJitManagers()` — one JIT manager per code heap:
- Total JIT code heap size (bytes committed to native machine code)
- Number of JIT managers (typically 1 for workstation, higher with tiered compilation or ReadyToRun)
- JIT heap as % of total process memory — unexpectedly large JIT heap indicates excessive dynamic code generation or many loaded assemblies

---

## 🔹 19.2 Compiled Method Analysis
### 📦
Via `ClrStackFrame.Method` across all thread stacks (methods currently executing or on call stacks):
- **Active method hotspot map**: methods appearing on the most thread stacks simultaneously — identifies hot paths at the moment of the dump
- Per method: `ClrMethod.Signature` (full signature), declaring `ClrType.Name`, `NativeCode` address
- **Native code range size**: `ClrMethod.HotColdInfo` — hot region size + cold region size; large methods (> 64 KB native) flag JIT bloat
- **Unmanaged frame ratio**: `ClrStackFrame.Kind` distribution (Managed vs Runtime vs Unmanaged) per thread — high unmanaged ratio indicates heavy P/Invoke or COM interop

---

## 🔹 19.3 Tiered Compilation & ReadyToRun
### 📦
- Methods with multiple `NativeCode` addresses (tiered: Tier0 → Tier1) — detected when the same `MetadataToken` maps to two address ranges
- ReadyToRun pre-compiled methods (from `ClrModule.IsPEFile` + R2R header presence): these have native code in the PE image, not the JIT heap
- Methods that have NOT been JIT-compiled despite being called (Tier0 stubs): identifiable when `NativeCode == 0` on a frame currently on stack

---

## 💡 Purpose
Understand the cost of loading and compiling managed code, detect JIT heap bloat, and identify hot paths captured at dump time.

---

# 📦 20. Boxing & Value Type Pressure

## 🔹 20.1 Boxed Value Type Inventory
### 📦
Boxing detected by finding heap objects whose `ClrType.IsValueType = false` but whose `ClrType.BaseType` is `System.ValueType` or `System.Enum`, or via `ClrObject.AsBoxedValue()`:
- Total boxed object count and size
- Top 20 value types most frequently boxed: type name, box count, total box size
- **Boxed enums**: `ClrType.IsEnum = true` on the inner type — extremely common anti-pattern in logging, dictionary keys, and comparison
- **Boxed structs in collections**: `object[]` or `IEnumerable<object>` holding value-type boxes — flag `List<object>`, `ArrayList`, `Hashtable` containing struct instances
- Per type: is the struct oversized? Structs > 16 bytes on heap are boxing candidates that should be classes or pooled

---

## 🔹 20.2 Value Type Shape Issues
### 📦
Via `ClrType.Fields` on value types found in heap index:
- **Mutable reference-containing structs**: value type with one or more `IsObjectReference = true` fields — can cause unexpected aliasing and GC write barrier costs
- **Struct field padding waste**: sum of `ClrInstanceField.Offset` gaps vs total field sizes — excessive padding inflates struct size (particularly costly in large arrays of structs)
- **Large structs passed by value**: structs > 64 bytes frequently on the stack (via `ClrThread.EnumerateStackObjects()` where `ClrType.IsValueType = true`) — should be passed `ref` or replaced with classes
- Top 10 oversized value types by `ClrType.StaticSize`

---

## 💡 Purpose
Eliminate unnecessary heap allocations from boxing and identify struct layout inefficiencies that drive GC pressure and cache misses.

---

# ☠️ 21. Finalizable Object Lifecycle

## 🔹 21.1 Finalizable Object Population
### 📦
All heap objects where `ClrType.IsFinalizable = true`, regardless of finalizer queue status:
- Total count and size of all finalizable objects across heap
- **By generation**: Gen0 / Gen1 / Gen2 / LOH — finalizable objects in Gen2 are the most expensive (require two collection cycles to free: first to queue them, second to collect after `Finalize()` runs)
- Top 20 finalizable types by Gen2 count and size
- `IsFinalizable` types that implement `IDisposable` but whose `Dispose()` was never called (heuristic: presence in finalizer queue + `_disposed` field = `false` if field exists)

---

## 🔹 21.2 Finalizer Queue Analysis
### 📦
Objects currently in the finalizer queue (reachable from the finalizer root in `ClrHeap.EnumerateRoots()` where `RootKind = Finalizer`):
- Finalizer queue depth (total count)
- Top types in finalizer queue ranked by count and size
- **Finalizer queue backlog severity**: 🔴 > 10 000 objects, 🟠 1 000–10 000, 🟢 < 1 000
- Finalizer queue objects retaining large sub-graphs (estimated retained bytes via bounded BFS — these objects block collection of everything they reference until finalized)
- **Resurrection detection**: finalizable objects that re-register themselves in `Finalize()` (`GC.ReRegisterForFinalize`) — these cycle through the finalizer queue indefinitely

---

## 🔹 21.3 Finalizer Thread Health
### 📦
Via `ClrThread` where `IsFinalizer = true`:
- Finalizer thread alive status and OS thread ID
- Finalizer thread blocked: `LockCount > 0` or current frame matching a known wait pattern
- If blocked: what is the blocking frame? (`ClrStackFrame.FrameName` on finalizer thread)
- Estimated finalizer throughput: impossible from a single snapshot, but a large queue + blocked finalizer thread = **confirmed starvation**
- Finalizer frames: full stack trace of the finalizer thread at the moment of the dump

---

## 💡 Purpose
Detect finalizer starvation, resurrection patterns, and the hidden GC cost of types that implement finalizers without proper `Dispose` usage.

---

# 🧮 22. Array Deep Analysis

## 🔹 22.1 Array Population Overview
### 📦
All heap objects where `ClrType.IsArray = true`:
- Total array object count and combined size
- **By element type** (`ClrType.ComponentType.Name`): count and total size per element type
- **By rank** (`ClrObject.AsArray().Rank`): single-dimensional (rank 1) vs multi-dimensional (rank ≥ 2) — multi-dim arrays cannot use `Span<T>` and are slower to access
- **By generation**: Gen0 / Gen1 / Gen2 / LOH distribution
- Top 20 array types by total size

---

## 🔹 22.2 Large Array Analysis
### 📦
Arrays with `ClrObject.Size > 85 000` (LOH residents) and notable arrays up to 1 MB:
- Individual large arrays: address, element type, length (`ClrObject.AsArray().Length`), size
- **Oversized array anti-patterns**:
  - `byte[]` > 1 MB: network/file buffers that should use `ArrayPool<byte>` or `MemoryPool<T>`
  - `string[]` or `object[]` > 10 000 elements: large collection backing stores
  - Multi-dimensional arrays > 85 KB: should be replaced with `T[][]` (jagged) for LOH avoidance
- Top 10 largest individual array instances with address and element type

---

## 🔹 22.3 Sparse & Wasteful Arrays
### 📦
Detected by sampling element values (`ClrObject.AsArray().GetObjectValue(index)`) on a bounded subset of large arrays:
- **Null density**: ratio of null elements in reference-type arrays — arrays > 50 % null are over-allocated
- **Zero density**: value-type arrays (e.g., `int[]`, `byte[]`) that are predominantly zero — over-allocated capacity
- Top 10 sparsest arrays: address, type, length, null/zero %, wasted bytes estimate
- **Backing arrays of over-capacity collections**: `List<T>._items`, `Dictionary<K,V>._entries` with fill rate < 25 % (cross-reference with §3.3 collection analysis)

---

## 🔹 22.4 Jagged vs Multi-Dimensional
### 📦
- `T[][]` (jagged arrays): each inner array is an independent heap object — high count of small inner arrays signals better restructuring as a flat `T[]` with manual indexing
- `T[,]` or `T[,,]` (multi-dimensional): single contiguous allocation — better for cache locality but incompatible with `Span<T>`, `Memory<T>`, and `ArrayPool<T>`
- Recommendation per finding based on usage pattern

---

## 💡 Purpose
Identify array allocation anti-patterns that drive LOH pressure, fragmentation, and missed pooling opportunities.

---

# 🔄 23. Async State Machine Objects

## 🔹 23.1 State Machine Population
### 📦
Async state machines are compiler-generated classes (names matching `<MethodName>d__N` pattern or `IAsyncStateMachine` interface) allocated on heap when an `async` method hits an `await` while suspended:
- Total state machine object count and size — each represents a suspended `async` method call
- Detection: `ClrType.Interfaces` contains `System.Runtime.CompilerServices.IAsyncStateMachine` OR `ClrType.Name` matches `<.*>d__\d+` pattern
- Top 20 state machine types by count and size
- **State field analysis** (`ClrType.Fields` field named `<>1__state`): current state value indicates how deep the method is in its execution (state -1 = completed, state -2 = not started, state ≥ 0 = suspended at await N)
- Distribution of state values across all instances

---

## 🔹 23.2 Captured Closure Analysis
### 📦
Async state machines capture local variables as fields. Via `ClrType.Fields` on state machine types:
- Reference fields on state machines = captured objects that cannot be collected until the async method completes
- **Large captures**: state machine instances with total reference field shallow size > 1 MB
- **Nested captures**: state machine fields referencing other state machines (deeply nested async chains)
- **Common problematic captures**: `HttpClient`, `DbContext`, `Stream`, `ILogger` — these hold native resources and should not be captured across long `await` spans
- Top 10 state machine types by total captured reference size

---

## 🔹 23.3 Suspended Method Map
### 📦
- For each state machine type, the originating method name (decoded from the compiler-generated class name)
- Suspended methods grouped by declaring type — e.g., 150 suspended instances of `CustomerService.ProcessOrderAsync` indicates a fire-and-forget leak
- Cross-reference with `Task` objects from §8.1: state machine instances whose associated `Task` is `Faulted` but uncollected (pending finalisation or referenced by continuation)

---

## 💡 Purpose
Expose the hidden heap cost of suspended `async` methods, identify captured closures causing unexpected retention, and detect fire-and-forget async leaks.

---

# 🧩 24. Weak Reference & ConditionalWeakTable Analysis

## 🔹 24.1 Weak GC Handle Population
### 📦
Via `ClrRuntime.EnumerateHandles()` where `HandleKind` is `Weak`, `WeakLong`, or `SizedRef`:
- Total weak handle count
- **Alive vs collected targets**: for each weak handle, check if the target object is still reachable (`ClrHeap.GetObject(address).IsValid`) — a high % of dead (collected) targets means the application holds many stale `WeakReference<T>` instances
- Top 10 target types by weak handle count (what types are being weakly referenced)
- `WeakLong` handles (track objects through finalisation) vs `Weak` handles (clear before finalisation) — count and purpose distinction

---

## 🔹 24.2 `WeakReference<T>` Object Analysis
### 📦
`System.WeakReference<T>` and `System.WeakReference` objects on heap:
- Total count and size
- **Stale `WeakReference` objects**: `WeakReference` whose target has been collected but the wrapper object itself is still alive (held in a list, cache, etc.) — these are memory waste since they serve no purpose
- Detection: read `m_handle` field; if handle target is null/invalid, the `WeakReference` is stale
- Top types holding large counts of stale `WeakReference` wrappers (indicates a cache that never purges dead entries)

---

## 🔹 24.3 `ConditionalWeakTable<TKey, TValue>` Analysis
### 📦
Via `ClrRuntime.EnumerateHandles()` where `HandleKind = Dependent`:
- Total `DependentHandle` count (each entry in a `ConditionalWeakTable` is a dependent handle pair)
- Source (key) type and target (value) type per pair
- **Key→value type distribution**: top 10 most common source→target type pairs
- **Live vs dead key analysis**: dependent handles whose source key is no longer strongly reachable — these should have been cleaned up by GC but may persist if the table itself is retained
- Large `ConditionalWeakTable` instances: tables with > 10 000 entries are unusual and may indicate accumulation

---

## 💡 Purpose
Diagnose caches and extension-data patterns that rely on weak references, detect stale wrapper accumulation, and understand `ConditionalWeakTable` growth.

---

# 💾 25. Virtual Memory & Segment Reservation

## 🔹 25.1 Committed vs Reserved Memory
### 📦
Via `ClrSegment.CommittedMemory` and `ClrSegment.ReservedMemory` across all segments:
- **Total committed managed memory**: sum of `CommittedMemory` across all segments — actual physical/page-file-backed memory consumed
- **Total reserved managed memory**: sum of `ReservedMemory` — virtual address space claimed by the GC but not yet backed by pages
- **Reservation gap**: `ReservedMemory - CommittedMemory` — large gaps indicate the GC has reserved large address space ranges for future growth
- Per-segment committed vs reserved table
- Reserved-to-committed ratio: > 4× is notable; > 10× may indicate address space exhaustion risk on 32-bit processes or containers with tight `RLIMIT_AS`

---

## 🔹 25.2 Segment Lifecycle
### 📦
- Total segment count by kind: SOH ephemeral / SOH non-ephemeral / LOH / POH / FOH
- **Ephemeral segments** (`ClrSegment.IsEphemeral = true`): there is exactly one per logical GC heap; its fill % is the primary GC trigger signal
- **Non-ephemeral SOH segments**: accumulated from previous Gen2 promotions; high count indicates the heap has never been fully compacted
- Segment address ranges: `Start` to `End` — useful for detecting if managed heap is fragmented across the virtual address space (many small non-contiguous segments vs few large ones)
- **Logical heap assignment** (`ClrSegment.LogicalHeap`): which Server GC heap owns each segment — enables per-CPU heap breakdown

---

## 🔹 25.3 Address Space Pressure
### 📦
- Total virtual address space consumed by managed heap (sum of all segment reserved ranges)
- **32-bit address space exhaustion risk**: if total reserved > 1.5 GB in a 32-bit process, `OutOfMemoryException` risk is elevated even if physical RAM is available
- Fragmented address space: many small segments with large gaps between them (non-contiguous reserved ranges) increase fragmentation of the process virtual address map
- Native heap comparison: JIT code heap size (from §19.1) + managed segment reserved size + typical native heap \u2014 gives a complete picture of where process virtual memory is consumed

---

## 💡 Purpose
Understand the full virtual memory footprint of the managed heap, detect address space exhaustion risks, and explain why the process virtual size is larger than the sum of managed object sizes.

---

# 🚀 Summary

This report:
- Explains problems deeply
- Identifies root causes
- Suggests fixes

👉 Suitable for:
- Production debugging
- Performance audits
- Senior-level diagnostics

---

# 📋 Analyzer → Report Section Coverage Map

> Sections 18–25 require new analyzers not yet implemented. Existing analyzer coverage shown for reference.

| Analyzer | Primary Sections |
|---|---|
| `MemoryAnalyzer` | §1, §2.1, §3.1 |
| `GCGenerationAnalyzer` | §2.2, §9.1, §9.2, §10.1 |
| `SegmentAnalyzer` | §2.1 (FOH/POH/Server GC), §9.2, §10.4, §10.5, §25.1, §25.2 |
| `MemoryLeakAnalyzer` | §6.1–6.4, §11.1–11.2 |
| `StaticRootLeakDetector` | §4.3, §5.1–5.3, §6.2 |
| `LohFragmentationAnalyzer` | §10.1–10.3 |
| `ThreadAnalyzer` | §7.1–7.2 |
| `LockGraphAnalyzer` | §7.3 |
| `HangAnalyzer` | §7.1 (thread pool), §8.1–8.3 |
| `ThreadStackClusterAnalyzer` | §7.2 (frame hotspots) |
| `GCHandleAnalyzer` | §5.1, §9.3, §24.1 |
| `DependentHandleAnalyzer` | §5.1, §24.3 |
| `EventLeakAnalyzer` | §4.3, §12.1–12.2 |
| `CollectionAnalyzer` | §3.3, §4.3, §22.3 |
| `CrashAnalyzer` | §13.1–13.2 |
| `ModuleAnalyzer` | §13.2 (origin classification), §18.1–18.3 |
| `ReferenceChainAnalyzer` | §4.1–4.2, §5.3 |
| `InsightEngine` | §16.1–16.3 |
| `TrendAnalyzer` | §14.1–14.2 |

---

## 🆕 Sections Requiring New Analyzers

| Section | Suggested Analyzer Name | Key ClrMD APIs |
|---|---|---|
| §18.1–18.3 AppDomain & Assembly | `AppDomainAnalyzer` | `ClrRuntime.AppDomains`, `ClrModule.EnumerateTypes()`, `ClrModule.IsDynamic` |
| §19.1–19.3 JIT & Native Code | `JitAnalyzer` | `ClrRuntime.GetJitManagers()`, `ClrMethod.NativeCode`, `ClrMethod.HotColdInfo`, `ClrStackFrame.Kind` |
| §20.1–20.2 Boxing & Value Types | `BoxingAnalyzer` | `ClrObject.AsBoxedValue()`, `ClrType.IsValueType`, `ClrType.IsEnum`, `ClrInstanceField.Offset` |
| §21.1–21.3 Finalizable Lifecycle | `FinalizableObjectAnalyzer` | `ClrType.IsFinalizable`, `ClrRoot` (finalizer kind), `ClrThread.IsFinalizer` |
| §22.1–22.4 Array Deep Analysis | `ArrayAnalyzer` | `ClrType.IsArray`, `ClrObject.AsArray()`, `ClrArray.Length`, `ClrArray.Rank`, `ClrType.ComponentType` |
| §23.1–23.3 Async State Machines | `AsyncStateMachineAnalyzer` | `ClrType.Interfaces` (`IAsyncStateMachine`), `ClrType.Name` pattern, `ClrType.Fields` |
| §24.1–24.3 Weak Refs & CWT | `WeakReferenceAnalyzer` | `ClrHandle` (Weak/WeakLong/Dependent), `ClrHeap.GetObject()`, `ClrInstanceField` (`m_handle`) |
| §25.1–25.3 Virtual Memory | `SegmentReservationAnalyzer` | `ClrSegment.CommittedMemory`, `ClrSegment.ReservedMemory`, `ClrSegment.LogicalHeap` |