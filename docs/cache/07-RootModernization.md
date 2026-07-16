> **Historical design record — not built, not on the current roadmap.**
> Tier 2 ([14-CleanSlateCacheRedesign.md](14-CleanSlateCacheRedesign.md))
> replaced this graph-based direction with the single-file columnar
> container. See [15-ImplementationRoadmap.md](15-ImplementationRoadmap.md)
> for what's actually being built. Note: Tier 0's Finding 2 already
> deleted `GetRootDescription` as dead code, so the "lazy description"
> motivation below no longer has a live consumer.

# Implementation Specification – Root Modernization

## Goals

Represent roots using ObjectIds while preserving compatibility.

## Current

Roots are primarily address-based.

## Target

RootInfo
- ObjectId
- RootKind
- ThreadAddress (optional)
- Lazy Description

## Migration

HeapAnalysisCache continues exposing address APIs.

Internally prefer ObjectIds.

## ClrMD Notes

Descriptions remain lazy because ToString() may be expensive.

## Edge Cases

Duplicate roots.
Static roots.
Interior roots.
Thread stack roots.
