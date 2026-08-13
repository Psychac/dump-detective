# DumpDetective

Production-grade .NET memory dump analyzer built on Microsoft.Diagnostics.Runtime (ClrMD 4). Handles very large dumps (1GB-25GB+), huge heaps, millions of heap objects.

## Reference docs
- [docs/architecture.md](docs/architecture.md) — design/architecture
- [docs/binary-format.md](docs/binary-format.md) — disk-based indexing/binary format
- [docs/performance-checklist.md](docs/performance-checklist.md) — perf requirements
- [docs/schema-versioning.md](docs/schema-versioning.md) — schema versioning
- [docs/cache/](docs/cache/) — cache subsystem notes

## Core philosophy (mandatory)

**Never:**
- Materialize the full heap into memory
- Call `.ToList()` on heap enumeration
- Build full object graphs eagerly
- Use LINQ in hot paths
- Store large strings redundantly
- Allocate per-object unnecessarily

**Do:**
- Stream (`yield return`)
- Use `ulong` address as identity
- Prefer `readonly struct` on hot paths
- Use disk-backed indices, built in one pass
- Run deep analysis only on filtered subsets

## Architecture
- Phase 1 — streaming index build: single-pass heap scan, minimal metadata, append-only disk writes
- Phase 2 — on-demand analysis: lazy graph traversal, root-paths on request, scoped expensive ops

Core modules:
- Dump layer: `DumpLoader`, `RuntimeFacade`
- Heap layer: `HeapStreamer`, `HeapEntry`, `TypeIndexBuilder`, object index writers
- Storage: `ObjectIndexReader` (FileStream + ArrayPool), binary format `Address|MethodTable|Size`
- Cache: `HeapAnalysisCache` (type/metadata caches only)
- Graph: `ReferenceGraph`, optional `ReverseReferenceIndex`, `RootPathFinder` (BFS, depth 20)
- Analysis: analyzers (leaks, threads, GCHandles, modules, LOH, etc.)
- Query: `QueryEngine` for index-based queries
- Insight: `InsightEngine` ranks cross-analyzer findings

## Performance rules (strict)

Heap traversal — always stream:
```csharp
foreach (var obj in heap.EnumerateObjects())
```
Never `heap.EnumerateObjects().ToList()`.

Object representation — keep minimal:
```csharp
internal readonly struct HeapEntry
{
    public readonly ulong Address;
    public readonly ulong MethodTable;
    public readonly ulong Size;   // 8 bytes — object size can exceed int.MaxValue on large heaps

    public HeapEntry(ulong address, ulong methodTable, ulong size)
    {
        Address = address;
        MethodTable = methodTable;
        Size = size;
    }
}
```
- Avoid string allocations and per-object heap allocations in hot paths
- Maintain a `string -> int` type id map; intern/dedupe type names
- Use `ArrayPool` for buffers; reuse memory; avoid JSON for large datasets

## Graph rules
- Forward refs: compute lazily from `ClrObject` fields
- Reverse refs: never build the full reverse graph in memory; build partial indexes scoped to types/suspects
- Root paths: BFS with depth limit (20), visited `HashSet<ulong>`, stop early when found

## Leak detection
1. Candidate selection: top types by size, long-lived (Gen2/LOH)
2. Selective analysis: build ref paths, check GC roots, detect static/event/thread retention
3. Score and rank suspects

## Thread analysis
Enumerate threads; group stacks; detect blocking and deadlocks.

## ClrMD usage
- Check `obj.IsValid` and `obj.Type != null`
- Read fields via `field.ReadObject(obj.Address)`
- Cache `ClrType` metadata and field layouts to avoid repeated expensive calls

## Caching
- Allowed: type metadata, `MethodTable -> Type` maps
- Avoid: full object or full-graph caching

## Extensibility — `IAnalyzer`
```csharp
public interface IAnalyzer
{
    string Name { get; }
    string Category { get; }
    IReadOnlyCollection<string> Tags { get; }
    int Order { get; }
    bool IsThreadSafe { get; }
    ValueTask<AnalyzerDomainResult> AnalyzeAsync(AnalysisContext context, CancellationToken cancellationToken);
}
```
Analyzers must return a domain result and call `result.Stamp(this)`.

Analyzers scanning large object populations that encounter malformed/unexpected heap data may
optionally take `ILogger<T>? logger = null` parameter for per-object error/debug diagnostics —
it's resolved automatically via `ActivatorUtilities` in `DefaultAnalyzerFactory`. See
[docs/architecture.md § 14 Observability](docs/architecture.md#14--observability) for the pattern.

Adding a new analyzer:
1. Add `XxxDomainResult` in models
2. Implement `XxxAnalyzer.cs` using streaming heap loops
3. Register in `DefaultAnalyzerFactory`
4. Add finding generator, trend comparer, section builder, tests, docs

## Output
CLI, JSON, and structured reports. Summarize and rank findings; avoid raw dumps.

## Code style
- Avoid LINQ in hot paths; prefer explicit loops
- Use `Span<T>`/`Memory<T>` and `readonly struct` where applicable
- No comments unless explaining non-obvious WHY (see global CLAUDE conventions)

## Testing
Support small and very large dumps. Include perf benchmarks and memory validation.

**NEVER run `DD_RUN_DISCREPANCY_TESTS=1` (or any test that loads a real `.dmp` file) more than
one at a time. NEVER run them via `run_in_background` or in parallel Bash calls.** These dumps are
1GB-25GB+; each test process memory-maps/loads the full dump, and multiple concurrent runs have
repeatedly OOM-crashed the development machine. Always run discrepancy/real-dump tests
one-at-a-time, in the foreground, and wait for each to finish before starting the next — even if
that means several sequential `dotnet test --filter` invocations instead of one combined run.

## Anti-patterns
Don't load the entire heap; don't build full adjacency lists; avoid heavy reflection in hot paths.

## Definition of done
- Works on 10GB+ dumps without crashing
- Bounded memory usage, reasonable runtime, no unnecessary allocations
