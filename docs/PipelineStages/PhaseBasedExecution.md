# ⚙️ Execution Plan: Phase 1 vs Phase 2

## 🎯 Goal
Control cost by splitting analysis into:
- Cheap upfront work
- Expensive on-demand computation

---

# 🥇 Phase 1: Streaming + Indexing (Always Runs)

## ✅ Characteristics
- Single-pass
- Low memory
- No graph building

---

## 🔹 Modules Included

### 1. HeapStreamer
- Enumerates objects

---

### 2. TypeIndexBuilder
- Aggregates:
  - Count
  - Total size

---

### 3. ObjectIndexWriter
- Writes:
  - Address
  - MethodTable
  - Size

---

### 4. SegmentAnalyzer
- Computes:
  - SOH / LOH / POH stats

---

### 5. Basic Thread Scan
- Thread count
- Basic metadata

---

## 📦 Outputs
- TypeIndex
- ObjectIndex (disk)
- SegmentStats
- ThreadSummary

---

## 💸 Cost
- Time: Medium (depends on dump size)
- Memory: Low

---

## 🧠 Used By Sections
- Executive Summary
- Heap Overview
- Type Distribution
- Basic Thread Analysis

---

# 🥈 Phase 2: Targeted Deep Analysis (Lazy)

## ✅ Characteristics
- Runs only when needed
- Focused on small subsets

---

## 🔹 Trigger Strategy

Only run for:
- Top N types (e.g., top 10–20 by size)
- Suspicious objects
- User-selected targets

---

## 🔹 Modules Included

---

### 1. ReferenceGraph (Lazy)
- Computes references on-demand

---

### 2. RootAnalyzer
- Enumerates GC roots
- Builds root paths

---

### 3. LeakDetector
- Applies heuristics:
  - Static retention
  - Event leaks
  - Thread retention

---

### 4. AsyncAnalyzer
- Task discovery
- State machine mapping

---

### 5. StringAnalyzer (Selective)
- Only for:
  - Large strings
  - High-count types

---

### 6. Delegate/Event Analyzer
- Only for:
  - Suspected leak types

---

## 📦 Outputs
- Root paths
- Retention insights
- Leak candidates
- Async/task insights

---

## 💸 Cost
- Time: High (bounded by filtering)
- Memory: Medium (controlled)

---

## 🧠 Used By Sections
- Leak Analysis
- GC Root Analysis
- Retention Graph
- Async Analysis
- Event/Delegate Analysis

---

# 🥉 Phase 3 (Optional): Comparative Analysis

## 🔹 Trigger
- Only if multiple dumps provided

---

## 🔹 Modules
- DiffEngine
- GrowthAnalyzer

---

## 📦 Outputs
- Type growth
- Memory deltas

---

## 💸 Cost
- Time: Medium–High
- Memory: Medium

---

## 🧠 Used By Sections
- Temporal Analysis
- Regression Detection

---

# ⚡ Execution Strategy Summary

| Phase   | Scope              | Cost     | When Runs         |
|--------|-------------------|----------|------------------|
| Phase 1| Full heap scan     | Medium   | Always           |
| Phase 2| Targeted analysis  | High     | On-demand        |
| Phase 3| Multi-dump diff    | High     | Optional         |

---

# 🧠 Smart Optimization Rules

## 🔹 Rule 1: Never Analyze Everything
- Always filter → Top N

---

## 🔹 Rule 2: Depth Limits
- Root path BFS capped

---

## 🔹 Rule 3: Cache Results
- Avoid recomputation

---

## 🔹 Rule 4: Fail Gracefully
- Skip expensive sections if needed

---

# 🚀 Final Insight

This phased model ensures:
- Fast initial results
- Deep insights when needed
- Scalability to massive dumps