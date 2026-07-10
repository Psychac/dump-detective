
# Implementation Specification – Dense ObjectId Index

## Background

Current indexes are address-centric.

Future graph algorithms benefit from compact integer identifiers.

## Goals

Assign every indexed object a stable ObjectId.

## Non Goals

- Replace addresses externally.
- Change analyzer APIs.

## Existing Flow

ClrHeap
 -> ObjectIndexWriter
 -> HeapEntry
 -> HeapIndexBuildResult

## Target Flow

ClrHeap
 -> ObjectIndexWriter
 -> HeapEntry(ObjectId)
 -> HeapIndexBuildResult
 -> Address/ObjectId maps

## HeapEntry Changes

Add:

- uint ObjectId

No existing fields removed.

## New Index Structures

Address -> ObjectId

ObjectId -> HeapEntry

Both should be O(1).

## Index Builder Changes

MemoryBackedObjectIndexWriter

- Assign sequential ObjectIds while indexing.

DiskBackedObjectIndexWriter

- Persist ObjectId with HeapEntry.

ObjectIndexReader

- Deserialize ObjectId.

## Public API

Existing address lookups remain.

Add optional helpers:

TryGetObjectId(address)

TryGetHeapEntry(objectId)

## Algorithm

1. Enumerate heap.
2. Allocate next ObjectId.
3. Populate HeapEntry.
4. Store mapping.
5. Persist if disk-backed.

## Complexity

CPU: O(N)

Memory:
+4 bytes per object for ObjectId plus lookup structures.

## ClrMD Notes

ObjectId is DumpDetective-specific.
Never assume ClrMD ordering beyond a single index build.

## Edge Cases

- Duplicate addresses.
- Corrupt heap.
- Invalid objects.
- Disk index versioning.
