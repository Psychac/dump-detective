
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
