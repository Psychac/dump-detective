# DumpDetective 2.0 — Unified Runtime Diagnostics Architecture

**Status:** Proposed / Architecture Exploration  
**Scope:** .NET memory dumps, ETL/.NET trace files, combined dump+trace analysis, multi-capture trend analysis  
**Primary runtime stack:** .NET 10, ClrMD 4.x, EventPipe/TraceEvent ecosystem  
**Design priority:** correctness → evidence quality → low memory → scalability → performance → simplicity

---

## 1. Executive Summary

DumpDetective should not evolve into “the existing dump analyzer plus trace support.”

If traces, combined investigations, and longitudinal trend analysis are first-class requirements, the current dump-centric mental model becomes a long-term architectural constraint. A ground-up redesign is justified.

The proposed architecture treats DumpDetective as a **runtime evidence processing platform**:

```text
Sources
  ↓
Source-specific observation
  ↓
Evidence
  ↓
Derived evidence / metrics
  ↓
Diagnostic analysis
  ↓
Findings
  ↓
Reports
```

The central architectural idea is that **files are evidence sources, not the unit of analysis**.

A single investigation may contain:

- one dump;
- one trace;
- a dump and a trace covering overlapping time;
- multiple dumps;
- multiple traces;
- repeated dump+trace pairs;
- future source types such as counters or logs.

The model therefore introduces:

```text
Investigation
 └── AnalysisSession
      ├── Source(s)
      ├── Runtime/process identity
      ├── Time context
      ├── Evidence
      ├── Metrics
      └── Findings
```

An investigation may contain many sessions:

```text
Investigation
 ├── Session 1
 │    ├── dump
 │    └── trace
 ├── Session 2
 │    ├── dump
 │    └── trace
 └── Session 3
      └── dump
```

This allows the same engine to produce:

1. single-dump reports;
2. single-trace reports;
3. combined dump+trace reports;
4. dump-vs-dump comparison;
5. trace-vs-trace comparison;
6. longitudinal trend reports;
7. investigation-level diagnostic conclusions.

The most important refinement is to **avoid creating one giant common abstraction that forces dumps and traces to look alike**.

A dump contains a point-in-time heap graph and runtime state.

A trace contains a time-ordered stream of observations.

They share concepts such as process identity, runtime identity, type identity, thread identity, and time, but their raw evidence models are fundamentally different.

Therefore:

> Normalize shared concepts, not fundamentally different evidence.

---

# 2. Goals

## 2.1 Primary goals

The new architecture should:

- support dumps and traces as independent first-class sources;
- support combined dump+trace investigations;
- support multiple captures and trend analysis;
- scale to very large dumps, including 25GB+ dumps and tens of millions of objects;
- avoid materializing complete heaps or complete traces in memory;
- isolate ClrMD and trace-reading implementation details;
- allow analyzers to depend on capabilities/evidence rather than source type;
- support analyzer dependency graphs;
- distinguish raw observations, derived evidence, metrics, and diagnostic conclusions;
- make every conclusion traceable to evidence;
- support confidence and false-positive reasoning;
- make expensive analyses explicit;
- allow analysis profiles to control cost;
- support incremental addition of future source types;
- keep report generation completely separate from analysis;
- provide stable domain contracts suitable for CLI, GUI, API, CI, and automation.

## 2.2 Secondary goals

The architecture should also make it practical to:

- cache expensive derived results;
- reuse analysis across multiple report formats;
- run several independent analyses without rereading expensive source data unnecessarily;
- persist compact investigation summaries;
- compare findings across captures;
- correlate time windows between dump and trace;
- expose explainable diagnostic evidence;
- introduce advanced graph algorithms without coupling them to presentation.

---

# 3. Non-Goals

The redesign should deliberately avoid becoming:

- a generic observability platform;
- a replacement for PerfView;
- a replacement for a full APM backend;
- a general-purpose event database;
- a giant object-relational domain model for every runtime artifact;
- a framework that requires every analyzer to understand every source;
- an abstraction layer that hides important source-specific semantics.

The goal is **diagnostic depth**, not universal telemetry storage.

---

# 4. Core Architectural Principles

## 4.1 Correctness over cleverness

A conclusion that is 95% correct but impossible to explain is worse than a slightly slower conclusion with strong provenance.

Every diagnostic result should be defensible.

## 4.2 Evidence over assumptions

Never conclude:

> “This is a memory leak.”

merely because:

> “The heap is large.”

Instead:

```text
large heap
+
growth across captures
+
objects surviving collections
+
strong retention path
+
allocation/retention relationship
=
high-confidence leak hypothesis
```

The architecture should make this style of reasoning natural.

## 4.3 Streaming over materialization

The default question should be:

> “Can this be processed as a stream?”

before:

> “What collection should we build?”

This applies to both dumps and traces.

## 4.4 Low memory over CPU savings

For large production dumps, additional CPU is generally more acceptable than unbounded working-set growth.

Prefer:

```text
extra reads
extra passes
disk-backed state
```

over:

```text
massive in-memory dictionaries
```

when the trade-off is reasonable.

## 4.5 Source isolation

ClrMD belongs in the dump adapter/engine.

TraceEvent and ETL/nettrace parsing belong in the trace adapter/engine.

The diagnostic domain must not become a wrapper around either library.

## 4.6 Explicit cost

Every expensive analysis should have an identifiable cost profile.

For example:

```text
Heap enumeration        Low/Medium
Type aggregation        Medium
Reference graph         High
Dominator tree          Very High
Full trace CPU stacks   Medium/High
Per-object allocation   Very High
```

The planner should know this before execution.

## 4.7 Immutable outputs

Analysis modules should preferably produce immutable results or append-only/persisted artifacts.

This reduces accidental coupling and makes caching and testing easier.

## 4.8 Deterministic analysis where possible

Given the same source and profile, an analyzer should produce the same logical result.

Nondeterministic ordering should not alter conclusions.

---

# 5. The Fundamental Domain Model

The most important concepts are:

```text
Source
Session
Investigation
Observation
Evidence
Metric
Finding
AnalysisModule
AnalysisPlan
Report
```

They should not be conflated.

---

# 6. Source

A source represents an external evidence container.

Conceptually:

```text
IAnalysisSource
 ├── DumpSource
 ├── TraceSource
 └── FutureSource
```

A source has:

```text
SourceId
SourceType
Path / locator
Metadata
CaptureTime
TimeRange
ProcessIdentity
RuntimeIdentity
Capabilities
```

The source is intentionally not itself an analyzer.

## 6.1 Dump source

A dump source may expose capabilities such as:

```text
Heap
ObjectGraph
Threads
Stacks
Handles
Modules
Runtime
GCSegments
Exceptions
Finalization
```

The underlying implementation may use ClrMD.

The analysis layer must not depend directly on ClrMD types.

## 6.2 Trace source

A trace source may expose:

```text
GC events
Allocation events
CPU samples
Thread events
ThreadPool events
Contention
Exceptions
JIT events
Loader events
Runtime events
```

The underlying implementation may use TraceEvent or other suitable readers.

Again, trace parser types must not leak into the diagnostic domain.

---

# 7. Why Source Alone Is Not Enough

A common design mistake would be:

```text
Dump → DumpAnalysis
Trace → TraceAnalysis
```

That works for the first version but becomes awkward for:

```text
Dump + Trace
```

and much worse for:

```text
Dump1
Dump2
Dump3
Trace1
Trace2
```

The correct abstraction is a **session**.

---

# 8. AnalysisSession

An `AnalysisSession` represents a coherent diagnostic observation.

Example:

```text
Session
├── Identity
├── TimeContext
├── RuntimeIdentity
├── Sources
├── Capabilities
├── Evidence
├── Metrics
└── Findings
```

A session may contain one or multiple sources.

Examples:

### Dump-only

```text
Session
└── app.dmp
```

### Trace-only

```text
Session
└── app.nettrace
```

### Combined

```text
Session
├── app.dmp
└── app.nettrace
```

The session therefore becomes the natural unit for:

- analysis execution;
- correlation;
- provenance;
- reporting.

---

# 9. Investigation

An investigation is a collection of related sessions.

```text
Investigation
├── Session A
├── Session B
├── Session C
└── ...
```

This is the abstraction that enables trends.

Example:

```text
Production memory investigation

Session 1: 09:00 dump
Session 2: 11:00 dump
Session 3: 13:00 dump + trace
Session 4: 15:00 dump + trace
```

The investigation can answer questions that a single session cannot:

- Is memory growing?
- Is retained object population increasing?
- Did allocation pressure change?
- Did GC behavior deteriorate?
- Does the same retention pattern recur?
- Did a finding first appear at a specific point?
- Is the apparent leak persistent or transient?

---

# 10. Runtime and Process Identity

Cross-session analysis must not blindly compare files.

A dump from process A must not accidentally be compared with a dump from process B.

A runtime/process identity should therefore be modeled explicitly.

Conceptually:

```text
RuntimeIdentity
├── RuntimeVersion
├── Architecture
├── OS
├── RuntimeFlavor
└── RuntimeConfiguration

ProcessIdentity
├── ProcessName
├── ProcessId
├── StartTime
├── ApplicationIdentity
└── DeploymentIdentity
```

The exact identity strategy should be conservative.

Process ID alone is not sufficient because PIDs are reused.

Useful correlation signals may include:

- process start time;
- process name;
- runtime identity;
- machine identity when available;
- application/deployment metadata supplied by the caller;
- capture metadata.

A mismatch should lower correlation confidence rather than silently proceeding.

---

# 11. TimeContext

Time becomes a first-class domain concept because combined dump+trace analysis depends on it.

A session should know:

```text
CaptureStart
CaptureEnd
RelevantTime
TimeZone
ClockInformation
```

A trace may cover:

```text
14:00 → 15:00
```

while a dump was captured at:

```text
14:52:31
```

The correlation engine can therefore derive:

```text
Relevant trace window:
14:42:31 → 14:52:31
```

if the analysis profile asks for the preceding ten minutes.

However, time alignment must be treated as a confidence-bearing operation.

Wall-clock timestamps can have:

- clock skew;
- timestamp conversion differences;
- trace capture overhead;
- process start differences;
- incomplete metadata.

Therefore:

```text
CorrelationConfidence
```

should exist.

---

# 12. Do Not Create a Universal Evidence Object

This is a major architectural warning.

A tempting abstraction is:

```csharp
interface IEvidence
{
    Timestamp Timestamp { get; }
    EvidenceKind Kind { get; }
}
```

and then everything becomes an event-like object.

This is wrong for dumps.

A dump may contain:

```text
Object graph
```

which has no intrinsic temporal sequence.

Likewise, forcing trace events into a dump-like object graph is equally wrong.

Instead use:

```text
Common domain concepts
+
Source-specific evidence models
```

---

# 13. Evidence Taxonomy

Evidence should have at least three conceptual layers.

```text
Raw Observation
      ↓
Derived Evidence
      ↓
Diagnostic Finding
```

Metrics form a parallel but connected path:

```text
Raw Observation
      ↓
Aggregation
      ↓
Metric
      ↓
Trend / Finding
```

---

# 14. Raw Observation

Raw observations are facts extracted from a source.

Dump examples:

```text
Object address
Method table
Object size
Object reference
GC segment
Thread
Stack frame
Handle
Exception
Module
Runtime metadata
```

Trace examples:

```text
GC start
GC stop
Allocation event
CPU sample
Thread start
Thread stop
Contention event
Exception event
JIT event
ThreadPool event
```

Raw observations should generally be streamed.

---

# 15. Derived Evidence

Derived evidence is computed from observations.

Examples:

```text
Retained size
Dominated object set
Reference path
Object type histogram
Allocation rate
GC pause distribution
Object survival rate
CPU hotspot
ThreadPool starvation interval
Contention interval
```

Derived evidence is where expensive algorithms live.

It should be independently testable.

---

# 16. Metrics

Metrics are compact quantitative representations.

Examples:

```text
ManagedHeapSize
Gen0Count
Gen1Count
Gen2Count
LOHSize
POHSize
AllocationRate
GCSpentTime
GCMaxPause
CPUProcessPercentage
ThreadPoolQueueLength
ExceptionRate
```

A metric should conceptually include:

```text
MetricId
Value
Unit
Timestamp or TimeRange
Dimensions
Source/Session provenance
Confidence if applicable
```

Metrics are especially important for trend analysis because they provide a compact longitudinal representation.

---

# 17. Findings

A finding is a diagnostic conclusion.

Conceptually:

```text
Finding
├── Id
├── Category
├── Severity
├── Confidence
├── Summary
├── Explanation
├── EvidenceReferences
├── Impact
├── Remediation
├── FalsePositiveConsiderations
└── Provenance
```

Example:

```text
Potential cache retention

Confidence: High

Evidence:
- 4.8 GB managed heap
- 61% of retained heap dominated by cache structure
- object population increased across captures
- allocation activity shows continuous population growth
- objects survive multiple GC cycles

Impact:
Potential unbounded memory growth.

Recommendation:
Review cache eviction and lifetime policy.

False positives:
A deliberately unbounded cache may be expected for this workload.
```

This model makes evidence-based reporting unavoidable.

---

# 18. Provenance

Every derived result should answer:

> Where did this come from?

Provenance should identify:

```text
Source
Session
Analyzer
Algorithm/version
Relevant time range
Object/type/thread identifiers where applicable
```

This is essential for trust.

A report should be able to say:

```text
Finding F123
  ← RetentionAnalyzer vX
  ← Dominator evidence
  ← Dump #3
  ← object 0x...
```

and:

```text
Allocation evidence
  ← AllocationAnalyzer
  ← trace.nettrace
  ← 14:31:00–14:41:00
```

---

# 19. Evidence References vs Embedded Evidence

Findings should preferably reference evidence rather than embed huge evidence structures.

Bad:

```text
Finding
└── List<85000000 objects>
```

Good:

```text
Finding
├── EvidenceReference: DominatorGroup #123
├── EvidenceReference: AllocationWindow #42
└── EvidenceReference: HeapMetric #91
```

The evidence store can retrieve detailed supporting information when needed.

This is particularly important for large dumps.

---

# 20. Source Engines

Source engines are infrastructure boundaries.

```text
Dump Engine
└── ClrMD adapter
```

```text
Trace Engine
└── Trace reader adapter
```

The dump engine should own:

- runtime acquisition;
- heap enumeration;
- segment access;
- object reading;
- type information;
- reference enumeration;
- stack/thread access;
- dump-specific indexing.

The trace engine should own:

- event decoding;
- trace metadata;
- event iteration;
- stack decoding;
- timestamp handling;
- event-specific extraction;
- trace aggregation primitives.

---

# 21. ClrMD Boundary

ClrMD-specific types should remain below the source boundary.

Avoid:

```csharp
Finding Analyze(ClrRuntime runtime)
```

Prefer:

```text
Analyzer
   ↓
HeapProvider / RuntimeEvidenceProvider
   ↓
ClrMD implementation
```

This has several advantages:

1. tests do not require ClrMD for every analyzer;
2. analyzer contracts remain stable when ClrMD changes;
3. source-specific implementation remains isolated;
4. multiple source implementations become possible;
5. domain logic stays readable.

This does not mean every ClrMD call needs another abstraction.

Over-abstraction is also dangerous.

Create boundaries around **meaningful capabilities**, not every method.

---

# 22. Trace Reader Boundary

Likewise, avoid leaking parser event classes through the system.

Prefer:

```text
TraceSource
  ↓
TraceEventProvider
  ↓
Analysis modules
```

The provider can expose streaming domain-oriented records or specialized query interfaces.

Do not automatically convert millions of trace events into a generic object hierarchy.

---

# 23. Streaming Architecture

The default execution pattern should be:

```text
Source
 ↓
Reader
 ↓
Analyzer / Aggregator
 ↓
Small state
 ↓
Output
```

not:

```text
Source
 ↓
List<T>
 ↓
Analyzer
```

For an 85-million-object dump, the second model is unacceptable.

For a large production trace, it may be equally problematic.

---

# 24. Large Dump Processing

A dump analyzer should assume:

```text
Object count = tens of millions
```

and possibly:

```text
Dump size = tens of GB
```

Therefore:

```text
Heap enumeration
    ↓
stream
    ↓
aggregators
```

Object indexes should use:

- compact structures;
- sequential writes;
- disk-backed indexes when appropriate;
- memory-mapped access where beneficial;
- partitioning;
- external sorting where necessary.

The existing DumpDetective disk-backed object index work fits naturally below the new evidence layer.

---

# 25. Large Trace Processing

Trace analysis should use:

```text
event stream
 ↓
filter
 ↓
aggregate
 ↓
discard
```

For example:

```text
10 million allocation events
        ↓
AllocationAggregator
        ↓
per-type counters
per-thread counters
time buckets
stack aggregates
```

There is rarely a reason to retain every event in managed memory.

---

# 26. Aggregation as a First-Class Primitive

Useful reusable aggregation structures include:

```text
Count
Sum
Min/Max
Average
Percentiles
Histogram
Time bucket
Top-K
Distinct count where needed
Grouped aggregation
```

Trace analyzers should reuse these primitives rather than each inventing its own giant dictionary.

The aggregation layer should also understand memory budgets.

For example:

```text
Top 100 allocation types
```

is far cheaper than:

```text
all allocation events by full stack
```

---

# 27. Spillable State

Some analyses require state larger than available RAM.

The architecture should support:

```text
Memory-backed state
Disk-backed state
Hybrid state
```

Example:

```text
Object index
 ├── Memory-backed implementation
 └── Disk-backed implementation
```

The analyzer sees the capability, not the storage implementation.

This is a key place where DumpDetective's existing indexing work can be reused conceptually without preserving the old architecture.

---

# 28. Analysis Modules

Replace the mental model of:

```text
36 analyzers
```

with:

```text
analysis modules operating on evidence
```

A module should declare:

```text
Id
Version
Inputs
Outputs
Dependencies
Cost
Memory profile
Supported source capabilities
```

Conceptually:

```text
AnalysisModule
├── Descriptor
├── Dependencies
├── Input requirements
├── Output types
└── Execute(context)
```

---

# 29. Analyzer Categories

There are three major categories.

## 29.1 Source analysis

Works on one source/session.

Examples:

```text
Heap summary
GC summary
Thread analysis
Exception analysis
Allocation analysis
CPU analysis
```

## 29.2 Cross-source analysis

Consumes evidence from multiple source types.

Examples:

```text
Allocation + retention correlation
GC pauses + heap state
CPU + thread state
Dump heap + trace allocation
```

## 29.3 Cross-session analysis

Consumes multiple sessions.

Examples:

```text
Heap growth trend
Finding recurrence
Object population growth
GC deterioration
Allocation-rate trend
```

---

# 30. Why the Three Categories Matter

Without this distinction, the architecture tends to grow hacks like:

```text
if dump exists...
if trace exists...
if multiple dumps...
if dump and trace...
```

Instead, the engine should reason in terms of dependencies.

Example:

```text
MemoryLeakFinding
requires:
    RetentionEvidence
    AllocationEvidence

RetentionEvidence
requires:
    ObjectGraph

AllocationEvidence
requires:
    AllocationEvents
```

The planner builds the execution graph automatically.

---

# 31. Analysis DAG

The analysis engine should use a dependency DAG.

Example:

```text
Heap Enumeration
       ↓
Object Index
       ↓
Reference Graph
       ↓
Dominator Tree
       ↓
Retention Evidence
       ↓
Memory Leak Finding
```

Trace:

```text
Trace Events
       ↓
Allocation Aggregation
       ↓
Allocation Evidence
       ↓
Allocation Pressure Finding
```

Combined:

```text
Retention Evidence ───┐
                      ├── Memory Correlation
Allocation Evidence ──┘
                      ↓
               Leak Finding
```

This avoids unnecessary work.

---

# 32. Planner Responsibilities

The planner should:

1. inspect available source capabilities;
2. inspect requested analysis profile;
3. resolve dependencies;
4. remove duplicate work;
5. estimate cost;
6. choose implementations;
7. schedule independent work;
8. enforce memory/resource constraints;
9. determine required persistence;
10. produce an executable plan.

---

# 33. Resource-Aware Planning

The planner should eventually understand:

```text
Available RAM
CPU budget
Disk availability
Source size
Expected object count
Trace size
Requested analysis depth
```

A 25GB dump should not receive the same strategy as a 300MB dump.

Possible decisions:

```text
Small dump
 → memory-backed index

Large dump
 → disk-backed index

Very large dump
 → partitioned / external processing
```

The decision should be explicit and observable.

---

# 34. Analysis Profiles

Profiles prevent every analysis from running everything.

Possible profiles:

```text
Quick
Memory
Performance
GC
Leak Investigation
Full
Trend
```

Example:

### Quick

```text
Runtime
Heap summary
Basic GC
Basic thread summary
```

### Memory

```text
Heap
Types
Retention
Dominators
Large objects
```

### Performance trace

```text
CPU
GC
ThreadPool
Contention
Allocations
```

### Full combined

```text
Dump analysis
+
Trace analysis
+
Correlation
```

---

# 35. Cost Model

Every expensive module should expose a rough cost classification.

For example:

```text
Cost
├── Source IO
├── CPU
├── Memory
├── Disk
└── Multi-pass requirement
```

A simple first implementation could classify modules:

```text
Low
Medium
High
VeryHigh
```

Later this can become estimated numeric resource requirements.

The purpose is not perfect prediction.

The purpose is to make expensive work visible and controllable.

---

# 36. Scheduling

The execution engine should allow parallelism only where it is safe.

For example:

```text
Heap summary ──────┐
Thread analysis ───┼──► Finding stage
Module analysis ───┘
```

may run concurrently if source access and memory budgets permit.

But:

```text
Heap enumeration
       ↓
Dominator tree
```

is inherently dependent.

The scheduler should not maximize parallelism blindly.

For huge dumps:

> lower memory pressure is more important than saturating all cores.

---

# 37. Evidence Store

The evidence store should not be one giant database.

Use multiple storage modes.

```text
EvidenceStore
├── Ephemeral memory
├── Compact persisted artifacts
├── Disk-backed indexes
└── Source-backed lazy evidence
```

Examples:

### Memory

```text
Finding summaries
small metric sets
analysis metadata
```

### Disk

```text
object indexes
large aggregation tables
dominance results
large evidence collections
```

### Source-backed

```text
raw heap objects
raw trace events
stack frames
```

---

# 38. Evidence Lifecycle

Evidence should have a lifecycle.

```text
Created
 ↓
Available
 ↓
Consumed
 ↓
Cached / persisted
 ↓
Evicted
```

Not all evidence needs permanent retention.

A useful policy is:

```text
Keep:
    small summaries
    findings
    important evidence references

Discard:
    intermediate streams
    temporary aggregation state

Persist:
    expensive reusable structures
```

This is important for repeated report generation.

---

# 39. Caching

Cache expensive work, not everything.

Good candidates:

```text
Object index
Type metadata
Dominator results
Aggregated trace data
Metric summaries
```

Poor candidates:

```text
every raw object
every raw trace event
every temporary iterator
```

Caches need versioning:

```text
Source identity
Analyzer/module version
Algorithm version
Configuration/profile
```

Otherwise stale evidence can silently produce incorrect findings.

---

# 40. Object Identity

Object addresses are only meaningful within a particular process/dump.

Never treat:

```text
0x123456
```

as a globally stable object ID.

Use:

```text
ObjectIdentity
├── SessionId
├── SourceId
└── Address
```

For cross-dump trend analysis, use stable higher-level identities where possible:

```text
Type
Path/signature
allocation site
logical grouping
```

rather than pretending individual object addresses survive across captures.

---

# 41. Type Identity

Type identity also needs care.

A simple type name can be ambiguous.

Potential identity dimensions:

```text
Assembly
Module
Namespace
TypeName
MethodTable
Runtime/session
```

Method table identity is excellent within a dump but should not be blindly assumed globally stable across captures.

Trend analysis should have a normalized type identity layer.

---

# 42. Thread Identity

Thread IDs can be reused.

For a session:

```text
ThreadIdentity
├── SessionId
├── OS Thread ID
└── runtime-specific identity where available
```

Across sessions, correlation should use richer signals where possible:

- thread role;
- stack signature;
- activity;
- managed thread characteristics;
- capture metadata.

Do not claim two thread IDs represent the same thread merely because the numeric ID matches.

---

# 43. Temporal Correlation Algorithm

For dump+trace:

1. identify process/runtime;
2. establish capture timestamp;
3. establish trace timeline;
4. estimate clock alignment;
5. calculate overlap;
6. select relevant trace window;
7. attach confidence;
8. run correlation modules.

Example:

```text
Trace:
09:00 ───────────────────────── 10:00

Dump:
                         ▲
                       09:52
```

The correlator might analyze:

```text
09:42 → 09:52
```

for a ten-minute pre-dump window.

The window should be configurable by analysis profile.

---

# 44. Correlation Must Not Imply Causation

This is one of the most important diagnostic rules.

If:

```text
allocation rate ↑
heap size ↑
```

that is correlation.

It does not prove:

```text
allocation caused the retained heap
```

To increase confidence, look for additional evidence:

```text
allocation ↑
+
object survival ↑
+
retention ↑
+
same type population ↑
```

The finding should explicitly distinguish:

```text
Observed
Inferred
Hypothesized
```

---

# 45. Confidence Model

Confidence should be evidence-based.

A useful conceptual model:

```text
Confidence
├── High
├── Medium
└── Low
```

But the internal implementation can eventually use a score.

For example:

```text
Evidence strength
Source quality
Correlation quality
Alternative explanations
Data completeness
```

A finding should also explain why confidence is high or low.

---

# 46. False Positive Model

Every important finding should document plausible alternatives.

Example:

```text
Finding:
Large cache retention

Alternative:
Cache is intentionally unbounded.

Counter-evidence:
Object population grows monotonically across four captures.

Conclusion:
Leak hypothesis remains strong.
```

This makes the tool much more useful to production engineers.

---

# 47. Finding Severity

Severity should be distinct from confidence.

```text
Severity:
Low / Medium / High / Critical

Confidence:
Low / Medium / High
```

Example:

```text
Critical impact + Low confidence
```

can be a valid result.

Do not collapse the two.

---

# 48. Metrics vs Findings

A metric says:

```text
Gen2 pause P99 = 410 ms
```

A finding says:

```text
Long Gen2 pauses are likely contributing to request latency.
```

The latter requires contextual reasoning.

Reports should show both:

```text
Metric
Evidence
Interpretation
```

---

# 49. Trend Analysis

Trend analysis should operate over normalized metrics and findings.

Input:

```text
Session 1
Session 2
Session 3
Session 4
```

Processing:

```text
Normalize
 ↓
Align
 ↓
Aggregate
 ↓
Detect trend
 ↓
Generate trend finding
```

---

# 50. Trend Metrics

Examples:

```text
Managed heap size
LOH size
POH size
Gen2 size
Object count
Selected type population
Retained size
Allocation rate
GC frequency
GC pause time
CPU
ThreadPool queue
Exception rate
```

---

# 51. Trend Detection

Start simple.

Useful methods:

### Absolute delta

```text
current - previous
```

### Percentage delta

```text
(current - previous) / previous
```

### Linear slope

Useful for:

```text
heap growth
allocation rate
object population
```

### Moving average

Useful for noisy metrics.

### Persistence

A trend should ideally recur across multiple captures.

One spike should not automatically become a leak finding.

---

# 52. Finding Recurrence

A particularly powerful trend concept is recurrence.

Example:

```text
Session 1:
No cache-retention finding

Session 2:
Weak

Session 3:
Medium

Session 4:
High
```

The trend analyzer can conclude:

```text
The same retention pattern is strengthening over time.
```

This is often more useful than raw heap size.

---

# 53. Trend Alignment Problems

Comparisons become invalid when:

- workloads differ;
- deployments changed;
- runtime versions changed;
- process identity changed;
- capture timing differs;
- traffic volume changed;
- trace duration differs.

Therefore each trend should expose:

```text
Comparability
```

For example:

```text
High comparability
Moderate comparability
Low comparability
```

and explain why.

---

# 54. Normalization

Some metrics need normalization.

Example:

```text
Allocations = 10 GB
```

is meaningless without workload context.

Better:

```text
Allocation rate = 180 MB/s
```

or:

```text
Allocation / request
```

where request counts are available.

The trend layer should support dimensions:

```text
Metric:
AllocationRate

Dimensions:
Application
Process
Thread
Type
Generation
```

but should avoid exploding cardinality.

---

# 55. High Cardinality Warning

Trace analysis can accidentally create:

```text
type × stack × thread × timestamp
```

with enormous cardinality.

The architecture should make cardinality limits explicit.

Useful techniques:

- top-K;
- heavy hitters;
- time bucketing;
- stack normalization;
- sampling;
- configurable dimensions;
- spill-to-disk aggregation.

---

# 56. Advanced Graph Analysis

The architecture should make graph algorithms independent modules.

Example:

```text
Object graph
 ↓
Dominator tree
 ↓
Retained-size groups
 ↓
Retention findings
```

The same architecture can later support:

- Lengauer–Tarjan dominators;
- SCC analysis;
- reachability;
- shortest retention paths;
- bipartite grouping;
- graph condensation;
- reference-pattern detection.

These algorithms should consume a graph capability and produce derived evidence.

They should not know about reports.

---

# 57. Dominator Analysis

Dominator analysis should be treated as an expensive derived-evidence module.

Potential output:

```text
DominatorNode
├── Object/type group
├── Retained size
├── Dominated count
├── Immediate dominator
└── Provenance
```

For very large heaps:

- avoid constructing a naïve object graph in RAM;
- use compact node IDs;
- stream edge generation;
- use external storage where necessary;
- use partitioning if required;
- avoid retaining unnecessary metadata.

The exact algorithm and storage strategy should be benchmark-driven.

---

# 58. Object-Level vs Group-Level Analysis

Not every question needs individual objects.

Prefer group-level analysis where possible:

```text
Type
Assembly
Namespace
Root path
Dominator group
Allocation site
```

Example:

```text
85 million objects
```

may reduce to:

```text
15,000 meaningful type groups
```

before expensive analysis is applied.

This is a major scalability strategy.

---

# 59. Two-Phase Analysis

A strong pattern for large sources is:

```text
Phase 1: cheap broad scan
Phase 2: expensive targeted analysis
```

Example:

```text
Phase 1
 ├── heap size
 ├── type histogram
 ├── generation distribution
 └── top retained candidates

Phase 2
 └── dominator analysis only for relevant candidates
```

This should be the default philosophy for expensive analyzers.

---

# 60. Analysis Scope

Modules should support scope where meaningful.

Examples:

```text
All heap
Top 100 types
Specific type
Specific namespace
Specific dominator
Specific time range
Specific thread
```

This allows targeted deep dives.

---

# 61. Reports

Reporting must be downstream of findings and evidence.

```text
Analysis
 ↓
Findings
 ↓
Report model
 ↓
Renderer
```

No analyzer should generate Markdown, HTML, PDF, or UI structures.

---

# 62. Report Types

The architecture should support:

```text
DumpReport
TraceReport
CombinedReport
ComparisonReport
TrendReport
InvestigationReport
```

These are presentation strategies, not separate analysis engines.

---

# 63. Dump Report

Potential sections:

```text
Executive Summary
Runtime
Process
Memory Overview
Heap Composition
GC
Object Retention
Dominators
Threads
Exceptions
Finalization
Handles
Findings
Recommendations
```

Sections should be omitted when their evidence is unavailable.

---

# 64. Trace Report

Potential sections:

```text
Executive Summary
Trace Metadata
CPU
GC
Allocations
ThreadPool
Contention
Exceptions
JIT
Runtime Activity
Timeline
Findings
Recommendations
```

---

# 65. Combined Report

The combined report should explicitly separate:

```text
Observed in dump
Observed in trace
Correlation
Diagnostic conclusion
```

Example:

```text
Dump:
4.8 GB retained by cache.

Trace:
180 MB/s allocation into cache-related types.

Correlation:
Allocation pressure precedes heap growth.

Conclusion:
High-confidence evidence of cache growth.
```

This is much stronger than dumping two unrelated reports side by side.

---

# 66. Investigation Report

An investigation report should focus on the story:

```text
What changed?
Why did it change?
What evidence supports that?
Is it persistent?
What should be investigated next?
```

A possible structure:

```text
Executive Summary
Timeline
Baseline
Current State
Memory Trend
GC Trend
Allocation Trend
Recurring Findings
Emerging Findings
Correlations
Root Cause Candidates
Confidence
Recommendations
Appendix
```

---

# 67. Report Composition

Use report sections as independent contributors.

Conceptually:

```text
IReportSection
├── Title
├── Applicability
├── Required evidence
└── Render data
```

The report engine selects sections based on available evidence.

This avoids:

```csharp
if (trace != null)
...
if (dump != null)
...
```

being duplicated throughout the report code.

---

# 68. Report Model

Reports should first be represented as a structured model:

```text
Report
├── Metadata
├── ExecutiveSummary
├── Sections[]
├── Findings[]
├── Metrics[]
└── EvidenceReferences[]
```

Then render:

```text
Report
 ├── Markdown
 ├── HTML
 ├── JSON
 └── future formats
```

This also makes machine-readable output possible.

---

# 69. Machine-Readable Output

JSON should be considered a first-class output.

Useful for:

- CI;
- automated regression detection;
- dashboards;
- other tools;
- LLM-assisted investigation;
- trend storage.

The JSON schema should be versioned.

---

# 70. CLI Architecture

The CLI should be thin.

Conceptually:

```text
CLI
 ↓
Parse request
 ↓
Create AnalysisRequest
 ↓
Engine
 ↓
Report
```

Commands could be:

```bash
dumpdetective analyze app.dmp
dumpdetective analyze app.nettrace
dumpdetective investigate --dump app.dmp --trace app.nettrace
dumpdetective compare dump1.dmp dump2.dmp
dumpdetective trend ./captures
```

The CLI should not contain analyzer logic.

---

# 71. AnalysisRequest

The public entry point should be an `AnalysisRequest`.

Conceptually:

```text
AnalysisRequest
├── Sources[]
├── Profile
├── Scope
├── AnalysisOptions
├── ResourceLimits
└── OutputOptions
```

This request can be created by:

- CLI;
- GUI;
- HTTP API;
- tests;
- CI pipeline.

---

# 72. Resource Limits

Large-dump diagnostics should allow explicit limits:

```text
MaxMemory
MaxDisk
MaxCpu
MaxAnalysisDuration
MaxTopK
MaxTraceWindow
```

The system should fail gracefully when limits prevent an analysis.

A partial result should say:

```text
Analysis incomplete
Reason:
memory budget exceeded

Completed:
heap summary
type aggregation

Skipped:
full dominator analysis
```

This is far better than an opaque out-of-memory crash.

---

# 73. Failure Model

Each module should be able to produce:

```text
Completed
Partial
Skipped
Failed
Unsupported
```

A failure in one analyzer should not necessarily invalidate the whole investigation.

Example:

```text
CPU analysis: completed
GC analysis: completed
Allocation analysis: completed
Dominator analysis: failed
```

The report can still be useful.

---

# 74. Capability Model

Sources should expose capabilities.

Example:

```text
Dump:
Heap
ObjectGraph
Threads
Stacks
Handles
GC
Runtime

Trace:
Allocations
GCEvents
CPU
Threads
Contention
Exceptions
```

Analyzer descriptors say:

```text
Requires:
ObjectGraph
```

rather than:

```text
Requires:
DumpSource
```

This is a crucial refinement.

It means a future source could provide object graph information without pretending to be a dump.

---

# 75. Capability vs Source Type

Prefer:

```text
Requires ObjectGraph
```

over:

```text
RunsOn Dump
```

because the former describes what the algorithm actually needs.

Source type can still be used where semantics genuinely differ.

---

# 76. Evidence Dependencies

A module may require:

```text
Raw evidence
```

or:

```text
Derived evidence
```

Example:

```text
LeakFinding
requires:
    RetentionEvidence
    AllocationEvidence
```

This naturally creates the DAG.

---

# 77. Module Versioning

Analysis results should be tied to module versions.

If:

```text
Dominator algorithm changes
```

cached evidence may become invalid.

Cache keys should therefore include:

```text
Source identity
Module ID
Module version
Algorithm version
Configuration
```

---

# 78. Configuration and Reproducibility

A report should be reproducible from:

```text
Input identity
Analysis profile
Configuration
Module versions
Tool version
```

Store this metadata in the report.

---

# 79. Testing Architecture

Testing should happen at multiple levels.

## Unit tests

Test:

```text
aggregation
normalization
trend detection
graph algorithms
correlation logic
confidence logic
```

without actual dumps/traces where possible.

## Source integration tests

Test:

```text
ClrMD adapters
trace readers
```

against known artifacts.

## Analyzer integration tests

Test:

```text
source → evidence → finding
```

## End-to-end tests

Test:

```text
source files
→ analysis plan
→ findings
→ report
```

---

# 80. Golden Evidence

For complex analyzers, store compact golden evidence rather than requiring a massive production dump for every unit test.

Example:

```text
10-object graph
known dominator tree
expected retained sizes
```

Then run large real dumps as performance/integration tests.

---

# 81. Performance Testing

Benchmark at realistic scales:

```text
300 MB dump
1 GB dump
3.3 GB dump
10 GB dump
25+ GB dump
```

and:

```text
small trace
medium trace
large trace
long-duration trace
high-allocation trace
```

Track:

```text
Elapsed time
Peak working set
Managed heap
Disk IO
CPU
Temporary disk
GC behavior
```

Peak working set should be treated as a first-class metric.

---

# 82. Analyzer Memory Budgets

Each expensive module should have a documented memory strategy.

Example:

```text
Type histogram:
O(number of types)

Object index:
O(number of objects) but spillable

Reference graph:
O(number of edges) and likely spillable

Dominator:
O(nodes + edges), implementation-dependent
```

Complexity must include storage implications, not only Big-O.

---

# 83. Complexity Documentation

Every module should document:

```text
Input size
Time complexity
Memory complexity
Disk complexity
Number of source passes
Parallelism
```

For a heap analyzer:

```text
N = objects
E = references

Enumeration:
O(N)

Reference graph:
O(N + E)

Dominator:
algorithm-dependent, generally graph-scale

Memory:
must not be assumed O(N) in RAM
```

The implementation should explicitly distinguish:

```text
logical complexity
physical memory residency
```

---

# 84. Multi-Pass Source Access

Some analyses require multiple passes.

That is acceptable if documented.

For example:

```text
Pass 1:
type histogram

Pass 2:
candidate selection

Pass 3:
reference analysis
```

The architecture should not force one-pass processing when that would dramatically increase memory.

---

# 85. Source Rewindability

Sources should expose whether they can be reread cheaply.

Conceptually:

```text
SourceCharacteristics
├── Streamable
├── Rewindable
├── RandomAccess
├── Indexed
└── ExpensiveToReopen
```

This helps the planner decide between:

```text
reread source
```

and:

```text
materialize compact intermediate evidence
```

---

# 86. Trace Time Indexing

For large traces, time-window analysis benefits from lightweight indexing.

Possible structure:

```text
Time
 ↓
chunk offsets
 ↓
trace reader
```

Then:

```text
Analyze 14:30–14:40
```

does not require replaying a multi-hour trace if the format/reader permits efficient seeking.

This should be benchmarked rather than assumed.

---

# 87. Event Filtering

Trace processing should push filters as close to the reader as practical.

Bad:

```text
decode everything
 ↓
discard 90%
```

Better:

```text
reader/event provider
 ↓
requested event classes
 ↓
analysis
```

This reduces CPU and memory pressure.

---

# 88. Stack Analysis

Stack data can be high-cardinality.

Normalize stacks where appropriate:

```text
raw stack
 ↓
canonical stack identity
 ↓
aggregation
```

But preserve enough information for diagnostics.

Avoid storing the same full stack repeatedly.

---

# 89. Allocation Analysis

Allocation analysis should support levels.

### Level 1

```text
total allocation rate
```

### Level 2

```text
allocation by type
```

### Level 3

```text
allocation by thread
```

### Level 4

```text
allocation by stack
```

### Level 5

```text
allocation by type × stack × time
```

The latter can explode.

Profiles should control depth.

---

# 90. GC Analysis

GC analysis should combine:

From dumps:

```text
heap composition
segments
generations
object populations
runtime state
```

From traces:

```text
GC starts/stops
pause durations
GC reasons
allocation pressure
survival indicators where available
```

Combined analysis can ask:

```text
Is the large heap accompanied by sustained allocation pressure?
Are long GC pauses associated with heap growth?
Does the dump show retention that explains the observed pressure?
```

---

# 91. Memory Leak Analysis

A leak analyzer should not be one giant algorithm.

Break it down:

```text
Heap composition
 ↓
Candidate growth
 ↓
Retention
 ↓
Root analysis
 ↓
Temporal evidence
 ↓
Allocation evidence
 ↓
Leak hypothesis
```

This makes each stage independently testable.

---

# 92. Leak Hypothesis

A finding should distinguish:

```text
Large
Retained
Growing
Unexpected
```

These are different properties.

A large object population is not necessarily:

- retained unexpectedly;
- growing;
- leaked.

A strong leak finding needs multiple dimensions.

---

# 93. Combined Leak Analysis

Potential pipeline:

```text
Dump #1
   ↓
retention baseline

Dump #2
   ↓
growth evidence

Trace
   ↓
allocation evidence

Dump #2
   ↓
root evidence

Correlation
   ↓
Leak hypothesis
```

This is the sort of analysis that justifies the architectural rewrite.

---

# 94. Cross-Session Object Analysis

Do not attempt to match individual object addresses.

Instead compare:

```text
Type population
Dominator groups
Retention paths
Allocation sites
Object size distributions
```

Potential matching signature:

```text
Type
+
retention root signature
+
assembly
+
size distribution
```

This is a heuristic and should be labeled as such.

---

# 95. Finding Identity Across Sessions

A finding should have a normalized identity.

Example:

```text
FindingSignature:
    Category = MemoryRetention
    Type = Some.Cache.Entry
    Dominator = SomeCache
```

Then:

```text
Session 1 → absent
Session 2 → present
Session 3 → stronger
```

The trend engine can identify recurrence.

Finding signatures must not depend on volatile addresses.

---

# 96. Diagnostic Narrative

The report layer should be able to construct:

```text
Observation
 ↓
Evidence
 ↓
Interpretation
 ↓
Impact
 ↓
Recommendation
```

This should not be generated by the analyzer as prose.

The analyzer produces structured facts.

The report layer turns those facts into readable output.

---

# 97. Explainability

Every finding should support drill-down:

```text
Finding
 ↓
Evidence
 ↓
Underlying metric
 ↓
Source
 ↓
Exact source reference
```

For example:

```text
Why is this a leak?

→ 1.8 GB retained
→ 74% dominated by cache
→ same signature increased across 4 captures
→ allocation rate sustained
→ objects survive multiple GCs
```

---

# 98. Future Source Types

The architecture should allow:

```text
Dump
Trace
Counter snapshot
Perf data
Logs
Application telemetry
```

but adding a new source should not require changing:

```text
Finding
Report
Investigation
Metric
```

Instead it should implement source capabilities and evidence providers.

---

# 99. Plugin Architecture

Do not implement a dynamic plugin system immediately unless required.

Start with compile-time module registration.

Example:

```text
ModuleRegistry
```

Later, if external analyzers are needed:

```text
IAnalysisModule
```

can become the plugin contract.

Premature plugin loading adds:

- versioning complexity;
- dependency isolation issues;
- security concerns;
- deployment complexity.

Keep the internal architecture plugin-friendly without requiring external plugins.

---

# 100. Dependency Injection

Use DI for:

- source factories;
- module registry;
- storage providers;
- report renderers;
- clocks/time services;
- logging.

Do not use DI to hide every object.

Simple domain structures should remain simple.

---

# 101. Domain/Core Assembly

The core should contain concepts such as:

```text
AnalysisRequest
AnalysisSession
Investigation
SourceMetadata
RuntimeIdentity
EvidenceReference
Metric
Finding
AnalysisResult
AnalysisPlan
```

It should not reference:

```text
ClrMD
TraceEvent
ASP.NET
CLI
HTML renderer
```

This is the architectural center.

---

# 102. Infrastructure Assemblies

Potential organization:

```text
DumpDetective.Core
DumpDetective.Dumps
DumpDetective.Traces
DumpDetective.Analysis
DumpDetective.Storage
DumpDetective.Reporting
DumpDetective.Cli
DumpDetective.Tests
```

A further split can happen when complexity justifies it.

Do not create 30 projects on day one.

---

# 103. Suggested Dependency Direction

```text
CLI
 ↓
Analysis Engine
 ↓
Core

Dump adapter ──┐
Trace adapter ─┼──► Core contracts
Storage ───────┘

Reporting ─────► Core analysis outputs
```

Core should not depend upward.

---

# 104. Potential Namespace Structure

```text
DumpDetective.Core
    Analysis
    Evidence
    Findings
    Metrics
    Sessions
    Sources
    Time
    Identity

DumpDetective.Dumps
    ClrMD
    Heap
    Objects
    Graph
    Runtime

DumpDetective.Traces
    TraceEvent
    Events
    Timeline
    Aggregation

DumpDetective.Analysis
    Modules
    Planning
    Scheduling
    Correlation
    Trends

DumpDetective.Storage
    Memory
    Disk
    Mmap
    Temporary

DumpDetective.Reporting
    Model
    Sections
    Markdown
    Html
    Json
```

---

# 105. Important Architectural Refinement: Avoid an “Evidence Bus”

A message bus can sound attractive:

```text
Analyzer A publishes evidence
Analyzer B subscribes
```

For an offline diagnostic tool this may be unnecessary complexity.

Prefer a deterministic dependency graph:

```text
A produces X
B requires X
```

and explicit evidence stores.

A bus is useful when:

- processing is distributed;
- streaming consumers need independent lifetimes;
- external integrations require events.

Neither is necessarily true initially.

---

# 106. Important Refinement: Avoid a Generic Query Language Initially

Do not build:

```text
DumpDetective Query Language
```

just because multiple evidence sources exist.

Start with strongly typed capabilities and APIs.

A query abstraction can emerge later when repeated patterns justify it.

---

# 107. Important Refinement: Don't Make Everything Lazy

Lazy evaluation is useful for:

```text
raw objects
trace events
expensive source reads
```

but excessive laziness makes execution unpredictable.

Derived evidence should generally have explicit lifecycle and ownership.

Prefer:

```text
stream raw source
produce defined evidence artifact
```

rather than an enormous web of deferred computations.

---

# 108. Important Refinement: Don't Persist Everything

Persistence is expensive.

Only persist:

- reusable expensive artifacts;
- compact investigation summaries;
- user-requested exports.

Temporary artifacts should be deleted automatically.

---

# 109. Important Refinement: Don't Over-Parallelize

For a 25GB dump:

```text
8 analyzers × large working sets
```

can be much worse than:

```text
2 analyzers × controlled memory
```

The scheduler should have a resource budget.

---

# 110. Important Refinement: Analyzer Independence

Modules should be independent in code and semantics.

But not all modules need independent source scans.

The planner should maximize **shared evidence**.

For example:

```text
Heap enumeration
       ↓
 ┌─────┼──────┐
 ↓     ↓      ↓
Type  GC    Candidate
stats stats selection
```

One expensive scan can feed several analyses.

---

# 111. Evidence Reuse

This is one of the biggest performance opportunities.

Instead of:

```text
Analyzer A → enumerate heap
Analyzer B → enumerate heap
Analyzer C → enumerate heap
```

do:

```text
Heap observation pass
        ↓
Shared derived evidence
   ├── A
   ├── B
   └── C
```

The planner should identify shared dependencies.

---

# 112. Source Pass Planning

For dumps, source passes are expensive.

The planner should ideally understand:

```text
Pass A:
needs object headers

Pass B:
needs references

Pass C:
needs stacks
```

and combine compatible requests where practical.

However, don't combine everything automatically.

A giant “do everything” pass may increase memory dramatically.

The planner needs to balance:

```text
fewer passes
vs
smaller state
```

---

# 113. Memory Budget Model

A useful future model:

```text
Total budget
 ├── source reader
 ├── shared cache
 ├── module A
 ├── module B
 ├── scheduler overhead
 └── safety margin
```

The engine should reserve headroom rather than consume 100% of available memory.

---

# 114. Disk Budget Model

Disk-backed analysis needs:

```text
estimated temporary space
actual temporary space
cleanup policy
```

For example:

```text
Object index
Reference edge store
Dominator workspace
Trace aggregation spill
```

Temporary data should use a dedicated workspace.

---

# 115. Crash Recovery

For very long analyses, consider checkpointing expensive phases.

Example:

```text
Heap indexing
100%
```

then:

```text
Dominator calculation
```

If the process crashes, rebuilding the heap index should not necessarily be required.

This becomes important for multi-hour analysis.

Checkpointing should be introduced only where the cost justifies it.

---

# 116. Cancellation

Every module should support cancellation.

Cancellation should propagate:

```text
CLI
 ↓
Analysis Engine
 ↓
Scheduler
 ↓
Source reader
 ↓
Analyzer
```

Temporary artifacts must be cleaned up.

---

# 117. Logging

Logs should be operational rather than diagnostic prose.

Useful:

```text
Started HeapEnumeration
Processed 40M objects
Peak WS = 4.7GB
Temporary index = 8.2GB
Completed in 119s
```

Avoid dumping millions of object-level log lines.

---

# 118. Progress Reporting

Large analyses need meaningful progress.

Examples:

```text
Heap scan: 72%
Objects: 61M / ~85M
Elapsed: 93s
ETA: 36s
```

For traces:

```text
Trace processing: 61%
Time: 37m / 60m
```

Progress should be based on source characteristics where possible.

---

# 119. Observability of DumpDetective Itself

DumpDetective should measure itself.

Record:

```text
Peak working set
Managed allocations
GC collections
CPU
Disk reads/writes
Temporary disk
Source passes
Analyzer durations
```

This will be critical for tuning the architecture.

---

# 120. Performance Regression Suite

Keep a corpus of representative dumps/traces.

For each:

```text
Analyzer
Elapsed
Peak WS
Disk
Result checksum/signature
```

A change that reduces runtime by 10% but increases peak working set from 5GB to 15GB may be a regression.

---

# 121. Data Correctness Regression

Performance tests are not enough.

Store expected:

```text
heap totals
type counts
GC counts
finding signatures
retained-size totals
```

Then detect semantic regressions.

---

# 122. Report Stability

Reports should be stable enough for automated comparison.

Do not include nondeterministic ordering.

Sort by:

```text
severity
impact
confidence
stable identity
```

where appropriate.

---

# 123. Security

Dump and trace files may contain sensitive production information.

The architecture should assume:

- secrets may exist in memory;
- strings may contain PII;
- paths may contain sensitive data;
- stack traces may expose internal systems.

Reports should avoid dumping raw strings or object contents unnecessarily.

Redaction should be possible at the report layer.

---

# 124. Privacy by Design

Raw evidence should not automatically be persisted.

Especially avoid storing:

```text
all strings
all object contents
all trace payloads
```

when the diagnostic conclusion only needs:

```text
type
size
count
retention path
```

---

# 125. Configuration Layers

Prefer:

```text
Defaults
 ↓
Profile
 ↓
Request overrides
 ↓
Analyzer-specific options
```

Avoid a single giant configuration class.

---

# 126. Versioning

The report and machine-readable output need schema versions.

Example:

```text
ReportSchemaVersion
EvidenceSchemaVersion
```

Analyzer versions should be independent.

---

# 127. Recommended End-to-End Pipeline

The final execution model should look like:

```text
User Request
    ↓
AnalysisRequest
    ↓
Source Discovery
    ↓
Session Construction
    ↓
Capability Discovery
    ↓
Analysis Planning
    ↓
Resource Planning
    ↓
Execution DAG
    ↓
Raw Evidence
    ↓
Derived Evidence
    ↓
Metrics
    ↓
Cross-source Correlation
    ↓
Cross-session Trend Analysis
    ↓
Findings
    ↓
Report Model
    ↓
Renderer
```

---

# 128. Example: Single Dump

```text
app.dmp
 ↓
DumpSource
 ↓
Capabilities
 ↓
Plan
 ↓
Heap enumeration
 ↓
Type metrics
 ↓
Retention analysis
 ↓
Dominator analysis
 ↓
Findings
 ↓
Dump report
```

---

# 129. Example: Single Trace

```text
app.nettrace
 ↓
TraceSource
 ↓
Capabilities
 ↓
Plan
 ↓
Streaming event processing
 ↓
GC metrics
Allocation metrics
CPU metrics
ThreadPool metrics
 ↓
Trace findings
 ↓
Trace report
```

---

# 130. Example: Dump + Trace

```text
app.dmp ─────────────┐
                     ↓
                Session
                     ↑
trace.nettrace ─────┘
                     ↓
          Capability discovery
                     ↓
              Analysis DAG
               /          \
              ↓            ↓
        Dump analysis   Trace analysis
              \            /
               ↓          ↓
                 Correlation
                     ↓
                  Findings
                     ↓
             Combined report
```

---

# 131. Example: Multi-Dump Trend

```text
Dump A → Session A ─┐
Dump B → Session B ─┼─► Trend Engine
Dump C → Session C ─┘
                       ↓
                  Normalization
                       ↓
                  Growth metrics
                       ↓
                Recurring findings
                       ↓
                  Trend findings
```

---

# 132. Example: Full Investigation

```text
Session 1
 ├── Dump
 └── Trace

Session 2
 ├── Dump
 └── Trace

Session 3
 └── Dump

       ↓

Per-session analysis
       ↓
Cross-source correlation
       ↓
Cross-session comparison
       ↓
Trend analysis
       ↓
Investigation findings
       ↓
Investigation report
```

---

# 133. Migration Strategy From Current DumpDetective

A ground-up architecture does not mean throwing away all useful algorithms.

The existing project contains valuable domain knowledge:

- heap indexing;
- disk-backed indexes;
- retention analysis;
- analyzer logic;
- ClrMD experience;
- validation corpus;
- performance measurements.

The migration should preserve the **algorithms and evidence knowledge**, not necessarily the existing architecture.

---

# 134. Migration Phase 0 — Freeze the Domain

Before coding the new engine, define:

```text
Source
Session
Investigation
Evidence
Metric
Finding
AnalysisModule
AnalysisPlan
```

Do not migrate analyzers yet.

The objective is to establish the new vocabulary.

---

# 135. Migration Phase 1 — Build Core Contracts

Implement:

```text
Core
 ├── identity
 ├── source metadata
 ├── sessions
 ├── investigations
 ├── evidence references
 ├── metrics
 ├── findings
 └── analysis requests
```

No ClrMD references.

No TraceEvent references.

---

# 136. Migration Phase 2 — Build Dump Adapter

Wrap the existing ClrMD infrastructure behind the new source boundary.

Reuse proven:

- heap enumeration;
- object reading;
- indexing;
- graph access.

Do not rewrite algorithms merely for stylistic purity.

---

# 137. Migration Phase 3 — Rebuild Shared Evidence

Start with:

```text
Heap metrics
Type metrics
Runtime metadata
Thread evidence
```

Validate against existing DumpDetective outputs.

---

# 138. Migration Phase 4 — Migrate Existing Analyzers

For each analyzer, ask:

1. What evidence does it actually consume?
2. What evidence does it produce?
3. Is it source-specific?
4. Is it derived evidence?
5. Is it a finding generator?
6. Does another analyzer already produce the same prerequisite evidence?
7. Can its scan be shared?
8. What is its memory complexity?
9. What are its false positives?
10. What is its confidence model?

This will likely expose opportunities to merge several current analyzers.

---

# 139. Migration Phase 5 — Add Trace Engine

Only after the domain/evidence model is stable.

Build:

```text
TraceSource
Trace capabilities
Streaming event providers
Aggregation primitives
Trace metrics
```

Then implement trace analyzers.

---

# 140. Migration Phase 6 — Correlation

Start with high-confidence correlations:

```text
Dump heap size
+
Trace allocation rate
+
GC behavior
```

Avoid jumping immediately to sophisticated causal inference.

---

# 141. Migration Phase 7 — Trends

Add:

```text
metric normalization
session comparison
finding signatures
growth detection
recurrence
```

Then build investigation reports.

---

# 142. Migration Phase 8 — Advanced Algorithms

Once the foundation is stable:

```text
Dominator tree
advanced retention
survival analysis
allocation/retention correlation
graph clustering
```

These become modules rather than architecture-defining features.

---

# 143. What Should NOT Be Reused Blindly

Do not carry forward:

- analyzer interfaces merely because they already exist;
- report-oriented result types;
- source-specific types in domain models;
- giant shared caches;
- assumptions that one dump equals one investigation;
- analyzer-specific scanning duplicated across modules;
- memory-heavy intermediate structures;
- implicit global state.

Reuse knowledge and proven implementation techniques, not accidental architecture.

---

# 144. Recommended Initial Project Structure

Start relatively small:

```text
DumpDetective.Core
DumpDetective.Analysis
DumpDetective.Dumps
DumpDetective.Traces
DumpDetective.Storage
DumpDetective.Reporting
DumpDetective.Cli
DumpDetective.Tests
```

Within these, use namespaces/modules rather than immediately splitting into dozens of assemblies.

Split projects only when:

- dependency direction demands it;
- independent deployment/versioning matters;
- compile-time boundaries become valuable.

---

# 145. Suggested Internal Structure

```text
Core
 ├── Analysis
 ├── Evidence
 ├── Findings
 ├── Identity
 ├── Metrics
 ├── Sessions
 ├── Sources
 └── Time

Analysis
 ├── Planning
 ├── Scheduling
 ├── Modules
 ├── Correlation
 └── Trends

Dumps
 ├── ClrMD
 ├── Heap
 ├── Graph
 ├── Indexing
 └── Runtime

Traces
 ├── Reader
 ├── Events
 ├── Timeline
 └── Aggregation

Storage
 ├── Memory
 ├── Disk
 ├── Mmap
 └── Temp

Reporting
 ├── Model
 ├── Sections
 └── Renderers
```

---

# 146. Example Module Descriptor

Conceptually:

```text
Module:
    RetentionAnalysis

Requires:
    ObjectGraph
    RuntimeRoots

Produces:
    RetentionEvidence

Cost:
    High

Memory:
    Spillable

Passes:
    1–3 depending on implementation

Supports:
    Dump

Dependencies:
    HeapEnumeration
    ObjectIndex
```

Another:

```text
Module:
    AllocationPressureAnalysis

Requires:
    AllocationEvents

Produces:
    AllocationMetrics
    AllocationEvidence

Cost:
    Medium

Memory:
    Bounded

Supports:
    Trace
```

And:

```text
Module:
    MemoryLeakCorrelation

Requires:
    RetentionEvidence
    AllocationEvidence
    HeapGrowthMetrics

Produces:
    MemoryLeakFinding

Cost:
    Low/Medium after prerequisites

Supports:
    Combined
```

---

# 147. Example Analysis DAG

```text
                    Heap Enumeration
                           │
                    ┌──────┴──────┐
                    ↓             ↓
                Type Stats    Object Index
                                  │
                           Reference Graph
                                  │
                           Dominator Tree
                                  │
                           Retention Evidence
                                  │
                                  ├──────────────┐
                                  │              │
                                  │              ↓
                                  │       Retention Finding
                                  │
Trace Events
    │
    ↓
Allocation Aggregation
    │
    ↓
Allocation Evidence
    │
    └──────────────┐
                   ↓
          Memory Correlation
                   │
                   ↓
        Leak / Pressure Finding
```

---

# 148. Why This Architecture Is Better for Large Dumps

The most important benefit is not “supports traces.”

It is that analysis becomes **evidence-reusable**.

Instead of:

```text
Analyzer 1 scans heap
Analyzer 2 scans heap
Analyzer 3 scans heap
Analyzer 4 scans heap
```

you get:

```text
Source
 ↓
shared evidence
 ↓
many consumers
```

This reduces redundant expensive source work.

At the same time, the architecture does not force all evidence into RAM.

---

# 149. Why This Architecture Is Better for Traces

Trace processing naturally becomes:

```text
stream
 ↓
aggregate
 ↓
compact evidence
```

rather than:

```text
read entire trace
 ↓
build giant object model
```

That makes long-running production traces viable.

---

# 150. Why This Architecture Is Better for Combined Analysis

Combined analysis becomes a separate concern:

```text
Dump analysis
+
Trace analysis
↓
Correlation
```

rather than modifying every dump analyzer to understand traces.

That keeps the system composable.

---

# 151. Why This Architecture Is Better for Trends

Trend analysis consumes:

```text
Session outputs
```

rather than raw files.

Therefore:

```text
Dump trend
Trace trend
Combined trend
```

can all use the same machinery.

---

# 152. Biggest Risks

The redesign itself has significant risks.

## Risk 1: Over-abstraction

Creating interfaces for everything can make the code harder to understand.

**Mitigation:** abstract capabilities and meaningful boundaries only.

## Risk 2: Universal evidence model

Forcing dump and trace data into one shape can destroy useful semantics.

**Mitigation:** common concepts + source-specific evidence.

## Risk 3: Too much persistence

Persisting every derived object creates huge storage requirements.

**Mitigation:** persist only reusable expensive artifacts.

## Risk 4: Over-parallelization

Parallel modules can multiply memory usage.

**Mitigation:** resource-aware scheduler.

## Risk 5: Premature plugin architecture

Dynamic plugins introduce unnecessary complexity.

**Mitigation:** compile-time modules initially.

## Risk 6: Analysis DAG complexity

A sophisticated scheduler can become its own project.

**Mitigation:** begin with a simple deterministic DAG executor.

## Risk 7: Correlation overconfidence

Combining two datasets can make weak evidence look authoritative.

**Mitigation:** explicit confidence and evidence provenance.

## Risk 8: Trend false positives

Different workloads can create apparent growth.

**Mitigation:** comparability scoring and workload-aware normalization.

---

# 153. The Most Important Trade-Off

The central trade-off is:

```text
Abstraction
vs
Performance
```

The architecture should not create abstractions in the hottest loops.

For example, avoid:

```text
85 million objects
 ×
multiple interface calls
 ×
virtual allocations
```

if a specialized streaming implementation can avoid them.

Use abstractions at **component boundaries**.

Use efficient concrete implementations inside hot loops.

---

# 154. Hot Path Rule

A good rule:

```text
Cold path:
rich abstractions are fine.

Hot path:
compact structs, spans, arrays, streaming, specialized storage.
```

Examples of cold path:

```text
finding construction
report planning
analysis planning
configuration
```

Examples of hot path:

```text
heap enumeration
reference enumeration
trace event processing
index writes
graph traversal
aggregation
```

---

# 155. Another Important Trade-Off: Reuse vs Rescanning

There is no universal answer.

Option A:

```text
rescan source
```

Costs:

```text
IO
CPU
```

Option B:

```text
persist evidence
```

Costs:

```text
disk
write time
storage
complexity
```

For a huge dump, persisting a compact reusable index may be worthwhile.

For a small dump, rescanning may be faster and simpler.

The planner should eventually choose.

---

# 156. Recommended Strategy Selection

Start with simple thresholds.

Example:

```text
Small source:
    in-memory / rescan

Medium:
    hybrid

Large:
    disk-backed reusable artifacts
```

Do not hardcode these thresholds permanently.

Benchmark them.

---

# 157. Architectural Boundary Summary

The cleanest boundaries are:

```text
Input
 ↓
Source
 ↓
Capability
 ↓
Evidence
 ↓
Analysis
 ↓
Finding
 ↓
Report
```

With cross-cutting concepts:

```text
Identity
Time
Provenance
Resource limits
```

And two aggregation dimensions:

```text
Within session
Across sessions
```

---

# 158. Final Recommended Architecture

```text
                                      ┌──────────────────────┐
                                      │ CLI / API / GUI      │
                                      └──────────┬───────────┘
                                                 │
                                      ┌──────────▼───────────┐
                                      │   Analysis Request   │
                                      └──────────┬───────────┘
                                                 │
                                      ┌──────────▼───────────┐
                                      │ Investigation Model  │
                                      │ Sessions / Identity  │
                                      │ Time / Sources       │
                                      └──────────┬───────────┘
                                                 │
                                      ┌──────────▼───────────┐
                                      │ Capability Discovery │
                                      └──────────┬───────────┘
                                                 │
                                      ┌──────────▼───────────┐
                                      │   Analysis Planner   │
                                      │ DAG / Cost / Memory  │
                                      └──────────┬───────────┘
                                                 │
                         ┌───────────────────────┼───────────────────────┐
                         │                       │                       │
                ┌────────▼────────┐    ┌────────▼────────┐      Future
                │   Dump Engine   │    │  Trace Engine   │      Sources
                │     ClrMD       │    │ ETL/nettrace    │
                └────────┬────────┘    └────────┬────────┘
                         │                       │
                         └───────────┬───────────┘
                                     │
                          ┌──────────▼──────────┐
                          │   Evidence Layer    │
                          │                     │
                          │ Raw                 │
                          │ Derived             │
                          │ Metrics             │
                          │ Provenance          │
                          └──────────┬──────────┘
                                     │
                ┌────────────────────┼────────────────────┐
                │                    │                    │
       ┌────────▼────────┐  ┌────────▼────────┐  ┌───────▼────────┐
       │ Source Analysis │  │ Correlation     │  │ Trend Analysis │
       │ Modules         │  │ Modules         │  │ Modules        │
       └────────┬────────┘  └────────┬────────┘  └───────┬────────┘
                │                    │                    │
                └────────────────────┼────────────────────┘
                                     │
                          ┌──────────▼──────────┐
                          │   Findings Model    │
                          │ Evidence            │
                          │ Confidence          │
                          │ Impact              │
                          │ Explanation         │
                          │ Remediation         │
                          └──────────┬──────────┘
                                     │
                          ┌──────────▼──────────┐
                          │    Report Model      │
                          └──────────┬──────────┘
                                     │
                 ┌───────────────────┼───────────────────┐
                 │                   │                   │
          ┌──────▼──────┐    ┌──────▼──────┐    ┌──────▼────────┐
          │   Dump      │    │   Trace     │    │ Combined /    │
          │   Report    │    │   Report    │    │ Trend / Full  │
          └─────────────┘    └─────────────┘    └───────────────┘
```

---

# 159. Final Recommendation

The preferred architecture is **not**:

```text
DumpDetective
 ├── DumpAnalyzer
 └── TraceAnalyzer
```

and not:

```text
Everything → GenericEvidence
```

It should be:

```text
                Investigation
                      │
                   Sessions
                      │
                   Sources
                      │
                Capabilities
                      │
                Evidence DAG
                      │
             ┌────────┴────────┐
             │                 │
          Metrics          Evidence
             │                 │
             └────────┬────────┘
                      │
                 Findings
                      │
                  Reports
```

The source types remain specialized.

The analysis model remains unified.

The evidence model is partially shared but not artificially universal.

The expensive operations remain streaming and spillable.

The planner controls dependencies and resources.

The findings layer becomes the universal diagnostic output.

The reporting layer becomes a pure consumer.

And the investigation/session model makes dump+trace correlation and longitudinal analysis natural rather than bolted on.

---

# 160. Architecture Decision Record

## Decision

Build DumpDetective 2.0 around a unified **Investigation → Session → Source → Evidence → Analysis → Finding → Report** architecture.

## Reasons

1. Supports dumps and traces without source-specific coupling.
2. Makes combined analysis a first-class operation.
3. Makes trend analysis a natural extension.
4. Allows evidence reuse between analyzers.
5. Supports streaming and disk-backed processing.
6. Preserves ClrMD and trace-reader isolation.
7. Enables dependency-aware analysis planning.
8. Provides strong provenance and explainability.
9. Allows partial/failed analyses without invalidating the investigation.
10. Creates a stable foundation for future evidence sources.

## Rejected Alternatives

### Dump-centric extension

Rejected because combined and trend analysis become increasingly special-cased.

### Universal event model

Rejected because dumps and traces have fundamentally different semantics.

### Giant generic evidence object model

Rejected because it encourages excessive materialization and weak typing.

### Analyzer-per-file-type architecture

Rejected because cross-source analysis becomes coupled to source classes.

### Immediate distributed processing

Rejected because the primary problem is local memory/CPU/disk efficiency, not distributed execution.

### Dynamic plugin system from day one

Rejected because it introduces complexity before there is a demonstrated external plugin requirement.

---

# 161. The One-Sentence Mental Model

If the entire architecture needs to fit in one sentence:

> **DumpDetective is a resource-aware evidence processing engine that turns heterogeneous runtime observations across time into explainable, confidence-rated diagnostic findings.**

That should be the architectural north star.
