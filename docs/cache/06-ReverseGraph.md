
# Implementation Specification – Reverse Graph

## Goals

Support incoming-reference queries without increasing startup cost.

## Non Goals

Automatic construction.

## Design

ReverseGraphCache is independent of the forward graph cache lifecycle.

## Algorithm

1. Ensure forward graph exists.
2. Count incoming edges.
3. Prefix-sum counts.
4. Allocate reverse arrays.
5. Populate reverse edges.
6. Publish immutable graph.

## Consumers

Future:
- Retained size
- Dominators
- Leak root discovery

## Edge Cases

Large fan-in.
Cycles.
Disconnected components.
