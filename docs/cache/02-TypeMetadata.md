
# Implementation Specification – Type Metadata Cache

## Background

MethodTableHasOutgoingRefs repeatedly discovers metadata already available from ClrMD.

## Goals

Cache immutable type information once per MethodTable.

## Non Goals

- Cache ClrType.
- Cache object instances.
- Cache mutable runtime state.

## Data Structure

TypeMetadata

- ulong MethodTable
- bool ContainsPointers
- bool IsArray
- bool ArrayContainsPointers
- bool IsString
- bool IsDelegate
- bool IsException
- bool IsFreeObject
- int InstanceSize
- int ReferenceFieldCount
- ImmutableArray<int> ReferenceFieldOffsets

## Public API

TryGet(MethodTable)

GetOrCreate(MethodTable)

## Algorithm

1. Lookup MethodTable.
2. Return cached metadata if present.
3. Resolve once using ClrMD.
4. Store immutable record.
5. Reuse for future requests.

## ClrMD Notes

Resolve metadata from ClrType only during creation.
Discard ClrType immediately after metadata extraction.

## Consumers

- Event analyzer
- WeakReference analyzer
- WCF analyzer
- Future graph builder
- Heap traversal helpers

## Edge Cases

- Invalid MethodTable.
- Missing metadata.
- Free objects.
- Arrays.
