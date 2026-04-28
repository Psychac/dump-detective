# 🧾 Dump Analyzer Report (Basic / MVP)

## 🎯 Goal
Provide a clear, actionable overview of memory usage and basic diagnostics from a .NET dump, with minimal computation cost.

---

# 🧠 1. Executive Summary

## 📦 Contains
- Total managed memory size
- Total object count
- Top 5 types by memory usage
- Key observations:
  - High memory usage types
  - Unusual object counts
- Basic recommendation hints

---

## 💡 Purpose
Give a quick understanding of:
- “What is using memory?”
- “Is something obviously wrong?”

---

# 🧠 2. Heap Overview

## 🔹 2.1 Memory Summary
### 📦
- Total heap size
- Object count
- Average object size

---

## 🔹 2.2 Generation Breakdown
### 📦
- Gen0 / Gen1 / Gen2 distribution
- LOH size

---

## 🔹 2.3 Segment Overview
### 📦
- Number of segments
- SOH vs LOH split

---

## 💡 Purpose
Understand overall heap structure and memory distribution.

---

# 🧱 3. Type Distribution

## 🔹 3.1 Top Types by Size
### 📦
- Type name
- Object count
- Total size
- % of heap

---

## 🔹 3.2 Top Types by Count
### 📦
- Frequently allocated small objects

---

## 🔹 3.3 Type Summary Table
### 📦
- All types (or top N)
- Sorted by size or count

---

## 💡 Purpose
Identify dominant memory consumers.

---

# 🧵 4. Thread Analysis

## 🔹 4.1 Thread Summary
### 📦
- Total thread count
- Alive vs dead threads

---

## 🔹 4.2 Stack Trace Samples
### 📦
- Sample stack traces
- Grouped by similarity (basic)

---

## 💡 Purpose
Spot:
- Thread buildup
- Repetitive execution patterns

---

# 🧷 5. Handle & GC Root Summary

## 🔹 5.1 Root Counts
### 📦
- Stack roots
- Static roots
- Handle roots

---

## 🔹 5.2 High-Level Observations
### 📦
- Large number of roots
- Suspicious root types

---

## 💡 Purpose
Basic understanding of object retention sources.

---

# 🔥 6. LOH Summary

## 🔹 6.1 Large Object Heap Overview
### 📦
- Total LOH size
- Object count

---

## 🔹 6.2 Top LOH Types
### 📦
- Largest object types in LOH

---

## 💡 Purpose
Detect large allocations and potential inefficiencies.

---

# 🧾 7. Exception Summary

## 🔹 7.1 Exception Types
### 📦
- Count by exception type

---

## 🔹 7.2 Sample Stack Traces
### 📦
- Where exceptions occurred

---

## 💡 Purpose
Identify frequent failures or error-heavy paths.

---

# 📊 8. Basic Insights

## 🔹 8.1 Observations
### 📦
- “Type X consumes 40% of memory”
- “High number of small objects”

---

## 🔹 8.2 Recommendations
### 📦
- “Investigate type X”
- “Review allocation patterns”

---

## 💡 Purpose
Turn raw data into simple actionable hints.

---

# 🧾 9. Appendix (Optional)

## 📦
- Full type table
- Full thread list
- Raw statistics

---

## 💡 Purpose
Provide detailed data for deeper manual inspection.

---

# 🚀 Summary

This report:
- Runs fast
- Uses minimal memory
- Provides immediate value

👉 Ideal for:
- MVP
- CLI output
- Initial debugging