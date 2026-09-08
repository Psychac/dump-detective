# Full Reverse Index (Disk-Backed Parent Lookup)

## Executive Summary (Read This First)

**What:** Pre-compute a disk-backed reverse-reference index during Phase 1 heap scanning, enabling fast "who holds this object?" queries by all downstream analyzers.

**Why:** Currently, each analyzer either re-computes forward references (CPU-wasteful) or would build a full in-memory reverse graph (violates bounded-memory constraint). A shared disk-backed index costs O(1) build-time per heap scan, then pays dividends across all analyzers.

**How:** Three-phase construction (hash-partitioned extraction → per-bucket sort → cache.bin integration), then thread-safe query API. See [Design Overview](#design-overview).

**Key constraints:**
- **Hash function:** Fnv1a64 (deterministic, essential for cache reuse).
- **Fanout cap:** 10K parents per child (prevents pathological cases like interned strings).
- **Bucket count:** Formula `N = max(1, dump_size_gb / 15)` keeps per-bucket memory <600 MB.
- **Single-pass extraction:** Enumerate edges once during streaming, no re-iteration.
- **Thread safety:** Per-bucket locks, minimal contention.

**Implementation:** ~1500 LOC across 5 classes (ReverseIndexConstants, ReverseEdgeExtractor, ReverseEdgeSorter, ReverseEdgeIndexReader, CacheContainerBuilder updates).

**Timeline:** 4–5 weeks (design → implementation → testing), 2–3 weeks additional for analyzer migration.

**Success metrics:**
- Edge count matches forward-ref enumeration (±0.1%).
- Query latency p99 <50 ms.
- Truncation rate <1%.
- Cache.bin size growth <15%.
- Peak memory during sort <600 MB.

**Critical unknowns (must validate before commitment):**
1. ClrMD 4 forward-ref completeness (single pass sufficient?).
2. Hash distribution on real heap addresses (uniform?).
3. Bucket size estimation accuracy (formula correct?).
4. Query latency on real dumps (p99 <50 ms achievable?).
5. Truncation impact on leak detection (false negatives acceptable?).

See [Known Unknowns & Investigations](#known-unknowns--investigations) for detailed investigation plan. See [pre-implementation-validation.md](./pre-implementation-validation.md) for executable validation checklist with success criteria, owner assignments, and go/no-go decision matrix.

---

## Problem Statement

Current heap traversal APIs require expensive on-demand computation of "who holds this object" — answering this question means either:
1. Materialize forward references from a `ClrObject` (cheap, O(fanout) per call, but repeated across analyzers and traversals).
2. Build a full reverse graph in memory (violates bounded-memory constraint; can be billions of edges on 25GB dumps).

Multiple analyzers need to traverse the heap graph (leak detection, reference chains, retention analysis). Repeated on-demand forward-ref enumeration wastes CPU; full in-memory reverse graph breaks memory budget. The gap is a **shared, disk-backed reverse index**: precomputed in Phase 1 (during/after heap scan), then queried by all downstream analyzers in Phase 2 without re-scanning.

## Design Overview

A three-phase construction followed by a read-only query API, integrated into `cache.bin`:

1. **Phase A — Edge Extraction** (single streaming pass, hash-partitioned into scratch buckets)
2. **Phase B — Per-Bucket Sort + Directory** (in-memory sort per bucket, parallelizable)
3. **Phase C — Container Integration** (merge into `cache.bin` with directory sections)

Then:
- **Query API**: `ReverseEdgeIndexReader.GetParents(address)` — seeks + buffered read, thread-safe.

## Scope: Full Index

**No type/noise filtering during extraction.** Build a truly complete reverse index over every live object and its parents. This means:
- Edge count can be large (billions for 25GB dumps), but disk is cheap.
- Fanout skew for hot objects (interned strings, type instances, empty arrays) is real and must be handled — see [Fanout Capping](#fanout-capping).
- Analyzers can later filter at query time if they want to exclude certain types or apply special logic.

---

## Phase A — Edge Extraction

**When**: During the existing Phase 1 heap streaming pass (single scan, concurrent with `HeapStreamer`).

**Key insight**: Enumerate forward references once during streaming. Use hash-partitioning to route `(parent, child)` edges to N buckets on-the-fly, avoiding a full re-scan and keeping each bucket's raw data small enough to sort in memory later.

### Bucket Count & Sizing

Choose N such that:
- Each bucket's raw edge data fits in memory (~200–500 MB RAM per bucket during sort phase).
- Formula: **N = max(1, (dump_size_gb / 15))**, e.g., 10GB → 1 bucket, 25GB → 2 buckets, 100GB → 7 buckets.
  - Conservative: Better to under-estimate and re-partition than OOM during sort.
  - Real heaps: Edge density (average fanout per object) varies; this formula assumes ~5–10 edges per live object on typical heaps.
  - **Action:** Benchmark on 5GB+ dumps; adjust formula if actual bucket sizes exceed 500MB consistently.

### Hash Function (Deterministic Partition)

All partitioning must use the same hash function across runs (for cache.bin reuse and reproducibility):

```csharp
private static uint ChildBucketHash(ulong child, int bucketCount)
{
    // Fnv1a 64-bit hash, then map to bucket index
    // Ensures: same child → same bucket across all runs
    unchecked
    {
        const ulong FnvPrime = 0x100000001b3;
        const ulong FnvOffset = 0xcbf29ce484222325;
        
        ulong hash = FnvOffset ^ child;
        hash = (hash ^ (child >> 32)) * FnvPrime;
        return (uint)(hash % (uint)bucketCount);
    }
}
```

**Why Fnv1a?** 64-bit operation on child (ulong), deterministic across .NET versions/platforms, standard for binary protocols.

### Fanout Capping (Deterministic Truncation)

During extraction, track an in-memory `Dictionary<ulong, int>` **per bucket** of edge counts for each child. **Collect edges first, then apply cap in sorted order** to ensure reproducibility:

```csharp
const int MaxParentsPerChild = 10_000;

// Phase A1: Collect edges per bucket (append-only writes)
// [child, parent, child, parent, ...]

// Phase A2: Post-process: sort edges per child, cap per-child list
var edgesPerChild = new Dictionary<ulong, List<ulong>>(); // child → [parents...]

foreach (var (child, parent) in bucket.RawEdges)
{
    if (!edgesPerChild.TryGetValue(child, out var parents))
    {
        parents = new List<ulong>();
        edgesPerChild[child] = parents;
    }
    parents.Add(parent);
}

var truncatedChildren = new HashSet<ulong>();

foreach (var (child, parents) in edgesPerChild)
{
    if (parents.Count > MaxParentsPerChild)
    {
        // Sort parents deterministically before truncating
        parents.Sort();
        parents.RemoveRange(MaxParentsPerChild, parents.Count - MaxParentsPerChild);
        truncatedChildren.Add(child);
    }
}
```

**Rationale**: Some objects (empty `Array[]`, interned strings, `Type` instances) can have millions of referrers. Recording all of them bloats disk and makes lookups slow. A cap of 10K is:
- High enough to diagnose real retention issues (e.g., "this event has 5K subscribers").
- Low enough to keep disk size predictable (~80 bytes per edge, worst case ~800KB per truncated child).
- Flagged via `Truncated` so analyzers can detect and handle (see [Truncation Handling](#truncation-handling)).

**For reproducibility:** Truncated children's parents are **always recorded in ascending address order** (sorted before capping); this ensures identical results across runs and analyzers.

### Scratch File Layout (Raw Edges)

Write fixed 16-byte records per bucket:
```
[ChildAddress: ulong(8)] [ParentAddress: ulong(8)]
```

No header, no sorting yet — just append-only stream of edges belonging to the bucket.

**File naming**: `<cache_dir>/reverse_edges_bucket_<i>.tmp`

### Integration with Phase 1 (Single-Pass Enumeration)

**Critical:** Do NOT re-iterate the heap after streaming. Integrate edge extraction into `HeapAnalysisEngine.BuildPhase1IndexAsync()`:

1. During `HeapStreamer.EnumerateObjects()`, after yielding each object, enumerate its forward references (fields).
2. For each `(parent: object, child: field reference)` edge:
   - Compute `bucketIndex = ChildBucketHash(child, N)`.
   - Write `[child, parent]` to bucket file N (lock-free, append-only).
   - Increment `fanoutPerChild[child]` counter in per-bucket dictionary.
3. After streaming completes, all bucket files are written; proceed to Phase B.

**Why single-pass?** A second heap scan is prohibitively expensive on 25GB dumps (15–20 min). Avoid unless absolutely necessary.

**If re-iteration is unavoidable** (e.g., ClrMD 4 doesn't expose all refs in first pass):
- Measure wall-clock time on 5GB+ dumps before committing to this approach.
- Consider caching forward refs in a temp index (disk-backed, not RAM) to avoid full re-scan.

---

## Phase B — Per-Bucket Sort + Directory

**When**: After all edges have been extracted into bucket files. Can start before Phase 1 completes other analyses (decoupled, parallel per bucket).

**Process** (per bucket, parallelizable via `Task.WhenAll`):

### B1: Load & Validate

```csharp
var bucketFile = $"reverse_edges_bucket_{i}.tmp";
var bucketSizeBytes = new FileInfo(bucketFile).Length;
const long MaxBucketSize = 600 * 1024 * 1024;  // 600 MB hard limit

if (bucketSizeBytes > MaxBucketSize)
{
    throw new InvalidOperationException(
        $"Bucket {i} exceeds {MaxBucketSize} bytes ({bucketSizeBytes}). " +
        $"Increase bucket count (N) and re-run extraction, or implement external merge-sort.");
}

var edgeCount = bucketSizeBytes / 16;  // 16 bytes per edge
var edges = new (ulong child, ulong parent)[edgeCount];

using (var fs = File.OpenRead(bucketFile))
using (var reader = new BinaryReader(fs))
{
    for (long j = 0; j < edgeCount; j++)
    {
        edges[j] = (reader.ReadUInt64(), reader.ReadUInt64());
    }
}
```

**Rationale:** Validate bucket size before allocating array. If bucket exceeds threshold, fail fast with actionable message (increase N or implement external sort).

### B2: Sort by Child Address

```csharp
// Quicksort (or use Array.Sort which uses introsort/heapsort)
Array.Sort(edges, (a, b) => a.child.CompareTo(b.child));
```

**Note:** Modern .NET uses introsort (quicksort + heapsort + insertion sort hybrid), better than raw quicksort.

### B3: Group by Child, Compute Directory

Walk sorted array, group consecutive edges by child, write output and build directory:

```csharp
var dataWriter = File.Create($"reverse_edges_bucket_{i}.dat");
var dirEntries = new List<(ulong childAddr, long fileOffset)>();

long currentOffset = 0;
for (int j = 0; j < edges.Length; )
{
    var child = edges[j].child;
    var parentList = new List<ulong>();
    
    // Collect all parents for this child
    while (j < edges.Length && edges[j].child == child)
    {
        parentList.Add(edges[j].parent);
        j++;
    }
    
    // Write group: [child:8][count:4][truncated:1][pad:3][parents:8*count]
    var groupStartOffset = currentOffset;
    var writer = new BinaryWriter(dataWriter);
    
    writer.Write(child);
    writer.Write(parentList.Count);
    writer.Write(parentList.Count > MaxParentsPerChild);  // truncated flag
    writer.Write(new byte[3]);  // padding for alignment
    
    foreach (var parent in parentList)
        writer.Write(parent);
    
    dataWriter.Flush();
    currentOffset = dataWriter.Length;
    
    // Add to directory
    dirEntries.Add((child, groupStartOffset));
}

dataWriter.Dispose();
```

### B4: Write Directory Index

```csharp
var dirWriter = File.Create($"reverse_edges_bucket_{i}.idx");
var bw = new BinaryWriter(dirWriter);

// Header (24 bytes)
bw.Write(0xDEADBEEF);      // Magic
bw.Write(1u);               // Version
bw.Write((long)dirEntries.Count);  // EntryCount
bw.Write(new byte[8]);      // Reserved

// Directory entries (sorted by child, already from earlier walk)
foreach (var (child, offset) in dirEntries)
{
    bw.Write(child);
    bw.Write(offset);
}

dirWriter.Dispose();
```

**Directory Size Estimate:**
- On 25GB dump: ~250M objects, assume ~70% have ≥1 parent → ~175M entries.
- 175M entries × 16 bytes (per directory entry) = **2.8 GB on disk**.
- With 2–3 buckets (per N formula), each directory is ~1–1.4 GB.
- **This is significant; quantify on test dumps and add to metadata.**

### Output Files (per bucket)

- **Sorted groups**: `<cache_dir>/reverse_edges_bucket_<i>.dat` (variable-length records)
- **Directory**: `<cache_dir>/reverse_edges_bucket_<i>.idx` (fixed 24-byte header + 16-byte entries)

These are later merged into `cache.bin` sections.

### Memory Peak Monitoring

During Phase B, peak memory = largest bucket file loaded as array:
- 500 MB bucket = 31.25M edges = 31.25M × 16 bytes = **500 MB** in `edges[]` array.
- Plus temporary allocations during sort (minimal with introsort).
- **Expected peak per-bucket: <600 MB.**
- Monitor via GC logs or process memory; if peak exceeds 700 MB, increase N and re-run.

---

## Phase C — Container Integration

Add three new sections to the `cache.bin` TOC:

| Section Name | Contains | Format |
|--------------|----------|--------|
| `ReverseEdgeBuckets.Bucket0..N` | Sorted group payloads per bucket | Variable-length records: [child:8][count:4][truncated:1][pad:3][parents:8*count] |
| `ReverseEdgeDirectories.Bucket0..N` | Directory index per bucket | Header (24B) + entries (16B each): [child:8][dataOffset:8] |
| `ReverseEdgeMetadata` | Metadata and stats | JSON: bucket config, timing, truncation distribution |

**Layout in cache.bin TOC**:
```
[CacheContainerHeader: 64B]
[TOC: 16+ entries × 32B]  // Includes ReverseEdgeBuckets.0, .1, ..., ReverseEdgeDirectories.0, .1, ..., ReverseEdgeMetadata
[... existing sections ...]
[ReverseEdgeBuckets.Bucket0 payload]
[ReverseEdgeBuckets.Bucket1 payload]
[ReverseEdgeBuckets.Bucket2 payload]
[ReverseEdgeDirectories.Bucket0 payload]
[ReverseEdgeDirectories.Bucket1 payload]
[ReverseEdgeDirectories.Bucket2 payload]
[ReverseEdgeMetadata payload]
```

**Metadata (JSON)**:
```json
{
  "bucketCount": 3,
  "maxParentsPerChild": 10000,
  "hashFunction": "Fnv1a64",
  "totalEdgesExtracted": 2_847_365_912,
  "totalChildren": 187_543_201,
  "totalParents": 2_102_184_567,
  "truncatedChildrenCount": 847,
  "truncatedDistribution": {
    "lessThan100": 400,
    "lessThan1K": 320,
    "lessThan10K": 127
  },
  "bucketSizeBytes": [
    523_547_821,
    481_293_456,
    445_821_034
  ],
  "directorySizeBytes": [
    1_400_676_064,
    1_203_455_392,
    1_089_234_704
  ],
  "extractionElapsedMs": 8500,
  "sortElapsedMs": 4250,
  "mergeElapsedMs": 2100,
  "peakMemoryMb": 487,
  "extractionTimestampUtc": "2026-08-11T14:32:00Z",
  "dumpPath": "large_heap.dmp",
  "dumpSize": 25_682_604_032
}
```

**Diagnostics to Monitor:**
- `truncatedChildrenCount` > 1% of `totalChildren` → Consider lower `maxParentsPerChild` or higher bucket count.
- `peakMemoryMb` > 600 → Bucket overflowed memory; increase N.
- `bucketSizeBytes` distribution skewed → Fanout distribution uneven; may indicate hash function issues or real workload skew.

### Format Versioning & Backward Compatibility

**Version Bump:** Increment `FormatVersion` in `CacheContainerHeader` (currently 3 → 4) when first reverse-index section is written.

**Compatibility Matrix:**

| Writer Version | Cache Version | Reader v3 | Reader v4+ |
|---|---|---|---|
| v3 (old) | 3 | ✅ Works | ✅ Works (ignores v4 sections) |
| v4 (new) | 4 | ❌ Fails ("Unsupported version") | ✅ Works |

**Reader Behavior:**
```csharp
// In CacheContainerReader.Load()
if (header.FormatVersion > SupportedVersion)
    throw new InvalidOperationException(
        $"Cache format v{header.FormatVersion} not supported. " +
        $"Please upgrade or regenerate cache.");

// When reading optional reverse-index sections (v4+)
if (header.FormatVersion >= 4 && TryReadSection("ReverseEdgeMetadata", out var meta))
{
    // Initialize ReverseEdgeIndexReader
}
else
{
    // v3 cache or v4 without reverse-index sections; continue without reverse lookups
}
```

**Incremental Rebuild:** If v3 cache exists and v4 code runs:
1. Detect version mismatch.
2. Delete v3 cache.bin.
3. Re-run `HeapAnalysisEngine.BuildPhase1IndexAsync()` with new code (generates v4 with reverse-index).
4. Transparent to analyzers (happens automatically).

**No downgrade path:** v4 cache cannot be read by v3 code. This is intentional (prevents silent data corruption).

---

## Query Path: ReverseEdgeIndexReader

### Public API

```csharp
internal sealed class ReverseEdgeIndexReader : IDisposable
{
    public ReverseEdgeIndexReader(CacheContainerReader container);
    
    /// <summary>
    /// Retrieve all parent addresses for a given child.
    /// Returns true if child has recorded parents (empty list if no parents, truncated indicates capped result).
    /// Returns false if child not in index (no parents recorded during extraction).
    /// </summary>
    public bool TryGetParents(
        ulong child, 
        out IReadOnlyList<ulong> parents, 
        out bool truncated);
    
    public void Dispose();
}
```

**Caller Contract:**
- `TryGetParents(child) == true` → child has ≥1 parent; `parents` list returned (possibly truncated).
- `TryGetParents(child) == false` → child has no recorded parents; `parents` empty list, `truncated` false.
- Truncated flag indicates "parents were capped at MaxParentsPerChild; not all referrers recorded."

### Implementation (Synchronized Access)

**Pattern: Synchronized per-bucket, single shared FileStream to cache.bin**

```csharp
internal sealed class ReverseEdgeIndexReader : IDisposable
{
    private readonly CacheContainerReader _container;
    private readonly int _bucketCount;
    private readonly int _maxParentsPerChild;
    
    // One lock per bucket to serialize seeks for that bucket's directory/data sections
    private readonly object[] _bucketLocks;
    
    public ReverseEdgeIndexReader(CacheContainerReader container)
    {
        _container = container ?? throw new ArgumentNullException(nameof(container));
        
        // Read metadata to get bucket count
        var meta = JsonSerializer.Deserialize<ReverseIndexMetadata>(_container.ReadSectionAsString("ReverseEdgeMetadata"));
        _bucketCount = meta.BucketCount;
        _maxParentsPerChild = meta.MaxParentsPerChild;
        
        _bucketLocks = Enumerable.Range(0, _bucketCount).Select(_ => new object()).ToArray();
    }
    
    public bool TryGetParents(ulong child, out IReadOnlyList<ulong> parents, out bool truncated)
    {
        parents = Array.Empty<ulong>();
        truncated = false;
        
        // Step 1: Compute bucket
        int bucketIdx = (int)(ChildBucketHash(child, _bucketCount) % (uint)_bucketCount);
        
        lock (_bucketLocks[bucketIdx])  // Serialize within this bucket
        {
            // Step 2: Binary search in bucket's directory
            var dirSection = _container.ReadSection($"ReverseEdgeDirectories.Bucket{bucketIdx}");
            if (!BinarySearchDirectory(dirSection, child, out long dataOffset))
            {
                return false;  // Child not in index
            }
            
            // Step 3: Seek & read group from data section
            var dataSection = _container.ReadSection($"ReverseEdgeBuckets.Bucket{bucketIdx}");
            var group = ReadGroup(dataSection, dataOffset, out int parentCount, out truncated);
            
            parents = group;
            return true;
        }
    }
    
    private bool BinarySearchDirectory(byte[] dirData, ulong child, out long dataOffset)
    {
        // Parse directory header
        var reader = new BinaryReader(new MemoryStream(dirData));
        uint magic = reader.ReadUInt32();
        if (magic != 0xDEADBEEF)
            throw new InvalidOperationException("Invalid directory magic.");
        
        uint version = reader.ReadUInt32();
        if (version != 1)
            throw new InvalidOperationException($"Unsupported directory version {version}.");
        
        long entryCount = reader.ReadInt64();
        reader.ReadBytes(8);  // skip reserved
        
        // Binary search over entries
        long lo = 0, hi = entryCount - 1;
        dataOffset = -1;
        
        while (lo <= hi)
        {
            long mid = lo + (hi - lo) / 2;
            
            // Seek to mid entry (header is 24 bytes, each entry is 16 bytes)
            reader.BaseStream.Seek(24 + mid * 16, SeekOrigin.Begin);
            ulong midChild = reader.ReadUInt64();
            long midOffset = reader.ReadInt64();
            
            if (midChild == child)
            {
                dataOffset = midOffset;
                return true;
            }
            else if (midChild < child)
                lo = mid + 1;
            else
                hi = mid - 1;
        }
        
        return false;
    }
    
    private ulong[] ReadGroup(byte[] dataSection, long offset, out int parentCount, out bool truncated)
    {
        var reader = new BinaryReader(new MemoryStream(dataSection));
        reader.BaseStream.Seek(offset, SeekOrigin.Begin);
        
        ulong childAddr = reader.ReadUInt64();  // Validate
        parentCount = reader.ReadInt32();
        truncated = reader.ReadBoolean();
        reader.ReadBytes(3);  // skip padding
        
        var parents = new ulong[parentCount];
        for (int i = 0; i < parentCount; i++)
            parents[i] = reader.ReadUInt64();
        
        return parents;
    }
}
```

**Thread Safety Rationale:**
- Each bucket has its own lock: `_bucketLocks[bucketIdx]`.
- Within a bucket lock, only two seeks: directory binary search, then data read.
- Lock scope is minimal (few microseconds per query).
- No contention between buckets; N threads querying different buckets proceed in parallel.
- Single shared `FileStream` from `CacheContainerReader` is safe because seeks are synchronized per bucket.

**Memory Overhead:**
- N bucket locks: negligible (N ≤ 10, ~100 bytes per lock object).
- No per-query allocations except `parents` array (returned to caller).
- Use pooled buffers for directory reads if directory is large (>100 MB).

### Performance Expectations

- **Directory binary search**: O(log M) seeks where M = distinct children in bucket. ~10–20 iterations per query, <1 ms per query.
- **Data read**: Seek + read 13 + (8 × K) bytes, where K ≤ 10K. With disk I/O, expect <5–20 ms per query for typical bucket layout.
- **Total expected latency**: <50 ms p99 for warm cache; cold disk reads (first query) may hit 100+ ms.
- **Concurrency**: 10 concurrent analyzers querying different buckets → minimal lock contention; parallelism limited by disk I/O throughput.

---

## Truncation Handling {#truncation-handling}

When a child object exceeds `MaxParentsPerChild` (10K parents), the index records only the first 10K **in ascending parent address order**. Analyzers querying truncated children must decide how to proceed:

### For Analyzers (Guidance)

```csharp
if (indexReader.TryGetParents(suspect, out var parents, out bool truncated))
{
    if (truncated && suspect.Size > 1_000_000)  // Suspect is large
    {
        // Fall back to expensive full scan
        var allParents = suspect.EnumerateParents();  // Direct ClrHeap enumeration
        // or skip this suspect as "too complex to analyze"
    }
    else
    {
        // Use truncated result for diagnostics
        analyzer.AnalyzeRetention(suspect, parents);
    }
}
```

### Expected Truncation Rates

On typical 25GB dumps, truncation should be rare (~<1% of children). Objects exceeding 10K referrers are:
- Interned strings (handled specially by StringAnalyzer).
- Empty arrays `Array[]` (not usually targets for leak analysis).
- `Type` instances held by assemblies (well-understood retention).

**Monitoring:** Include truncated child count in metadata; trend across dumps. If >5% children truncated, lower `MaxParentsPerChild` or increase bucket count N.

---

## Architectural Decisions & Trade-offs

### Decision 1: Full Index vs. Selective Filtering

**Choice:** Build a **full reverse index** with no type/noise filtering during extraction.

**Trade-off:**
| Aspect | Full Index | Filtered Index |
|--------|-----------|-----------------|
| **Disk size** | Larger (billions of edges) | Smaller (type-filtered) |
| **Extraction cost** | Fixed, one-time | Lower, faster |
| **Analyzer flexibility** | High (each can filter at query time) | Low (filtering baked in) |
| **Rebuild frequency** | Rare (if at all) | Per-analyzer or per-filter config |

**Rationale for Full:** Each analyzer has different filtering needs. StringAnalyzer skips primitives; WeakReferenceAnalyzer skips internal CLR types. Building once, filtering N times at query is cheaper than rebuilding N times. **Cost:** 2–3 GB extra disk per 25GB dump.

**Future:** If disk bloat becomes critical, implement `IEdgeFilter` interface and rebuild with selective extraction.

---

### Decision 2: Single-Pass Extraction vs. Re-iteration

**Choice:** **Single-pass during heap streaming** (no re-iteration).

**Trade-off:**
| Aspect | Single-Pass | Re-Iteration |
|--------|-------------|--------------|
| **Wall-clock time** | ~1–2× heap scan | +15–20 min on 25GB |
| **Complexity** | Simple (extend HeapStreamer) | Requires caching or second scan |
| **Code coupling** | Tight (integrated into Phase 1) | Loose (separate task) |

**Rationale:** Heap traversal on 25GB dumps is expensive (~10–15 min). Re-scanning adds 15–20 min per analysis run, compounding with every new analyzer. Single-pass amortizes cost across all analyzers. **Constraint:** ClrMD must expose all forward refs in one pass (true for ClrMD 4+).

**Risk:** If ClrMD doesn't expose all refs in one pass (undocumented), extraction is incomplete. Mitigation: Benchmark ClrMD 4 on test dumps, validate edge completeness against manual forward-ref enumeration.

---

### Decision 3: Hash-Partitioning vs. External Merge-Sort

**Choice:** **Hash-partitioning into N small buckets** with independent per-bucket sort.

**Trade-off:**
| Aspect | Hash-Partition (N buckets) | External Merge-Sort (K-way) |
|--------|---------------------------|---------------------------|
| **Complexity** | O(N) simple sorts | O(K log K) merge, complex |
| **Memory peak** | ~500 MB per bucket | <100 MB (streaming merge) |
| **Parallelism** | Trivial (N tasks) | Non-trivial (merge phases) |
| **Implementation LOC** | ~200 | ~500+ |
| **Debuggability** | Easy (inspect bucket files) | Hard (merge traces complex) |

**Rationale:** Simple, parallel, debuggable. Trade per-bucket memory for simplicity. **Risk:** If bucket formula underestimates, single bucket exceeds 600 MB and sort fails. Mitigation: Validate bucket sizes pre-sort; fail with actionable message.

---

### Decision 4: Bounded Fanout (10K cap) vs. Full Fanout

**Choice:** **Cap fanout at 10K parents per child.**

**Trade-off:**
| Aspect | Capped (10K) | Unbounded |
|--------|---------|-----------|
| **Disk size** | Predictable (~1–5 GB) | Unbounded (pathological cases: 100 GB) |
| **Query latency** | O(10K) = bounded | O(millions) = unbounded |
| **False negatives** | Possible for truncated children | No false negatives |
| **Usability** | High (predictable) | Low (unpredictable perf) |

**Rationale:** Pathological objects (interned strings, `Type` instances) can have millions of referrers. Unbounded index bloats disk and makes traversal slow for marginal diagnostic value. 10K is sufficient to identify "hot" objects; analyzers can decide to handle truncated results specially.

**Cost:** Analyzers querying truncated suspects must fall back to expensive full scan. Expected to be rare (<1% of queries).

---

### Decision 5: Concurrent Access Pattern (Synchronized vs. Lock-Free)

**Choice:** **Per-bucket synchronized access** via lock per bucket.

**Trade-off:**
| Aspect | Per-Bucket Lock | Global Lock | Lock-Free (Atomic) |
|--------|----------------|-------------|-------------------|
| **Complexity** | Moderate (N locks) | Simple (1 lock) | High (CAS loops, complex) |
| **Contention** | Low (N buckets, independent) | High (single lock) | Minimal |
| **Latency p99** | <50 ms (minimal lock hold) | <50 ms (contention under 10 threads) | <50 ms (no wait) |
| **Throughput (100 threads)** | Near-linear scaling | Serialized, limited | Near-linear scaling |

**Rationale:** Per-bucket locks balance simplicity and concurrency. Analyzers typically run 2–10 threads; per-bucket contention is negligible. Global lock would serialize across buckets (acceptable for small thread count but doesn't scale). Lock-free adds complexity for marginal gain.

**Scalability:** If future workloads spawn 100+ analyzer threads, migrate to lock-free design or thread-local buffering.

---

### Decision 6: Container Integration (cache.bin) vs. Standalone Files

**Choice:** **Integrate into cache.bin** (three new TOC sections: `ReverseEdgeBuckets`, `ReverseEdgeDirectories`, `ReverseEdgeMetadata`).

**Trade-off:**
| Aspect | cache.bin | Standalone Files |
|--------|-----------|------------------|
| **Atomicity** | Atomic (one file, version-checked) | Partial (3+ files, race conditions) |
| **Reader complexity** | One `CacheContainerReader` | N file handles, manual offset tracking |
| **Backward compat** | Clean (version check) | Complex (file versioning per file) |
| **Disk fragmentation** | Low (single container) | High (scattered files) |
| **Cache coherence** | All sections validated together | Per-file validation, skew possible |

**Rationale:** Simpler reader, atomic validation, version safety. Old code reading v4 cache.bin fails cleanly (version check); doesn't silently misinterpret data.

---

## Why This Design

See [Architectural Decisions & Trade-offs](#architectural-decisions--trade-offs) for detailed rationale on each choice (full index, single-pass extraction, hash-partitioning, fanout capping, per-bucket synchronization, container integration).

**Summary:**
1. **Full index** trades disk size for analyzer flexibility (no rebuilds per analyzer).
2. **Single-pass extraction** amortizes expensive heap traversal across all analyzers.
3. **Hash-partitioning** enables simple parallel sort without external merge complexity.
4. **Fanout capping** bounds pathological cases (hot objects with millions of referrers) while remaining useful for leak detection.
5. **Per-bucket locking** provides good concurrency without lock-free complexity.
6. **cache.bin integration** ensures atomic validation and simpler reader lifecycle.

---

## Analyzer Integration Pattern

Analyzers can opt-in to use reverse-index queries. This is **optional** — analyzers without reverse-index support continue to work unchanged (fallback to forward-ref enumeration or existing patterns).

### Base Class / Mixin (Recommended)

```csharp
// New: AnalysisContextExtensions or IReverseLookupCapable
public abstract class ReverseIndexAwareAnalyzer : IAnalyzer
{
    protected ReverseEdgeIndexReader? ReverseIndex { get; private set; }
    
    public async ValueTask<AnalyzerDomainResult> AnalyzeAsync(
        AnalysisContext context, 
        CancellationToken cancellationToken)
    {
        // Try to load reverse index from cache
        if (context.Cache.TryReadSection("ReverseEdgeMetadata", out _))
        {
            ReverseIndex = new ReverseEdgeIndexReader(context.Cache);
        }
        
        return await AnalyzeWithReverseLookupAsync(context, cancellationToken);
    }
    
    protected abstract ValueTask<AnalyzerDomainResult> AnalyzeWithReverseLookupAsync(
        AnalysisContext context,
        CancellationToken cancellationToken);
}
```

### Usage in Existing Analyzers

**Example: WeakReferenceAnalyzer**
```csharp
public class WeakReferenceAnalyzer : ReverseIndexAwareAnalyzer
{
    protected override async ValueTask<AnalyzerDomainResult> AnalyzeWithReverseLookupAsync(
        AnalysisContext context,
        CancellationToken ct)
    {
        var result = new WeakReferenceAnalyzerResult();
        
        foreach (var weak in heap.EnumerateObjectsOfType("System.WeakReference"))
        {
            if (ReverseIndex != null && ReverseIndex.TryGetParents(weak.Address, out var parents, out bool truncated))
            {
                // Use fast reverse-index lookup
                foreach (var parent in parents)
                    result.Holders.Add(parent);
                
                if (truncated)
                    result.AddNote("Truncated parent list (>10K holders)");
            }
            else
            {
                // Fallback: expensive enumeration (still correct, just slow)
                foreach (var parent in weak.EnumerateParents())
                    result.Holders.Add(parent.Address);
            }
        }
        
        return result;
    }
}
```

### Migration Timeline

- **Phase 1 (v4 release):** Reverse-index available; no analyzers updated yet.
- **Phase 2 (v4.1):** WeakReferenceAnalyzer + StringAnalyzer migrated to opt-in (20% perf gain).
- **Phase 3 (v4.2):** AsyncStateMachineAnalyzer, TimerLeakAnalyzer migrated (incremental).
- **Phase 4:** New analyzers default to reverse-index usage.

Existing analyzers without migration continue to work (no breaking change).

---

## Implementation Strategy

### Step 1: Hash Function & Utilities
**Deliverable:** `ReverseIndexConstants.cs`

```csharp
internal static class ReverseIndexConstants
{
    public const uint Magic = 0xDEADBEEF;
    public const uint DirectoryVersion = 1;
    public const int MaxParentsPerChild = 10_000;
    
    // Deterministic Fnv1a 64-bit hash
    public static uint ChildBucketHash(ulong child, int bucketCount)
    {
        unchecked
        {
            const ulong FnvPrime = 0x100000001b3;
            const ulong FnvOffset = 0xcbf29ce484222325;
            
            ulong hash = FnvOffset ^ child;
            hash = (hash ^ (child >> 32)) * FnvPrime;
            return (uint)(hash % (uint)bucketCount);
        }
    }
    
    // Calculate bucket count based on dump size
    public static int CalculateBucketCount(long dumpSizeBytes)
    {
        var dumpSizeGb = dumpSizeBytes / (1024.0 * 1024 * 1024);
        return Math.Max(1, (int)(dumpSizeGb / 15));
    }
}
```

**Tests:**
- Verify hash is deterministic (same child → same bucket across 100 calls).
- Verify hash distributes uniformly (sample 1M children, check bucket distribution ±10%).

---

### Step 2: Writer Infrastructure (Phase A)
**Deliverable:** `ReverseEdgeExtractor.cs`

```csharp
internal class ReverseEdgeExtractor : IAsyncDisposable
{
    private readonly int _bucketCount;
    private readonly BinaryWriter[] _bucketWriters;
    private readonly Dictionary<ulong, int>[] _fanoutPerBucket;
    private readonly HashSet<ulong>[] _truncatedPerBucket;
    
    public ReverseEdgeExtractor(int bucketCount, string cacheDir)
    {
        _bucketCount = bucketCount;
        _bucketWriters = new BinaryWriter[bucketCount];
        _fanoutPerBucket = new Dictionary<ulong, int>[bucketCount];
        _truncatedPerBucket = new HashSet<ulong>[bucketCount];
        
        for (int i = 0; i < bucketCount; i++)
        {
            var path = Path.Combine(cacheDir, $"reverse_edges_bucket_{i}.tmp");
            var fs = File.Create(path, bufferSize: 65536);
            _bucketWriters[i] = new BinaryWriter(fs, Encoding.Default, leaveOpen: false);
            _fanoutPerBucket[i] = new Dictionary<ulong, int>();
            _truncatedPerBucket[i] = new HashSet<ulong>();
        }
    }
    
    public void RecordEdge(ulong parent, ulong child)
    {
        int bucketIdx = (int)ReverseIndexConstants.ChildBucketHash(child, _bucketCount);
        var fanout = _fanoutPerBucket[bucketIdx];
        
        if (!fanout.TryGetValue(child, out int count))
            count = 0;
        
        if (count >= ReverseIndexConstants.MaxParentsPerChild)
        {
            _truncatedPerBucket[bucketIdx].Add(child);
            return;
        }
        
        fanout[child] = count + 1;
        _bucketWriters[bucketIdx].Write(child);
        _bucketWriters[bucketIdx].Write(parent);
    }
    
    public async ValueTask DisposeAsync()
    {
        foreach (var writer in _bucketWriters)
            writer?.Dispose();
    }
}
```

**Integration into `HeapAnalysisEngine.BuildPhase1IndexAsync()`:**
- After heap streaming starts, spawn `ReverseEdgeExtractor`.
- During object enumeration, call `extractor.RecordEdge(parent, child)` for each forward ref.
- After streaming ends, `await extractor.DisposeAsync()`.

**Tests:**
- Small heap: 100 objects, 200 edges; verify all edges routed to correct buckets.
- Fanout cap: Create child with 15K parents; verify exactly 10K recorded and truncated flag set.
- Determinism: Same heap → same edge distribution (verify via byte-for-byte comparison of bucket files).

---

### Step 3: Sort & Directory (Phase B)
**Deliverable:** `ReverseEdgeSorter.cs`

```csharp
internal class ReverseEdgeSorter
{
    public async Task<ReverseIndexSortResult> SortBucketsAsync(
        string cacheDir,
        int bucketCount,
        CancellationToken ct)
    {
        var sortTasks = Enumerable.Range(0, bucketCount)
            .Select(i => SortBucketAsync(cacheDir, i, ct))
            .ToArray();
        
        var results = await Task.WhenAll(sortTasks);
        
        return new ReverseIndexSortResult
        {
            BucketDataSizes = results.Select(r => r.DataFileSize).ToList(),
            BucketDirSizes = results.Select(r => r.DirFileSize).ToList(),
            TotalElapsedMs = results.Max(r => r.ElapsedMs),
            PeakMemoryMb = results.Max(r => r.PeakMemoryMb),
        };
    }
    
    private async Task<BucketSortResult> SortBucketAsync(string cacheDir, int bucketIdx, CancellationToken ct)
    {
        var tmpFile = Path.Combine(cacheDir, $"reverse_edges_bucket_{bucketIdx}.tmp");
        var dataFile = Path.Combine(cacheDir, $"reverse_edges_bucket_{bucketIdx}.dat");
        var dirFile = Path.Combine(cacheDir, $"reverse_edges_bucket_{bucketIdx}.idx");
        
        var sw = Stopwatch.StartNew();
        var fileInfo = new FileInfo(tmpFile);
        const long MaxBucketSize = 600 * 1024 * 1024;
        
        if (fileInfo.Length > MaxBucketSize)
            throw new InvalidOperationException(
                $"Bucket {bucketIdx} exceeds {MaxBucketSize} bytes. Increase N and re-run extraction.");
        
        // Load edges
        var edgeCount = fileInfo.Length / 16;
        var edges = new (ulong child, ulong parent)[edgeCount];
        
        using (var fs = File.OpenRead(tmpFile))
        using (var reader = new BinaryReader(fs))
        {
            for (long i = 0; i < edgeCount; i++)
                edges[i] = (reader.ReadUInt64(), reader.ReadUInt64());
        }
        
        // Sort by child
        Array.Sort(edges, (a, b) => a.child.CompareTo(b.child));
        
        // Write sorted groups + directory
        var dirEntries = new List<(ulong, long)>();
        using (var dataWriter = File.Create(dataFile, bufferSize: 65536))
        {
            long offset = 0;
            for (int i = 0; i < edges.Length; )
            {
                var child = edges[i].child;
                var parents = new List<ulong>();
                
                while (i < edges.Length && edges[i].child == child)
                {
                    parents.Add(edges[i].parent);
                    i++;
                }
                
                // Write group
                var bw = new BinaryWriter(dataWriter);
                bw.Write(child);
                bw.Write(parents.Count);
                bw.Write(parents.Count > ReverseIndexConstants.MaxParentsPerChild);
                bw.Write(new byte[3]);
                foreach (var p in parents)
                    bw.Write(p);
                
                dirEntries.Add((child, offset));
                offset = dataWriter.Length;
            }
        }
        
        // Write directory
        using (var dirWriter = File.Create(dirFile))
        using (var bw = new BinaryWriter(dirWriter))
        {
            bw.Write(ReverseIndexConstants.Magic);
            bw.Write(ReverseIndexConstants.DirectoryVersion);
            bw.Write((long)dirEntries.Count);
            bw.Write(new byte[8]);
            
            foreach (var (child, fileOffset) in dirEntries)
            {
                bw.Write(child);
                bw.Write(fileOffset);
            }
        }
        
        sw.Stop();
        return new BucketSortResult
        {
            DataFileSize = new FileInfo(dataFile).Length,
            DirFileSize = new FileInfo(dirFile).Length,
            ElapsedMs = (int)sw.ElapsedMilliseconds,
            PeakMemoryMb = (int)(GC.GetTotalMemory(false) / (1024 * 1024)),
        };
    }
}

internal class BucketSortResult
{
    public long DataFileSize { get; set; }
    public long DirFileSize { get; set; }
    public int ElapsedMs { get; set; }
    public int PeakMemoryMb { get; set; }
}
```

**Tests:**
- Sorted order: Load bucket, verify edges grouped and sorted by child.
- Directory binary search: Manually construct directory, verify binary search finds all children.
- OOM handling: Artificially create 700 MB bucket, verify clean error (not OOM crash).

---

### Step 4: Container Integration (Phase C)
**Deliverable:** Update `CacheContainerBuilder`

- Read `.dat` and `.idx` files per bucket.
- Add TOC entries `ReverseEdgeBuckets.Bucket0..N`, `ReverseEdgeDirectories.Bucket0..N`, `ReverseEdgeMetadata`.
- Bump `FormatVersion` 3 → 4.
- Write metadata JSON with stats from Phase A/B.

**Tests:**
- TOC integrity: Load cache.bin, verify all reverse-index sections present and seekable.
- Version check: Old code reading v4 cache fails with proper error message.

---

### Step 5: Reader Implementation
**Deliverable:** `ReverseEdgeIndexReader.cs` (see [Query Path](#query-path-reverseedgeindexreader) section).

**Tests:**
- Concurrent queries: 10 threads querying different children simultaneously; verify no crashes or wrong results.
- Truncated children: Query child with 15K parents (truncated at 10K); verify truncated flag set and exactly 10K parents returned.
- Missing child: Query child not in heap; verify TryGetParents returns false.

---

### Step 6: Analyzer Integration & End-to-End Testing

**Deliverable:** `ReverseIndexAwareAnalyzer` base class + example integration.

**Testing Plan:**

| Dump | Focus | Validation |
|------|-------|-----------|
| 10 MB test | Edge correctness | Manually verify parent lists against forward-ref enumeration |
| 500 MB medium | Perf & truncation | Profile disk size, query latency, truncation rate |
| 5–10 GB large | Bounded memory | Monitor GC, peak memory, wall-clock time |

**Success Criteria:**
- Edge count matches (or nearly matches within 0.1%) forward-ref enumeration on test dump.
- Query latency p99 < 50 ms.
- Truncation rate < 1% (< 1% of children truncated).
- Total time (extraction + sort + merge) < 5 min on 10 GB dump.
- Peak memory during sort < 600 MB.
- Cache.bin size increase < 15% on large dumps.

---

## Risks & Mitigations

| Risk | Severity | Mitigation | Owner | Timeline |
|------|----------|-----------|-------|----------|
| **Disk size blowup** (billions of edges) | Medium | Fanout cap (10K) prevents worst case. Monitor actual edge counts on 5–10GB test dumps. If >20% of dump size, lower cap or increase N. | Phase 2 | Before launch |
| **Sort phase OOM** (bucket >600MB) | High | Validate bucket sizes pre-sort. If exceeded, fail with actionable message + recommendation. Implement external merge-sort as Phase 2 fallback if needed. | Phase 1 | Before launch |
| **Hash distribution skew** | Medium | Benchmark hash function on real heap addresses (sample 1M children, verify ±10% bucket size distribution). If skew >20%, tune or replace hash. | Phase 1 | Before launch |
| **Single-pass edge extraction incomplete** | High | ClrMD may not expose all forward refs in one pass. Benchmark vs. multi-pass enumeration on test dumps. If gap >1%, implement re-iteration with fallback pattern. | Phase 1 | Before launch |
| **Query latency degradation** (cold disk, many children) | Low | Expected <50ms p99 with directory binary search + buffered disk read. Benchmark on 5+ GB dumps. If p99 >100ms, implement LRU cache for hot children. | Phase 2 | Post-launch |
| **Truncation silent failures** | Medium | Analyzers must detect truncated flag and decide fallback strategy. Document per-analyzer guidance; add asserts/logging for suspicious truncations. | Phase 2 | Before analyzer migration |
| **Version compatibility** (v3↔v4) | Low | Bumping `FormatVersion` ensures old readers fail cleanly with clear error message. Incremental rebuild automatic. No manual migration needed. | Phase 1 | Before launch |
| **Lock contention** (many analyzer threads) | Low | Per-bucket locks provide good concurrency up to ~50 threads. If workloads exceed 100 threads, migrate to lock-free or thread-local buffering. | Phase 3 | Monitor in production |

---

## Known Unknowns & Pre-Implementation Investigations

Before committing to full implementation, the following must be validated:

### 1. ClrMD 4 Forward-Ref Completeness
**Question:** Does `ClrObject.EnumerateReferences()` expose all field references in a single pass, or are there edge cases (pinned objects, large arrays, LOH) that require re-iteration?

**Investigation:**
- Write test on 100 MB+ dump comparing edges from single vs. dual enumeration passes.
- Measure time cost of re-iteration (target: <10% of single-pass time to remain viable).
- Document ClrMD 4 limitations (if any) in schema.

**Impact:** Affects whether Phase A requires single-pass integration or can tolerate re-iteration.

---

### 2. Hash Function Distribution
**Question:** Does Fnv1a64 distribute heap addresses evenly across bucket counts (1–10)?

**Investigation:**
- Generate 1M synthetic child addresses (realistic distribution from large heap).
- Compute bucket assignment for each; check size distribution (should be ±10% uniform).
- Compare against alternative (xxHash64, MurmurHash3) if available.
- Verify determinism across multiple runs.

**Impact:** Affects bucket count formula and sort phase memory pressure.

---

### 3. Bucket Size Estimation
**Question:** Given the bucket count formula `N = max(1, dump_size_gb / 15)`, what are typical bucket sizes on real 25GB+ dumps?

**Investigation:**
- Profile 3–5 large dumps (10–25 GB each).
- Measure actual raw edge data per bucket.
- Verify formula (dump_size_gb / 15) produces <500 MB buckets.
- If consistently <100 MB, optimize formula for tighter buckets.
- If occasionally >600 MB, raise threshold or refactor formula.

**Impact:** Affects OOM risk and sort parallelism effectiveness.

---

### 4. Disk I/O Latency (Query Performance)
**Question:** Can ReverseEdgeIndexReader achieve <50ms p99 query latency on 5GB+ dumps with directory binary search + disk read?

**Investigation:**
- Implement reader (Step 4); benchmark on 5GB+ test dumps.
- Measure per-query latency distribution (p50, p95, p99).
- Vary disk type (SSD vs. HDD) and cache.bin size.
- If p99 >100ms, profile bottleneck (seek time vs. read time vs. lock contention).
- Consider LRU cache for frequently queried children if needed.

**Impact:** Affects whether reverse-index is practical for interactive queries or batch-only.

---

### 5. Truncation Impact on Leak Detection
**Question:** How often do suspects (candidate leak sources) end up truncated (>10K parents)? Does this break leak detection analysis?

**Investigation:**
- Run Phase A on 5+ large dumps; collect truncation distribution.
- Identify truncated children; categorize (interned strings, Type instances, etc.).
- Simulate leak detection algorithm on truncated results vs. full enumeration; measure false-negative rate.
- Adjust `MaxParentsPerChild` based on findings (e.g., raise to 50K if <1% truncation).

**Impact:** Affects whether 10K cap is safe or needs tuning for specific workloads.

---

### 6. Concurrent Query Throughput
**Question:** With per-bucket locking, what is the maximum query throughput (queries/sec) with 10+ concurrent analyzer threads?

**Investigation:**
- Implement reader + analyzer integration (Steps 4–5).
- Simulate 10 concurrent threads querying random children from 5GB+ dump.
- Measure queries/sec and lock wait times.
- If throughput insufficient (<10K qps), profile locks vs. disk I/O bottleneck.
- Consider lock-free or buffering strategies if contention high.

**Impact:** Affects viability for heavily multithreaded analyzer pipelines.

---

## Testing Plan

### Phase 1: Unit & Component Tests (Steps 1–5)

| Test | Target | Validation |
|------|--------|-----------|
| **Hash function determinism** | `ReverseIndexConstants.ChildBucketHash` | Same input → same output across 100 calls; different inputs → (mostly) different buckets |
| **Hash distribution** | 1M random children across N buckets | Bucket sizes within ±10% uniformity |
| **Edge routing** | `ReverseEdgeExtractor` with 100 objects | All edges routed to correct buckets based on hash |
| **Fanout cap** | Create child with 15K parents | Exactly 10K recorded, truncated flag set, extras skipped |
| **Sort correctness** | `ReverseEdgeSorter` on mock bucket | Edges grouped by child, within-child parents in ascending order |
| **Binary search** | Directory lookup for all children | 100% hit rate for present children, 0 false positives |
| **Directory serialization** | Write & read `.idx` file | Magic, version, entry count preserved; entries exactly match |
| **TOC integration** | `CacheContainerBuilder` with reverse sections | Sections present, seekable, offsets correct, version bumped to 4 |

### Phase 2: Integration Tests (Steps 5–6)

| Test | Dump Size | Focus | Success Criteria |
|------|-----------|-------|------------------|
| **Small (10 MB)** | 10 MB synthetic | Edge completeness | Parent lists match forward-ref enumeration to 100% |
| **Medium (500 MB)** | Real or synthetic | Performance & truncation | <100ms extraction, <50ms sort, truncation rate <0.5% |
| **Large (5–10 GB)** | Real production dump | Bounded memory | Peak memory <600 MB, wall-clock <8 min, no OOM |
| **Concurrent queries** | 5 GB+ | Reader stability | 10 threads querying concurrently, no crashes, p99 latency <50ms |
| **Truncated analysis** | Dump with >10K-parent objects | Fallback behavior | Truncated flag detected; analyzer fallback to enumeration works correctly |

### Phase 3: Regression Tests (Post-Implementation)

| Test | Existing Behavior | Validation |
|------|-------------------|-----------|
| **Phase 1 no regression** | `HeapAnalysisEngine.BuildPhase1IndexAsync()` on 5GB+ | Time +5–10% (edge extraction overhead), memory peak unchanged |
| **Analyzer compatibility** (non-reverse-aware) | StringAnalyzer, AsyncStateMachineAnalyzer | Results identical to v3, no functional change |
| **Cache.bin read (v3→v4)** | v3 cache read by v4 code | Silently skips reverse-index (graceful degradation) |
| **Cache.bin read (v4 by v3)** | v4 cache read by old v3 code | Fails with clear "unsupported version" error, no data corruption |

### Phase 4: Production Validation (Post-Launch)

| Metric | Target | Action if Missed |
|--------|--------|------------------|
| **Truncation rate** | <1% | Lower `MaxParentsPerChild` or increase bucket count |
| **Query latency p99** | <50ms | Profile disk I/O vs. lock contention; implement LRU cache if needed |
| **Cache.bin size growth** | <15% on 25GB dumps | Reassess full-index strategy; consider selective filtering |
| **Analyzer adoption** | ≥50% of analyzers migrated to reverse-aware in 6 months | Evaluate migration difficulty; provide more base-class helpers |

---

## Future Extensions

- **Selective indexing**: Add type-filter option (e.g., "only index edges to Gen2 objects") to reduce disk size for specific workloads.
- **Incremental updates**: If re-running analysis on the same dump, skip extraction/sort if `cache.bin` already valid.
- **Compression**: Apply zstd or deflate to bucket payloads if disk size becomes a bottleneck.
- **Distributed sort**: Spawn sort tasks on multiple cores per bucket for very large buckets.

---

## Reference

- **Binary format**: See [docs/binary-format.md](../binary-format.md) for container layout conventions.
- **Phase 1 architecture**: See [docs/architecture.md § Phase 1](../architecture.md).
- **Existing traversal**: See `RootPathFinder` and `ReferenceGraph` for current reverse-lookup patterns.
