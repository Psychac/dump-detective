
# Implementation Specification – Analyzer Migration

## Background

After the graph infrastructure is available, only analyzers that repeatedly discover object relationships should migrate. Heap-scanning analyzers should continue using the HeapIndex.

## Goals

- Reuse ReferenceGraphCache where it provides measurable value.
- Avoid unnecessary ClrMD reference enumeration.
- Preserve existing analyzer outputs.

## Migration Order

1. Reference Chain Analyzer
2. Event Leak Analyzer
3. Delegate/Event Handler Analyzer
4. Retained Object traversal
5. Future Dominator/Retained Size analyzers

## Analyzers That Should Remain Heap-Based

- String Analyzer
- WCF Analyzer
- Type Statistics
- Object counting analyzers
- Any analyzer that only enumerates objects once.

## Migration Strategy

For each analyzer:

1. Identify repeated EnumerateReferences() calls.
2. Replace graph traversal with ReferenceGraphCache.
3. Continue using HeapIndex for metadata lookups.
4. Keep analyzer output unchanged.
5. Benchmark before/after.

## Public API Changes

Prefer dependency on an IReferenceGraphProvider abstraction rather than direct cache access where practical.

## Edge Cases

- Missing graph.
- Lazy graph construction.
- Partial graph availability.
- Invalid ObjectIds.
