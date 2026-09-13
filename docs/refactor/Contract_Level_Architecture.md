# DumpDetective 2 — Contract-Level Architecture
## Deep Design of the Core Interfaces, Data Contracts, Planning Model, and Execution Engine

**Status:** Architecture proposal  
**Target:** .NET 10  
**Primary runtime:** CLRMD 4.x for managed dump analysis  
**Trace stack:** ETL / .NETTrace / EventPipe-compatible providers through an isolated trace adapter  
**Purpose:** Define the concrete contracts beneath the unified DumpDetective 2 architecture.

---

# 1. Executive Summary

DumpDetective 2 should not be modeled as:

```text
DumpAnalyzer
TraceAnalyzer
ReportGenerator
```

That split looks modular, but it creates the wrong abstraction boundary. Dumps and traces are fundamentally different observations of a running system:

- a dump is predominantly a **state snapshot**
- a trace is predominantly a **time-ordered stream of observations**
- multiple dumps are a **sequence of snapshots**
- multiple traces are a **temporal observation window**
- dumps + traces together form a **correlated investigation**

The contract architecture therefore needs a stable semantic layer above the source-specific mechanics.

The recommended dependency direction is:

```text
                 ┌───────────────────────────┐
                 │       Presentation         │
                 │ reports / CLI / JSON / UI │
                 └─────────────┬─────────────┘
                               │
                 ┌─────────────▼─────────────┐
                 │       Findings Model       │
                 │ evidence / confidence /   │
                 │ impact / explanations      │
                 └─────────────┬─────────────┘
                               │
                 ┌─────────────▼─────────────┐
                 │     Analysis Contracts     │
                 │ modules / plans / DAG /   │
                 │ capabilities / metrics     │
                 └─────────────┬─────────────┘
                               │
          ┌────────────────────┴────────────────────┐
          │                                         │
┌─────────▼─────────┐                     ┌─────────▼─────────┐
│  Semantic Model   │                     │ Evidence Storage  │
│ objects / stacks  │                     │ memory / disk     │
│ events / threads  │                     │ indexes / blobs   │
└─────────┬─────────┘                     └─────────┬─────────┘
          │                                         │
┌─────────▼─────────────────────────────────────────▼─────────┐
│                     Source Adapters                          │
│             DumpAdapter       TraceAdapter                   │
│                 CLRMD             TraceEvent                  │
└─────────┬─────────────────────────────────────────┬─────────┘
          │                                         │
      dump files                               ETL / nettrace
```

The critical design rule is:

> **Source adapters expose capabilities and semantic observations; analyzers consume contracts, not CLRMD or TraceEvent objects.**

However, the semantic layer must **not** become an enormous universal abstraction. A managed object address in a dump and an allocation event in a trace are related concepts, but they are not the same evidence.

The architecture should therefore use:

1. **narrow semantic contracts**
2. **capability-specific interfaces**
3. **immutable result contracts**
4. **lazy/streaming data access**
5. **explicit evidence provenance**
6. **planner-generated analysis DAGs**
7. **resource-aware execution**
8. **source-specific escape hatches only at adapter boundaries**

---

# 2. Design Goals

## 2.1 Primary goals

The contract system must support:

### Single dump

```text
dump
  ↓
dump-capable analyses
  ↓
findings
  ↓
report
```

### Single trace

```text
trace
  ↓
trace-capable analyses
  ↓
findings
  ↓
report
```

### Multiple dumps

```text
dump A ─┐
dump B ─┼─→ temporal/trend analysis → findings → report
dump C ─┘
```

### Multiple traces

```text
trace A ─┐
trace B ─┼─→ trace trend analysis → findings
trace C ─┘
```

### Dump + trace

```text
dump ─────────────┐
                  ├─→ correlation → finding
trace ────────────┘
```

### Mixed investigation

```text
        ┌─ dump 1
        ├─ trace 1
        ├─ dump 2
        ├─ trace 2
        └─ dump 3
              │
              ▼
       unified investigation
              │
      ┌───────┴────────┐
      ▼                ▼
single-source       cross-source
analysis            correlation
      │                │
      └───────┬────────┘
              ▼
          findings
              ▼
            report
```

---

# 3. Non-Goals

The contracts should **not** attempt to:

- make every source look identical
- expose every CLRMD API through a generic wrapper
- expose every TraceEvent API through a generic wrapper
- serialize arbitrary runtime objects
- make every analyzer capable of every source
- maintain a giant in-memory object graph
- turn the analysis engine into a general workflow framework
- provide arbitrary distributed execution
- make reports responsible for interpreting raw evidence
- hide every performance characteristic behind interfaces

These would make the architecture look elegant while damaging scalability and correctness.

---

# 4. Core Architectural Vocabulary

The following terms must have precise meanings.

## 4.1 Investigation

The complete user-requested analysis operation.

An investigation can contain:

- one source
- multiple sources
- dumps
- traces
- source groups
- temporal relationships
- analysis configuration
- execution configuration

```text
Investigation
 ├── Sources
 ├── Relationships
 ├── AnalysisConfiguration
 ├── ExecutionPolicy
 └── ReportConfiguration
```

---

## 4.2 Source

A concrete diagnostic artifact.

Examples:

- `foo.dmp`
- `trace.nettrace`
- `trace.etl`

A source has:

- identity
- kind
- metadata
- time information if available
- capabilities
- adapter

A source is **not** itself a semantic dataset.

---

## 4.3 Source Session

The runtime-backed access context for one source.

For a dump:

```text
DumpSourceSession
 └── ClrMD DataTarget / Runtime / Heap
```

For a trace:

```text
TraceSourceSession
 └── TraceEvent processing session
```

The session owns resources and lifecycle.

It must not leak source-specific runtime objects into the rest of the system.

---

## 4.4 Capability

A declaration that a source can support a particular operation.

Examples:

```text
ManagedHeap
ObjectEnumeration
ObjectReferences
TypeInformation
ThreadStacks
GCEvents
AllocationEvents
CPUStacks
ThreadScheduling
Exceptions
Locks
```

Capabilities are more important than source kind.

A trace might provide GC events but no object graph.

A dump provides object graph information but no historical allocation event stream.

---

## 4.5 Analysis Module

A self-contained analytical unit.

Examples:

```text
LargeObjectAnalyzer
StringRetentionAnalyzer
GCPressureAnalyzer
AllocationHotspotAnalyzer
ThreadPoolAnalyzer
ExceptionRateAnalyzer
HeapTrendAnalyzer
DumpTraceMemoryCorrelationAnalyzer
```

A module declares:

- inputs
- required capabilities
- produced artifacts
- resource characteristics
- execution constraints
- semantic scope

---

## 4.6 Artifact

A reusable intermediate result.

Examples:

```text
HeapSummary
ObjectTypeHistogram
ObjectIndex
ReferenceGraph
DominatorTree
GCStats
AllocationSummary
CpuHotspots
ThreadTimeline
ExceptionSummary
```

Artifacts are not necessarily persisted.

They can be:

- streamed
- memory-backed
- disk-backed
- cached
- regenerated

---

## 4.7 Evidence

A piece of diagnostic support for a finding.

Evidence should answer:

> What observation supports this conclusion?

Examples:

```text
Type X retains 4.7 GB through static field Y.
```

or:

```text
Allocation rate increased from 1.2 GB/s to 3.9 GB/s during the incident window.
```

Evidence has provenance.

---

## 4.8 Finding

An interpretation backed by evidence.

A finding is not merely a metric.

Example:

```text
Finding:
    A cache is retaining objects beyond expected lifecycle.

Impact:
    4.7 GB managed heap retention.

Confidence:
    High.

Evidence:
    Dominator path
    Type counts
    Snapshot trend
```

---

# 5. Contract Design Principles

## 5.1 Interfaces should represent capabilities, not implementation classes

Bad:

```csharp
public interface IClrMdAnalyzer
```

Better:

```csharp
public interface IObjectGraphProvider
```

Best when appropriate:

```csharp
public interface IManagedHeapSnapshot
```

The latter describes a semantic contract rather than a library.

---

## 5.2 Avoid "god interfaces"

Do not create:

```csharp
public interface IAnalysisContext
{
    IRuntime Runtime { get; }
    IHeap Heap { get; }
    IEnumerable<Event> Events { get; }
    ...
}
```

This causes:

- unnecessary coupling
- difficult testing
- accidental source assumptions
- unclear dependencies
- impossible capability enforcement

Instead:

```csharp
IAnalysisContext
 ├── Metadata
 ├── ArtifactStore
 ├── EvidenceSink
 └── capability-specific services
```

---

# 6. Investigation Contract

Recommended shape:

```csharp
public sealed record InvestigationDefinition(
    InvestigationId Id,
    ImmutableArray<SourceDefinition> Sources,
    ImmutableArray<SourceRelationship> Relationships,
    AnalysisSelection Analysis,
    ExecutionPolicy Execution,
    ReportConfiguration Reports);
```

This is configuration, not runtime state.

It should be immutable.

---

# 7. SourceDefinition

```csharp
public sealed record SourceDefinition(
    SourceId Id,
    SourceKind Kind,
    string Path,
    SourceMetadata Metadata);
```

Potential `SourceKind`:

```csharp
public enum SourceKind
{
    Dump,
    Trace
}
```

Do not encode capabilities in this enum.

A source's actual capabilities come from its adapter.

---

# 8. SourceMetadata

```csharp
public sealed record SourceMetadata(
    SourceId SourceId,
    DateTimeOffset? StartTime,
    DateTimeOffset? EndTime,
    string? ProcessName,
    int? ProcessId,
    string? MachineName,
    string? RuntimeVersion,
    ImmutableDictionary<string, string> Properties);
```

Important:

`SourceMetadata` is observational metadata.

It must distinguish:

```text
Known
Unknown
Inferred
```

Do not convert missing metadata into guessed values.

---

# 9. Source Relationships

Cross-source analysis needs explicit relationships.

```csharp
public abstract record SourceRelationship;

public sealed record TemporalRelation(
    SourceId Earlier,
    SourceId Later,
    TimeSpan? ExpectedGap) : SourceRelationship;

public sealed record SameProcessRelation(
    SourceId Left,
    SourceId Right) : SourceRelationship;

public sealed record SameIncidentRelation(
    ImmutableArray<SourceId> Sources,
    string Reason) : SourceRelationship;
```

However, most relationships should be **inferred**, not manually required.

The planner may infer:

```text
same PID
+
same machine
+
overlapping/nearby timestamps
+
compatible runtime
```

and produce:

```text
InferredRelationship
Confidence = 0.94
```

The inference itself should become evidence if used for a conclusion.

---

# 10. Source Adapter Contract

Recommended top-level contract:

```csharp
public interface IAnalysisSource : IAsyncDisposable
{
    SourceDescriptor Descriptor { get; }

    IReadOnlySet<AnalysisCapability> Capabilities { get; }

    ValueTask<ISourceSession> OpenAsync(
        SourceOpenOptions options,
        CancellationToken cancellationToken);
}
```

This interface deliberately does **not** expose:

```csharp
ClrMD
TraceEvent
```

---

# 11. Source Session

```csharp
public interface ISourceSession : IAsyncDisposable
{
    SourceDescriptor Descriptor { get; }

    IReadOnlySet<AnalysisCapability> Capabilities { get; }

    ValueTask<T> GetAsync<T>(
        CancellationToken cancellationToken = default)
        where T : class;
}
```

This generic service locator deserves scrutiny.

It is convenient but dangerous.

### Risk

```csharp
session.GetAsync<IManagedHeapSnapshot>()
```

is effectively a hidden dependency system.

Therefore, the preferred design is:

```csharp
public interface IManagedHeapSource
{
    ValueTask<IManagedHeapSnapshot> GetHeapAsync(...);
}
```

and capability interfaces are exposed through a strongly typed session aggregate.

For example:

```csharp
public interface IManagedDumpSession : ISourceSession
{
    IManagedHeapSource Heap { get; }
    IThreadSource Threads { get; }
    IModuleSource Modules { get; }
}
```

Trace:

```csharp
public interface ITraceSession : ISourceSession
{
    IEventSource Events { get; }
    IStackSource Stacks { get; }
    IGcEventSource Gc { get; }
}
```

This is preferable to a generic service locator.

---

# 12. Capability Model

Recommended:

```csharp
public enum AnalysisCapability
{
    ManagedHeap,
    ObjectEnumeration,
    ObjectReferences,
    TypeMetadata,
    ManagedThreads,
    StackWalk,
    Modules,

    GcEvents,
    AllocationEvents,
    CpuSamples,
    ThreadScheduling,
    ExceptionEvents,
    ContentionEvents,
    LockEvents,

    RuntimeCounters,
    ProcessCounters
}
```

Do not create hundreds of tiny capabilities.

Capabilities should correspond to meaningful analysis dependencies.

---

# 13. Why Capability Checks Matter

Suppose:

```csharp
LargeObjectAnalyzer
```

requires:

```text
ManagedHeap
ObjectEnumeration
TypeMetadata
```

The planner can reject a trace before execution.

Similarly:

```csharp
AllocationHotspotAnalyzer
```

requires:

```text
AllocationEvents
```

A dump cannot satisfy that requirement.

This gives deterministic planning:

```text
source capabilities
        ↓
analysis requirements
        ↓
eligible modules
```

---

# 14. Semantic Dump Contracts

The dump adapter should expose a semantic model.

## 14.1 Heap

```csharp
public interface IManagedHeapSource
{
    ValueTask<HeapDescriptor> GetDescriptorAsync(
        CancellationToken cancellationToken);

    IAsyncEnumerable<HeapObject> EnumerateObjectsAsync(
        HeapEnumerationOptions options,
        CancellationToken cancellationToken);
}
```

Important:

`IAsyncEnumerable<HeapObject>` must not imply that all objects are materialized.

---

# 15. HeapObject

```csharp
public readonly record struct HeapObject(
    ulong Address,
    ulong MethodTable,
    uint Size,
    ObjectKind Kind);
```

Keep this tiny.

Do not embed:

```csharp
Type
List<Reference>
string Name
StackTrace
```

Those cause memory explosions.

---

# 16. Object Identity

A dump object address is only locally meaningful.

Therefore:

```csharp
public readonly record struct SnapshotObjectId(
    SourceId Source,
    ulong Address);
```

Never use:

```csharp
ulong Address
```

as a globally unique identifier.

For trend analysis, address identity is insufficient because GC relocation changes addresses.

Cross-snapshot identity should be represented separately:

```csharp
public readonly record struct LogicalObjectId(
    ObjectIdentityKind Kind,
    ulong Value);
```

But the system should not pretend that arbitrary objects can always be matched across dumps.

---

# 17. Cross-Snapshot Object Identity

There are several levels.

### Level 0 — no identity

Compare aggregates:

```text
Type X:
dump A = 2.1 GB
dump B = 3.8 GB
```

This is robust.

### Level 1 — type/shape identity

Compare:

```text
type
size
generation
retention pattern
```

### Level 2 — stable application identity

If an object exposes a stable ID:

```text
cache entry key
request ID
entity ID
```

it may be correlated.

### Level 3 — address identity

Generally weak across snapshots.

The contract must therefore avoid:

```csharp
ObjectId = Address
```

for trend analysis.

---

# 18. Object References

```csharp
public interface IObjectReferenceSource
{
    IAsyncEnumerable<ObjectReference> EnumerateReferencesAsync(
        SnapshotObjectId source,
        ReferenceEnumerationOptions options,
        CancellationToken cancellationToken);
}
```

```csharp
public readonly record struct ObjectReference(
    SnapshotObjectId Source,
    SnapshotObjectId Target,
    ReferenceKind Kind);
```

Again, no object graph materialization.

---

# 19. Reference Kind

```csharp
public enum ReferenceKind
{
    ObjectField,
    ArrayElement,
    StaticField,
    Handle,
    Finalizer,
    Unknown
}
```

The adapter should preserve uncertainty.

If CLRMD cannot confidently classify something:

```text
Unknown
```

is preferable to guessing.

---

# 20. Type Metadata

```csharp
public interface ITypeMetadataSource
{
    ValueTask<TypeDescriptor?> GetTypeAsync(
        ulong methodTable,
        CancellationToken cancellationToken);
}
```

```csharp
public sealed record TypeDescriptor(
    ulong MethodTable,
    string Name,
    string? Namespace,
    uint? BaseSize,
    TypeKind Kind);
```

Caching strategy belongs outside this contract.

---

# 21. Trace Contracts

Trace data is fundamentally different.

The primary primitive should be a streaming event.

```csharp
public interface ITraceEventSource
{
    IAsyncEnumerable<TraceEventRecord> ReadEventsAsync(
        TraceReadOptions options,
        CancellationToken cancellationToken);
}
```

---

# 22. TraceEventRecord

Do not expose `TraceEvent` directly.

```csharp
public readonly record struct TraceEventRecord(
    long Sequence,
    DateTimeOffset Timestamp,
    TraceProviderId Provider,
    TraceEventKind Kind,
    int? ProcessId,
    int? ThreadId,
    ReadOnlyMemory<byte> Payload);
```

However, arbitrary payload bytes create usability problems.

The preferred design is:

```csharp
TraceEventRecord
 ├── identity
 ├── timestamp
 ├── process/thread
 ├── kind
 └── typed semantic payload
```

But typed payload should be represented through specialized event interfaces where analysis needs it.

---

# 23. Specialized Trace Sources

Example:

```csharp
public interface IAllocationEventSource
{
    IAsyncEnumerable<AllocationEvent> ReadAllocationsAsync(...);
}

public interface IGcEventSource
{
    IAsyncEnumerable<GcEvent> ReadGcEventsAsync(...);
}

public interface ICpuSampleSource
{
    IAsyncEnumerable<CpuSample> ReadSamplesAsync(...);
}
```

This is better than:

```csharp
IEnumerable<TraceEvent>
```

for every analyzer.

---

# 24. AllocationEvent

```csharp
public readonly record struct AllocationEvent(
    DateTimeOffset Timestamp,
    int ProcessId,
    int ThreadId,
    ulong? TypeId,
    string? TypeName,
    long Size,
    AllocationKind Kind);
```

The exact fields should reflect what the trace can actually provide.

Optional fields must remain optional.

---

# 25. GC Event

```csharp
public readonly record struct GcEvent(
    DateTimeOffset Timestamp,
    int ProcessId,
    GcEventKind Kind,
    int Generation,
    TimeSpan Duration,
    long? HeapSizeBefore,
    long? HeapSizeAfter);
```

Again:

```text
unknown != zero
```

Do not encode unavailable values as zero.

---

# 26. Stack Contracts

CPU and allocation analyses frequently require stack information.

```csharp
public interface IStackSource
{
    ValueTask<StackTraceView?> GetStackAsync(
        StackId id,
        CancellationToken cancellationToken);
}
```

For high-volume samples, avoid duplicating complete stack arrays.

Use interned stack IDs where possible:

```text
sample → StackId → stack frames
```

---

# 27. Thread Contracts

```csharp
public interface IThreadSource
{
    IAsyncEnumerable<ThreadDescriptor> EnumerateThreadsAsync(...);
}
```

```csharp
public sealed record ThreadDescriptor(
    int ThreadId,
    string? Name,
    ThreadKind Kind,
    ThreadState State);
```

Trace thread lifetimes may be richer:

```csharp
ThreadLifetime
 ├── Start
 ├── End
 ├── CPU time
 ├── waits
 └── scheduling
```

Do not force dump threads and trace thread lifetimes into the same object.

Use semantic concepts where genuinely common.

---

# 28. Analysis Module Contract

This is the most important contract.

Recommended:

```csharp
public interface IAnalysisModule
{
    AnalysisDescriptor Descriptor { get; }

    ValueTask<AnalysisResult> AnalyzeAsync(
        AnalysisContext context,
        CancellationToken cancellationToken);
}
```

But this alone is insufficient.

The module must declare its requirements.

---

# 29. AnalysisDescriptor

```csharp
public sealed record AnalysisDescriptor(
    AnalysisId Id,
    string Name,
    Version Version,
    AnalysisScope Scope,
    ImmutableArray<AnalysisRequirement> Requirements,
    ImmutableArray<ArtifactTypeId> Produces,
    ResourceProfile Resources);
```

---

# 30. AnalysisScope

```csharp
public enum AnalysisScope
{
    Source,
    SourceGroup,
    Investigation
}
```

Meaning:

### Source

Works on one dump or one trace.

### SourceGroup

Works on multiple homogeneous sources.

### Investigation

Can consume heterogeneous sources.

This distinction prevents accidental complexity.

---

# 31. Requirements

A module may require:

```csharp
public abstract record AnalysisRequirement;

public sealed record CapabilityRequirement(
    AnalysisCapability Capability)
    : AnalysisRequirement;

public sealed record ArtifactRequirement(
    ArtifactTypeId Artifact)
    : AnalysisRequirement;

public sealed record SourceKindRequirement(
    SourceKind Kind)
    : AnalysisRequirement;
```

Example:

```text
HeapRetentionAnalyzer
 ├── ManagedHeap
 ├── ObjectReferences
 ├── TypeMetadata
 └── ReferenceGraph artifact
```

---

# 32. Why Requirements Must Be Declarative

Without declarative requirements:

```text
planner
  ↓
run analyzer
  ↓
analyzer discovers missing data
  ↓
fails halfway
```

With requirements:

```text
planner
  ↓
capability validation
  ↓
artifact planning
  ↓
resource estimation
  ↓
execution
```

This is especially important for 25 GB dumps.

We cannot afford expensive failed attempts.

---

# 33. ResourceProfile

Resource estimates should be explicit.

```csharp
public sealed record ResourceProfile(
    ResourceBehavior Memory,
    ResourceBehavior Disk,
    ResourceBehavior Cpu,
    ResourceBehavior Io);
```

Possible:

```csharp
public enum ResourceBehavior
{
    Low,
    Moderate,
    High,
    Extreme,
    Unknown
}
```

This is coarse metadata.

The executor can combine it with runtime telemetry.

---

# 34. More Useful Resource Metadata

For serious scaling, add:

```csharp
public sealed record ResourceEstimate(
    long? FixedMemoryBytes,
    long? PerMillionObjectsBytes,
    long? PerMillionEventsBytes,
    long? EstimatedDiskBytes,
    bool RequiresRandomAccess,
    bool RequiresGlobalState,
    bool Streamable);
```

But these are estimates, not guarantees.

Do not build scheduling around fake precision.

---

# 35. AnalysisContext

Avoid making it a giant service bag.

Recommended:

```csharp
public sealed class AnalysisContext
{
    public required AnalysisExecutionContext Execution { get; init; }
    public required IArtifactStore Artifacts { get; init; }
    public required IEvidenceSink Evidence { get; init; }
    public required IAnalysisLogger Logger { get; init; }
}
```

Source-specific semantic services should be injected according to the module's requirements.

---

# 36. Dependency Injection vs Context Lookup

Preferred:

```csharp
public interface IHeapRetentionAnalyzer
{
    ValueTask<AnalysisResult> AnalyzeAsync(
        IManagedHeapSource heap,
        IObjectReferenceSource references,
        AnalysisContext context,
        CancellationToken cancellationToken);
}
```

But this creates many module-specific interfaces.

Alternative:

```csharp
AnalysisContext
 └── Capabilities
      ├── Heap
      ├── References
      └── Types
```

Recommended compromise:

```csharp
public interface ICapabilityContext
{
    T Require<T>() where T : class;
}
```

This is acceptable **inside the execution layer**, because the planner has already validated capabilities.

The module's descriptor remains the authoritative dependency declaration.

---

# 37. AnalysisResult

Results should not directly be findings only.

```csharp
public sealed record AnalysisResult(
    AnalysisId Analysis,
    AnalysisStatus Status,
    ImmutableArray<ArtifactReference> Artifacts,
    ImmutableArray<Finding> Findings,
    AnalysisStatistics Statistics);
```

Why both artifacts and findings?

Because an analysis may produce useful intermediate results without concluding that there is a problem.

Example:

```text
GC analysis:
    GC statistics artifact
    no finding
```

---

# 38. AnalysisStatus

```csharp
public enum AnalysisStatus
{
    Completed,
    CompletedWithWarnings,
    Partial,
    Skipped,
    Failed
}
```

`Partial` is crucial.

Large diagnostic tools often encounter:

- corrupt pages
- missing trace providers
- unsupported runtime versions
- truncated traces
- inaccessible memory regions

A production analyzer must not equate:

```text
could not inspect everything
```

with:

```text
nothing found
```

---

# 39. Evidence Contract

Recommended:

```csharp
public sealed record Evidence(
    EvidenceId Id,
    EvidenceKind Kind,
    string Summary,
    EvidenceSource Source,
    EvidenceLocation Location,
    EvidenceStrength Strength,
    ImmutableDictionary<string, object?> Attributes);
```

But `object?` is dangerous for serialization and schema stability.

Prefer:

```csharp
ImmutableDictionary<string, EvidenceValue>
```

where:

```csharp
public readonly record struct EvidenceValue(
    EvidenceValueKind Kind,
    string? Text,
    long? Integer,
    double? Number,
    bool? Boolean);
```

Or use a typed evidence hierarchy.

For long-term schema stability, the hierarchy is preferable.

---

# 40. Evidence Provenance

Every evidence item should identify:

```text
source
analysis
artifact
location
derivation
```

Example:

```csharp
public sealed record EvidenceProvenance(
    SourceId Source,
    AnalysisId Analysis,
    ArtifactReference? Artifact,
    EvidenceLocation Location);
```

This allows a report to answer:

> Where did this claim come from?

---

# 41. Evidence Location

Dump:

```text
Object address
Method table
Type
Reference path
Segment
```

Trace:

```text
timestamp
event sequence
provider
thread
process
stack
```

Cross-source:

```text
source A location
source B location
correlation window
```

Use a discriminated union rather than one giant structure.

---

# 42. Finding Contract

Recommended:

```csharp
public sealed record Finding(
    FindingId Id,
    FindingCategory Category,
    FindingSeverity Severity,
    FindingTitle Title,
    FindingExplanation Explanation,
    ImpactAssessment Impact,
    ConfidenceAssessment Confidence,
    ImmutableArray<EvidenceReference> Evidence,
    ImmutableArray<Remediation> Remediations,
    ImmutableArray<FalsePositive> PossibleFalsePositives);
```

---

# 43. Finding vs Metric

This distinction is fundamental.

Metric:

```text
Heap = 8.4 GB
```

Finding:

```text
A cache-like object graph retains approximately 4.2 GB
and increased consistently across three snapshots.
```

Metric is observation.

Finding is interpretation.

Do not let metric APIs become finding APIs.

---

# 44. ImpactAssessment

```csharp
public sealed record ImpactAssessment(
    ImpactDimension Dimension,
    double? Value,
    string Unit,
    ImpactSeverity Severity);
```

Examples:

```text
Managed memory: 4.2 GB
CPU: 37%
Allocation rate: 2.8 GB/s
GC pause: 420 ms
Thread count: +1,240
```

---

# 45. Confidence

Avoid a naked:

```csharp
double Confidence
```

Instead:

```csharp
public sealed record ConfidenceAssessment(
    ConfidenceLevel Level,
    double? Score,
    ImmutableArray<ConfidenceFactor> Factors);
```

```csharp
public enum ConfidenceLevel
{
    VeryLow,
    Low,
    Medium,
    High,
    VeryHigh
}
```

Factors:

```text
+ repeated across snapshots
+ direct retention path
+ matching trace evidence
- incomplete heap
- missing provider
```

---

# 46. Confidence Must Not Pretend to Be Statistical

A score of:

```text
0.93
```

does not mean:

> 93% probability the finding is true.

Unless the system has a validated statistical model, it should not make that claim.

The numeric score should therefore be optional and secondary to explainable factors.

---

# 47. Remediation

```csharp
public sealed record Remediation(
    string Summary,
    RemediationPriority Priority,
    ImmutableArray<string> Rationale);
```

The analyzer should explain why the recommendation follows from the evidence.

---

# 48. False Positives

This deserves first-class representation.

Example:

```text
Possible false positive:
    This may be an intentionally unbounded cache if the process is
    expected to retain historical entries for the observed workload.
```

This is much better than:

```text
Confidence = 75%
```

without explanation.

---

# 49. Artifact Contract

Artifacts are the reusable backbone.

```csharp
public interface IArtifact
{
    ArtifactTypeId Type { get; }
    ArtifactMetadata Metadata { get; }
}
```

But artifacts can be huge.

Therefore the actual interface should expose access semantics.

```csharp
public interface IArtifactHandle
{
    ArtifactDescriptor Descriptor { get; }

    ValueTask<TArtifact> OpenAsync<TArtifact>(
        CancellationToken cancellationToken);
}
```

---

# 50. Artifact Categories

### Small artifacts

Examples:

```text
HeapSummary
GcSummary
TopTypes
```

Can be memory-resident.

### Medium artifacts

Examples:

```text
Type histogram
Thread summary
Allocation aggregation
```

Can often be memory-resident.

### Huge artifacts

Examples:

```text
Object index
Reference index
Dominator graph
Raw event index
```

Must support disk-backed storage.

---

# 51. Artifact Storage Abstraction

```csharp
public interface IArtifactStore
{
    ValueTask<ArtifactHandle<T>> CreateAsync<T>(
        ArtifactCreationOptions options,
        CancellationToken cancellationToken);

    ValueTask<ArtifactHandle<T>?> TryOpenAsync<T>(
        ArtifactReference reference,
        CancellationToken cancellationToken);
}
```

The store decides:

```text
RAM
disk
memory mapped
compressed
hybrid
```

based on artifact policy.

---

# 52. Important: Artifact != Cache

An artifact is a semantic result.

A cache is an implementation optimization.

Example:

```text
ObjectTypeHistogram
```

is an artifact.

```text
CLRMD method table lookup cache
```

is a cache.

Do not expose cache internals as analytical artifacts.

---

# 53. Artifact Lifetime

Every artifact should have a lifetime:

```csharp
public enum ArtifactLifetime
{
    ExecutionOnly,
    Investigation,
    Persisted
}
```

Default should be:

```text
ExecutionOnly
```

Persist only what has a demonstrated reuse value.

This prevents disk bloat.

---

# 54. Analysis Plan

The planner converts:

```text
InvestigationDefinition
+
available capabilities
+
selected analyses
```

into:

```text
AnalysisPlan
```

---

# 55. AnalysisPlan

```csharp
public sealed record AnalysisPlan(
    ImmutableArray<PlanNode> Nodes,
    ImmutableArray<PlanEdge> Edges,
    PlanDiagnostics Diagnostics);
```

Nodes:

```text
source open
artifact build
analysis
correlation
report
```

---

# 56. PlanNode

```csharp
public abstract record PlanNode(
    PlanNodeId Id,
    PlanNodeKind Kind);
```

Kinds:

```csharp
public enum PlanNodeKind
{
    SourceOpen,
    ArtifactBuild,
    Analysis,
    Correlation,
    Report
}
```

---

# 57. Example Plan

For a dump retention report:

```text
OpenDump
   │
   ├── HeapEnumeration
   │
   ├── TypeIndex
   │
   └── ReferenceGraph
           │
           ▼
     DominatorTree
           │
           ▼
  RetentionAnalyzer
           │
           ▼
       Findings
```

---

# 58. Shared Artifact Deduplication

Suppose ten analyzers require:

```text
TypeIndex
```

The planner must create:

```text
one TypeIndex node
```

not:

```text
ten TypeIndex nodes
```

This is one of the strongest reasons for an explicit DAG.

---

# 59. Analysis Node Contract

```csharp
public sealed record AnalysisPlanNode(
    PlanNodeId Id,
    AnalysisDescriptor Descriptor,
    SourceScope Scope,
    ImmutableArray<ArtifactReference> Inputs,
    ImmutableArray<ArtifactTypeId> Outputs);
```

The planner resolves all dependencies before execution.

---

# 60. DAG vs Pipeline

A pipeline:

```text
A → B → C
```

is insufficient.

Real diagnostic work looks like:

```text
              ┌→ TypeHistogram ───────┐
Heap ─────────┼→ ObjectIndex ─────────┼→ Retention
              ├→ SegmentStats         │
              └→ RootAnalysis ────────┘
```

Therefore the executor should be DAG-based.

But it should remain a **specialized analysis DAG**, not a general-purpose workflow engine.

---

# 61. Why Not a Generic Workflow Engine?

Generic workflow engines introduce:

- too many abstractions
- opaque scheduling
- difficult resource control
- serialization requirements
- lifecycle complexity

DumpDetective needs:

```text
resource-aware
streaming
local
diagnostic-specific
```

A custom DAG executor is preferable.

---

# 62. Execution Context

```csharp
public sealed record AnalysisExecutionContext(
    InvestigationId InvestigationId,
    PlanNodeId NodeId,
    ResourceBudget Budget,
    CancellationToken CancellationToken);
```

---

# 63. Resource Budget

```csharp
public sealed record ResourceBudget(
    long MemorySoftLimitBytes,
    long MemoryHardLimitBytes,
    long DiskLimitBytes,
    int MaxConcurrency,
    TimeSpan? TimeLimit);
```

Memory limits should be advisory plus enforceable where possible.

The runtime cannot always force third-party libraries to stay within a precise budget.

---

# 64. Memory Accounting

The executor should track:

```text
managed allocations
artifact allocations
memory-mapped regions
native allocations where measurable
working-set telemetry
```

But do not treat working set as exact allocation ownership.

Windows file mapping can make:

```text
working set ≠ committed bytes
```

and CLRMD may use native memory.

Use multiple metrics.

---

# 65. Scheduler

The scheduler should consider:

```text
DAG dependencies
memory cost
disk cost
I/O contention
source locks
CPU pressure
```

Not merely:

```csharp
Task.WhenAll(...)
```

---

# 66. Example Scheduling

Bad:

```text
Run all 36 analyzers concurrently.
```

Better:

```text
Heap enumeration
    ↓
shared indexes
    ↓
low-memory analyzers in parallel
    ↓
graph-heavy analysis alone
```

The exact concurrency should be dynamic.

---

# 67. Source Session Concurrency

CLRMD access must be treated as potentially expensive and potentially non-thread-friendly at the operation level.

Therefore the architecture should not assume:

```text
one heap session = unlimited parallel reads
```

Instead, each source session declares:

```csharp
ConcurrencyPolicy
```

Example:

```text
Dump:
    bounded concurrency
    serialized heap enumeration
    concurrent read-only index queries

Trace:
    usually sequential event consumption
    parallel post-processing
```

The adapter owns these constraints.

---

# 68. Streaming Contract

Large source data should normally be:

```csharp
IAsyncEnumerable<T>
```

or a synchronous iterator where async is not useful.

Do not automatically use:

```csharp
IReadOnlyList<T>
```

for:

- 85M objects
- millions of events
- stack samples
- references

---

# 69. Cancellation

Every streaming operation must accept:

```csharp
CancellationToken
```

Cancellation should be cooperative and frequent.

A 25 GB dump operation must not check cancellation only after the entire heap scan.

---

# 70. Progress

Progress should be semantic:

```text
HeapEnumeration:
    objects processed
    estimated total if known

TraceRead:
    events processed
    time range processed

Dominator:
    graph nodes processed
```

Avoid progress percentages when total work is unknown.

---

# 71. Partial Results

A cancelled analysis may have partial metrics.

Do not silently report them as complete.

Use:

```csharp
AnalysisCompletion
{
    Completed = false,
    Coverage = ...
}
```

---

# 72. Coverage

For trace:

```text
Processed time: 00:05:00
Total trace:    00:20:00
Coverage:       25%
```

For dump:

```text
Heap segments inspected: 98/100
```

Coverage should be evidence attached to any conclusion.

---

# 73. Evidence Sink

Analyzers should not construct reports.

They emit findings/evidence.

```csharp
public interface IEvidenceSink
{
    ValueTask<EvidenceReference> AddAsync(
        Evidence evidence,
        CancellationToken cancellationToken);
}
```

But analyzers can simply return findings containing evidence.

The sink becomes useful when evidence is large and must be persisted separately.

---

# 74. Evidence Storage

Evidence may include:

```text
small scalar
path
stack
graph slice
top-N table
time interval
object sample
```

Large evidence should be represented as references:

```text
EvidenceArtifactReference
```

not embedded arrays.

---

# 75. Large Evidence Example

Bad:

```csharp
Finding
{
    RetainingPath = ImmutableArray<ulong>(100_000)
}
```

Better:

```text
Finding
 └── Evidence
      └── GraphSliceArtifact
           └── ArtifactReference
```

The report can request details on demand.

---

# 76. Correlation Contract

Cross-source analysis deserves its own interface.

```csharp
public interface ICorrelationModule : IAnalysisModule
{
}
```

More importantly, define correlation primitives.

```csharp
public sealed record CorrelationWindow(
    DateTimeOffset Start,
    DateTimeOffset End);
```

---

# 77. Temporal Alignment

Do not assume timestamps from dump and trace are directly comparable.

Possible sources:

```text
wall-clock time
monotonic time
trace-relative time
dump timestamp
file timestamp
```

The correlation layer needs:

```csharp
public sealed record TimeReference(
    TimeReferenceKind Kind,
    DateTimeOffset? Absolute,
    TimeSpan? RelativeOffset);
```

---

# 78. Dump Timestamp Uncertainty

A dump may have:

```text
creation time
```

but that does not necessarily mean:

```text
exact instant represented by every heap object
```

A dump represents a snapshot taken around an instant.

Correlation should therefore use a configurable window:

```text
dump time ± N minutes
```

rather than exact timestamp equality.

---

# 79. Correlation Confidence

Example:

```text
Dump:
    10:32:14

Trace:
    10:30:00–10:35:00

Correlation:
    High
```

But if:

```text
Dump:
    10:32

Trace:
    08:00–16:00
```

the relationship is weaker.

This uncertainty should propagate into findings.

---

# 80. Cross-Source Evidence

Example finding:

```text
Managed heap increased sharply between snapshots,
while the trace immediately preceding the second snapshot
shows elevated allocation from the same type family.
```

Evidence:

```text
Dump A:
    Type X = 1.2 GB

Dump B:
    Type X = 4.7 GB

Trace:
    Allocation rate for X = 2.8 GB/s

Correlation window:
    10:28–10:33
```

The finding should reference each independently.

---

# 81. Trend Contract

Trend analysis should not consume raw dumps directly whenever possible.

Instead:

```text
Dump 1 → summary artifact
Dump 2 → summary artifact
Dump 3 → summary artifact
        ↓
    TrendAnalyzer
```

This avoids rescanning raw dumps unnecessarily.

---

# 82. Trend Data

```csharp
public sealed record TrendPoint(
    SourceId Source,
    DateTimeOffset? Timestamp,
    ImmutableDictionary<MetricId, MetricValue> Metrics);
```

For type trends:

```text
TypeTrendPoint
 ├── type identity
 ├── count
 ├── bytes
 └── source
```

---

# 83. Trend Identity

Never assume:

```text
MethodTable A == MethodTable B
```

across dumps.

Use:

```text
Assembly identity
Type name
Generic arguments
runtime identity
```

with fallback rules.

Even then, identity can be ambiguous.

The trend engine must represent:

```text
Matched
PossiblyMatched
Unmatched
```

---

# 84. Report Contract

Reports should consume findings and selected artifacts.

```csharp
public interface IReportGenerator
{
    ReportDescriptor Descriptor { get; }

    ValueTask<ReportResult> GenerateAsync(
        ReportContext context,
        CancellationToken cancellationToken);
}
```

Formats:

```text
JSON
Markdown
HTML
console
future UI model
```

The report generator should not know CLRMD.

---

# 85. Report Model

A report should be built from:

```text
Investigation summary
Findings
Evidence
Metrics
Warnings
Coverage
Analysis execution
```

not raw analysis classes.

---

# 86. Report Ordering

Findings should be ranked using explicit metadata:

```text
severity
impact
confidence
evidence strength
```

Do not use:

```text
whatever analyzer ran first
```

---

# 87. Analyzer Result Determinism

For the same:

```text
source
configuration
analyzer version
```

the result should be deterministic where possible.

This is important for:

- regression tests
- cache validation
- user trust
- trend comparisons

---

# 88. Artifact Fingerprints

Artifact reuse needs identity.

```csharp
public sealed record ArtifactFingerprint(
    SourceFingerprint Source,
    ArtifactTypeId Type,
    string AlgorithmVersion,
    string ConfigurationHash);
```

The artifact is reusable only when all relevant inputs match.

---

# 89. Source Fingerprint

Do not rely only on file path.

Use:

```text
file size
creation/modification metadata
content hash where practical
runtime metadata
```

For huge files, hashing the entire source may be expensive.

Use configurable fingerprint strategies.

---

# 90. Analyzer Versioning

An artifact generated by:

```text
DominatorAlgorithm v1
```

should not automatically be reused by:

```text
DominatorAlgorithm v2
```

Therefore:

```text
AlgorithmVersion
```

must be part of artifact identity.

---

# 91. Configuration Hashing

If an analyzer changes:

```text
top N = 100
```

to:

```text
top N = 1000
```

the artifact may or may not be reusable.

The analyzer declares which configuration values affect artifact semantics.

---

# 92. Index Contracts

DumpDetective already has large-scale indexes.

These should become artifacts.

Example:

```csharp
public interface IObjectIndex
{
    ValueTask<ObjectIndexEntry?> GetAsync(
        SnapshotObjectId id,
        CancellationToken cancellationToken);

    IAsyncEnumerable<ObjectIndexEntry> EnumerateAsync(...);
}
```

But the concrete implementation can be:

```text
memory-backed
disk-backed
memory-mapped
compressed
hybrid
```

without changing analyzer contracts.

---

# 93. ObjectIndexEntry

```csharp
public readonly record struct ObjectIndexEntry(
    SnapshotObjectId Object,
    ulong MethodTable,
    uint Size,
    ObjectGeneration Generation);
```

Do not add every property analyzers might ever want.

Keep the base index compact.

---

# 94. Secondary Indexes

Potential artifacts:

```text
Type → object ranges
MethodTable → object list
Address → object
Segment → object range
Generation → object range
```

Do not automatically build all indexes.

The planner should decide based on requested analyses.

---

# 95. Adaptive Indexing

Example:

```text
Analyzer A requires TypeIndex.
Analyzer B requires AddressLookup.
Analyzer C requires ReferenceReverseIndex.
```

Planner:

```text
build TypeIndex
build AddressLookup
skip ReverseIndex
```

This is substantially better than:

```text
build every possible index
```

---

# 96. Reference Graph

A full object graph for 85M objects can be enormous.

The graph contract must support:

```text
streamed edges
```

rather than:

```text
Dictionary<Object, List<Object>>
```

The dominator implementation can consume a disk-backed graph representation.

---

# 97. Dominator Artifact

The dominator tree should be represented separately from the raw graph.

Potential:

```text
Object → ImmediateDominator
Object → RetainedSize
```

This can be disk-backed.

Do not keep:

```text
full graph
+
dominator tree
+
all object metadata
```

in RAM simultaneously.

---

# 98. Two-Phase Graph Analysis

Preferred architecture:

### Phase 1

Build compact graph/index.

### Phase 2

Run graph algorithm.

### Phase 3

Discard or downgrade phase-1 state if no longer needed.

This reduces peak working set.

---

# 99. Analyzer State

Analyzers should ideally be stateless.

Bad:

```csharp
class Analyzer
{
    Dictionary<ulong, ObjectInfo> _objects;
}
```

Better:

```text
input artifact
    ↓
stream/process
    ↓
output artifact
```

If state is necessary, it should be explicitly bounded or disk-backed.

---

# 100. Analyzer Reuse

An analyzer instance should not necessarily be reused across investigations.

Prefer:

```text
module definition = immutable/stateless
execution = per invocation
```

This prevents accidental state leakage.

---

# 101. Plugin Model

Do not start with dynamic third-party plugins.

Initially:

```text
assembly-discovered built-in modules
```

with a stable descriptor interface is enough.

A future plugin boundary can be added once contracts stabilize.

---

# 102. Registration

Simple:

```csharp
services.AddAnalysisModule<HeapSummaryAnalyzer>();
services.AddAnalysisModule<RetentionAnalyzer>();
services.AddAnalysisModule<AllocationAnalyzer>();
```

The registry exposes:

```text
module descriptors
```

to the planner.

---

# 103. Module Dependency Validation

At startup:

```text
duplicate IDs
invalid requirements
cyclic artifact dependencies
missing artifact producers
```

should fail fast.

---

# 104. Circular Dependency Detection

Example:

```text
A requires B
B requires C
C requires A
```

Planner must reject this before execution.

---

# 105. Optional Dependencies

Sometimes an analyzer can improve its result if optional evidence exists.

Represent:

```csharp
Required
Optional
```

Example:

```text
HeapRetentionAnalyzer:
    required:
        heap
        references

    optional:
        historical dumps
        trace allocation evidence
```

This enables graceful degradation.

---

# 106. Analysis Modes

An analyzer can have:

```text
Required capability
Optional capability
Preferred artifact
```

Example:

```text
GCPressureAnalyzer
```

can operate on:

```text
trace with GC events
```

and optionally enrich from:

```text
runtime counters
```

---

# 107. Graceful Degradation

Suppose:

```text
trace has GC events
trace lacks CPU samples
```

GC analyzer should run.

CPU correlation should be skipped.

The planner should report:

```text
CPU correlation unavailable: CpuSamples capability absent.
```

This is better than failing the entire investigation.

---

# 108. Analysis Selection

User may request:

```text
all
memory
gc
cpu
threads
exceptions
```

The planner maps categories to modules.

Avoid hardcoding CLI logic into analyzers.

---

# 109. Analysis Packs

Useful concept:

```text
Memory Pack
GC Pack
CPU Pack
Concurrency Pack
Full Diagnostic Pack
```

These are selection presets, not analyzers.

---

# 110. Execution Telemetry

Every plan node should produce:

```text
duration
CPU
peak working set estimate
bytes read
bytes written
items processed
warnings
status
```

This becomes essential for tuning DumpDetective itself.

---

# 111. AnalysisStatistics

```csharp
public sealed record AnalysisStatistics(
    TimeSpan Duration,
    long ItemsProcessed,
    long BytesRead,
    long BytesWritten,
    ResourceUsage Usage);
```

Avoid assuming all analyzers can provide all values.

Use nullable fields.

---

# 112. Error Model

Errors should distinguish:

```text
InputInvalid
UnsupportedRuntime
UnsupportedFormat
CorruptData
CapabilityUnavailable
ResourceLimit
Cancelled
InternalError
```

This gives much better diagnostics than generic exceptions.

---

# 113. Source Errors vs Analysis Errors

If a dump fails to read one corrupted segment:

```text
Source warning
```

not necessarily:

```text
entire investigation failed
```

If a required artifact cannot be built:

```text
dependent analysis skipped
```

with an explicit reason.

---

# 114. Failure Propagation

DAG semantics:

```text
A fails
 ↓
B requires A → skipped
 ↓
C independent of A → still runs
```

This is crucial.

One broken analyzer must not destroy the whole diagnostic report.

---

# 115. Report Coverage

Every report should include:

```text
sources analyzed
analyses completed
analyses skipped
analyses partial
warnings
coverage limitations
```

A report without coverage information can create false confidence.

---

# 116. Security Boundary

Dump and trace files are untrusted input.

Adapters must avoid:

```text
arbitrary code execution
unsafe deserialization
unbounded allocations
path traversal
```

Report generation must also escape:

```text
type names
module names
exception messages
trace payload strings
```

especially HTML.

---

# 117. Resource Exhaustion Protection

Potential attacks or accidental worst cases:

```text
85M objects
billions of references
huge strings
pathological event counts
deep stacks
massive exception payloads
```

The architecture needs configurable limits:

```text
max evidence items
max report rows
max retained paths
max stack depth
max per-finding payload
```

---

# 118. Report Data Limits

Never allow:

```text
"dump every object into JSON"
```

Default report behavior should be:

```text
top N
aggregates
representative samples
references to detailed artifacts
```

---

# 119. Sampling Contracts

When sampling is used, explicitly report it.

Bad:

```text
Objects inspected: 10M
```

if only a sample was used.

Better:

```text
Sampled 10M of estimated 85M objects.
```

And findings should downgrade confidence where appropriate.

---

# 120. Configuration Contract

Configuration should be immutable:

```csharp
public sealed record DumpDetectiveOptions(
    ResourceBudget Resources,
    AnalysisOptions Analysis,
    StorageOptions Storage,
    ReportOptions Reports);
```

Do not allow analyzers to mutate global options.

---

# 121. Storage Options

```csharp
public sealed record StorageOptions(
    string WorkingDirectory,
    StorageMode Mode,
    long MemoryCacheLimitBytes,
    long DiskLimitBytes);
```

Potential modes:

```text
Auto
MemoryPreferred
DiskPreferred
```

But `Auto` should be the default.

---

# 122. Memory Policy

The system should treat memory as a budget.

For example:

```text
Input:
    25 GB dump

Available RAM:
    32 GB

Analyzer budget:
    6–8 GB

```

The exact budget must account for:

```text
CLR
CLRMD native memory
OS file cache
memory mappings
other application allocations
```

Do not equate:

```text
dump size
```

with:

```text
required RAM
```

---

# 123. Disk Policy

Disk-backed indexes can trade:

```text
RAM ↓
disk ↑
I/O ↑
```

The planner should choose storage based on:

```text
estimated cardinality
available disk
required random access
reuse probability
```

---

# 124. Automatic Spill

An aggregation should be able to transition:

```text
RAM hash table
      ↓ threshold
spill runs
      ↓
external merge
```

without changing analyzer semantics.

This is a storage-engine concern.

---

# 125. Collection APIs

Avoid returning:

```csharp
List<T>
Dictionary<TKey,TValue>
```

from source APIs for potentially huge datasets.

Prefer:

```text
IAsyncEnumerable<T>
cursor
paged lookup
artifact query
```

---

# 126. Query APIs

For disk-backed artifacts:

```csharp
public interface IObjectIndexQuery
{
    ValueTask<ObjectIndexEntry?> FindAsync(...);

    IAsyncEnumerable<ObjectIndexEntry> FindByTypeAsync(...);

    IAsyncEnumerable<ObjectIndexEntry> FindByRangeAsync(...);
}
```

The query API is semantic.

It does not reveal:

```text
B-tree
sorted file
LMDB
SQLite
mmap
```

---

# 127. But Don't Hide Cost

A semantic API should document cost.

Example:

```text
FindByTypeAsync:
    sequential range scan

FindAsync:
    O(log n)
```

The analyzer needs to know whether an operation is cheap.

A beautiful abstraction that hides a 4 GB scan is dangerous.

---

# 128. Cost Metadata

Queries can expose:

```csharp
QueryCostEstimate
```

where useful.

Example:

```text
Random lookup: cheap
Type enumeration: moderate
Reverse reference lookup: expensive
```

This can inform planning.

---

# 129. API Naming

Avoid generic names:

```text
GetData()
GetObjects()
GetEvents()
```

Prefer semantic names:

```text
EnumerateObjectsAsync()
ReadAllocationsAsync()
EnumerateReferencesAsync()
GetTypeAsync()
```

This makes contracts self-documenting.

---

# 130. Async Everywhere?

Do not blindly make every API async.

For CPU-bound local algorithms:

```csharp
Analyze(...)
```

may be simpler.

Use async when:

- I/O exists
- streaming exists
- cancellation is useful
- source processing naturally yields

Do not turn every property access into:

```csharp
await GetFooAsync()
```

---

# 131. Value Types for Hot Records

High-volume records should often be:

```csharp
readonly record struct
```

Examples:

```text
HeapObject
ObjectReference
AllocationEvent
CpuSample
```

This reduces object allocation.

But do not overuse structs for large objects.

---

# 132. String Interning

Type names and provider names repeat heavily.

Use:

```text
IDs
interned strings
string tables
```

where measured to matter.

Never assume `string.Intern()` is appropriate; CLR global interning can retain strings for process lifetime.

Use a custom bounded string table when necessary.

---

# 133. Event Payloads

Avoid allocating one managed object per trace event if millions of events exist.

Potential design:

```text
decoder
 ↓
struct event
 ↓
aggregator
```

For very hot providers, specialized decoding can write directly into aggregators.

The generic event abstraction should not force object allocation.

---

# 134. Trace Processing Strategy

Trace adapters should support:

```text
single pass
filtered pass
time-window pass
provider-filtered pass
```

The planner should request only required providers where possible.

---

# 135. Trace Re-reading

Sometimes multiple analyzers need the same event stream.

Do not necessarily reread the trace for each analyzer.

Options:

### Strategy A

One shared pass with multiple consumers.

### Strategy B

Build reusable event artifacts.

### Strategy C

Re-read source if cheap enough.

Planner chooses based on:

```text
trace size
I/O cost
memory cost
analysis requirements
```

---

# 136. Why Not a Universal Event Bus?

An event bus sounds attractive:

```text
Trace → EventBus → all analyzers
```

but can cause:

- buffering
- backpressure complexity
- analyzer coupling
- accidental materialization
- nondeterministic ordering

Prefer explicit shared trace passes or artifacts.

---

# 137. Multi-Consumer Streaming

If implemented, it should be bounded:

```text
producer
 ↓
bounded channel
 ├→ consumer A
 ├→ consumer B
 └→ consumer C
```

Never use an unbounded channel for arbitrary event rates.

---

# 138. Backpressure

If one analyzer is slow:

```text
trace producer
```

must not grow memory indefinitely.

Options:

```text
slow all consumers
spill
drop only explicitly droppable data
```

For diagnostics, silently dropping events is usually unacceptable unless the analysis declares itself sampling-based.

---

# 139. Source Pass Planning

A better planner may group analyzers:

```text
Pass 1:
    GC
    allocation
    exception

Pass 2:
    CPU stacks

Pass 3:
    thread scheduling
```

This minimizes trace rereads.

---

# 140. Dump Pass Planning

Likewise:

```text
Heap enumeration pass:
    type histogram
    size histogram
    generation stats
    object index
```

Then:

```text
Reference pass:
    reference graph
    roots
    retention
```

Then:

```text
expensive graph pass:
    dominator
```

---

# 141. Pass Contracts

A pass is an execution optimization, not a semantic result.

```csharp
public sealed record AnalysisPass(
    PassId Id,
    SourceId Source,
    ImmutableArray<PlanNodeId> Consumers,
    PassStrategy Strategy);
```

Analyzers should not need to know whether they were executed in a shared pass.

---

# 142. Planner Optimization

The planner should perform:

```text
dependency deduplication
pass fusion
artifact reuse
capability pruning
resource-aware ordering
```

This is the optimization layer.

---

# 143. Planner Correctness

Planner optimizations must preserve semantic results.

Do not optimize based on assumptions like:

```text
these two analyzers probably won't care about ordering
```

unless contracts guarantee it.

---

# 144. Ordering

Most analyzers should be order-independent.

Where ordering matters, make it explicit through DAG dependencies.

Never rely on registration order.

---

# 145. Deterministic IDs

Finding IDs should be stable enough for trend comparison.

Potential:

```text
hash(
    analyzer ID
    finding category
    semantic subject identity
)
```

Do not hash raw object addresses for cross-dump findings.

---

# 146. Finding Identity Across Dumps

Example:

```text
Finding:
    Cache retained 4.2 GB
```

Across snapshots, the finding identity could be:

```text
Analyzer:
    CacheRetention

Subject:
    Type family / cache identity

Finding kind:
    Retention
```

This allows:

```text
first observed
peak
latest
trend
```

---

# 147. Finding Lifecycle

A trend engine can model:

```text
New
Persistent
Growing
Stable
Recovered
Intermittent
Unknown
```

This is much more useful than treating each snapshot independently.

---

# 148. Finding Correlation

Two findings may represent the same root cause:

```text
Dump:
    large retained cache

Trace:
    allocation spike
```

The correlation engine can produce:

```text
RootCauseCandidate
```

but should not collapse the original findings.

Preserve provenance.

---

# 149. Root Cause Candidate

```csharp
public sealed record RootCauseCandidate(
    CandidateId Id,
    ImmutableArray<FindingReference> SupportingFindings,
    Explanation Explanation,
    ConfidenceAssessment Confidence);
```

This is explicitly an inference layer.

---

# 150. Do Not Call It Root Cause Too Early

A diagnostic tool should distinguish:

```text
Observation
Finding
Correlation
Hypothesis
Root cause
```

Recommended terminology:

```text
RootCauseCandidate
```

unless evidence is strong enough to justify stronger language.

---

# 151. Explainability Contract

Every finding should be reconstructible from:

```text
evidence
algorithm
configuration
source
artifact
```

This makes reports auditable.

---

# 152. Analyzer Documentation Contract

Every module should document:

```text
Purpose
Required capabilities
Optional capabilities
Artifacts consumed
Artifacts produced
Complexity
Memory behavior
Known limitations
False positives
Confidence model
```

This should be machine-readable where practical.

---

# 153. Example Analyzer Descriptor

```csharp
new AnalysisDescriptor(
    Id: "memory.retention",
    Name: "Managed Retention Analysis",
    Version: new Version(2, 0),
    Scope: AnalysisScope.Source,
    Requirements:
    [
        new CapabilityRequirement(AnalysisCapability.ManagedHeap),
        new CapabilityRequirement(AnalysisCapability.ObjectReferences)
    ],
    Produces:
    [
        ArtifactTypeIds.RetentionSummary
    ],
    Resources:
        ResourceProfiles.GraphHeavy);
```

---

# 154. Concrete Example: Single Dump

Input:

```text
25 GB dump
85M objects
```

Requested:

```text
memory
```

Planner:

```text
OpenDump
   ↓
HeapEnumeration
   ├── TypeHistogram
   ├── ObjectIndex
   └── SegmentStats
          ↓
ReferenceEnumeration
          ↓
ReferenceGraph
          ↓
DominatorTree
          ↓
RetentionAnalysis
```

Execution:

```text
stream heap
spill index
stream references
build graph
run dominator
discard temporary graph if possible
produce findings
```

---

# 155. Concrete Example: Single Trace

Input:

```text
45-minute nettrace
```

Requested:

```text
gc + allocations + cpu
```

Planner:

```text
OpenTrace
   ↓
TracePass
   ├── GC summary
   ├── allocation aggregation
   └── CPU sample aggregation
          ↓
      analyzers
```

Potentially one source pass if provider data can be decoded efficiently.

---

# 156. Concrete Example: Dump Trend

Inputs:

```text
dump A
dump B
dump C
```

Planner:

```text
Dump A → HeapSummary
Dump B → HeapSummary
Dump C → HeapSummary
       ↓
TypeTrendAnalyzer
       ↓
GrowthFindings
```

If retention is requested:

```text
each dump
   ↓
selected retention artifact
   ↓
trend correlation
```

Do not necessarily run full dominator analysis on every dump.

The planner should choose the minimum evidence needed.

---

# 157. Concrete Example: Dump + Trace

Inputs:

```text
dump at 12:30
trace 12:00–12:45
```

Planner:

```text
Dump → HeapSummary
     → RetentionEvidence

Trace → AllocationSummary
      → GCEvents

          ↓

TemporalCorrelation
          ↓
MemoryPressureCorrelation
          ↓
Finding
```

---

# 158. Important Cross-Source Limitation

The system must not claim:

```text
trace allocation X
```

directly caused:

```text
dump object Y
```

unless there is actual identity evidence.

Instead:

```text
trace shows increased allocations of type X
+
dump shows increased retained memory associated with X
```

This supports:

```text
strong correlation
```

not object-level causality.

---

# 159. Testability

Each contract should have a fake implementation.

Example:

```csharp
FakeManagedHeapSource
FakeObjectReferenceSource
FakeTraceEventSource
```

Tests should not require CLRMD for algorithm tests.

---

# 160. Contract Tests

Adapters need contract tests:

```text
EnumerateObjects
    returns valid object identities

References
    targets belong to source snapshot

Type metadata
    stable method table mapping

Trace timestamps
    monotonic ordering guarantees if promised

Cancellation
    stops enumeration

Dispose
    releases resources
```

---

# 161. Golden Tests

Use known dumps/traces.

Validate:

```text
top types
GC counts
allocation totals
retention paths
finding IDs
confidence factors
```

Do not snapshot entire reports blindly.

Prefer semantic assertions.

---

# 162. Performance Tests

For dump analyzer:

```text
1M objects
10M objects
50M objects
85M objects
```

Measure:

```text
wall time
peak working set
managed heap
disk usage
I/O
GC
```

For trace:

```text
1M
10M
100M
events
```

---

# 163. Contract Evolution

Interfaces should remain small because versioning them is expensive.

Prefer:

```csharp
sealed record options
```

over adding many parameters:

```csharp
Analyze(foo, bar, baz, qux, ...)
```

This gives compatible evolution.

---

# 164. Avoid Generic Type Explosion

Do not build:

```csharp
IArtifact<TInput,TOutput,TMetadata,TStorage,TQuery>
```

It becomes impossible to understand.

Prefer a small number of semantic contracts.

---

# 165. Recommended Core Assemblies

A likely project layout:

```text
DumpDetective.Core
DumpDetective.Analysis
DumpDetective.Artifacts
DumpDetective.Execution
DumpDetective.Reporting

DumpDetective.Dumps
DumpDetective.Traces

DumpDetective.Analysis.Memory
DumpDetective.Analysis.GC
DumpDetective.Analysis.CPU
DumpDetective.Analysis.Threads
DumpDetective.Analysis.Correlation
DumpDetective.Analysis.Trends
```

Potentially:

```text
DumpDetective.Storage
```

if storage becomes substantial.

---

# 166. Dependency Direction

Strict rule:

```text
Core
 ↑
Analysis contracts
 ↑
Adapters / implementations
```

Never:

```text
Core → CLRMD
Core → TraceEvent
```

CLRMD belongs in:

```text
DumpDetective.Dumps
```

TraceEvent belongs in:

```text
DumpDetective.Traces
```

---

# 167. Core Should Know Neither CLRMD Nor TraceEvent

This is worth enforcing with architecture tests.

For example:

```text
DumpDetective.Core
must not reference
Microsoft.Diagnostics.Runtime
Microsoft.Diagnostics.Tracing.TraceEvent
```

This is a hard architectural boundary.

---

# 168. Analysis Assemblies

Memory analysis may reference:

```text
DumpDetective.Core
DumpDetective.Artifacts
```

but should not need:

```text
CLRMD
```

unless implementing an adapter.

This keeps analysis algorithms testable.

---

# 169. One Important Exception

A highly specialized CLR runtime diagnostic may genuinely require CLRMD semantics.

That analysis can live in:

```text
DumpDetective.Analysis.Runtime
```

and explicitly depend on a specialized semantic runtime contract.

Do not contaminate all analyzers.

---

# 170. Source-Specific Escape Hatch

Sometimes the semantic abstraction loses important information.

Provide:

```csharp
IAdvancedSourceAccess
```

only to specialized modules.

Example:

```csharp
public interface IClrRuntimeAdvancedAccess
{
    object NativeHandle { get; }
}
```

But this should be:

```text
rare
documented
non-portable
```

and never used by generic modules.

---

# 171. Is `object NativeHandle` Good?

No, ideally not.

Better:

```csharp
internal interface IClrMdBridge
```

accessible only inside the dump-specific assembly.

If a module requires it, that module becomes explicitly dump/runtime-specific.

That is preferable to leaking `object`.

---

# 172. API Visibility Strategy

Use:

```text
public semantic contracts
internal adapter bridges
```

The majority of the codebase should only see semantic contracts.

---

# 173. Storage Engine Boundary

The artifact store should not know:

```text
analyzer semantics
```

It only knows:

```text
artifact type
size
lifetime
storage policy
serialization
query implementation
```

---

# 174. Serialization

Not every artifact should be serializable.

Some artifacts are:

```text
execution-only
```

Examples:

```text
open CLRMD heap
trace decoder state
live cursors
```

Persist only stable artifacts.

---

# 175. Artifact Schema

Persisted artifacts require:

```text
artifact type
schema version
algorithm version
source fingerprint
configuration fingerprint
creation metadata
```

Without these, artifact reuse becomes dangerous.

---

# 176. Cache Validation

Before reuse:

```text
source fingerprint matches?
artifact schema supported?
algorithm version compatible?
configuration compatible?
artifact complete?
storage not corrupted?
```

If any fail:

```text
rebuild
```

---

# 177. Crash Recovery

A partially written artifact must not look valid.

Use:

```text
temporary file
write
flush
finalize
atomic rename
```

or equivalent transactional semantics.

---

# 178. Artifact Integrity

Large artifacts should have:

```text
checksum
record counts
footer
version
```

This makes corruption detectable.

---

# 179. Reporting Large Artifacts

Reports should use:

```text
lazy artifact expansion
```

Example:

```text
Top retaining paths
  1. ...
  2. ...
  3. ...

[full paths available in artifact]
```

The report remains small.

---

# 180. CLI Contract

The CLI should map to the investigation model.

Example conceptual syntax:

```text
dumpdetective analyze dump.dmp
dumpdetective analyze trace.nettrace
dumpdetective analyze dump1.dmp dump2.dmp
dumpdetective analyze dump.dmp trace.nettrace
```

The CLI should construct:

```text
InvestigationDefinition
```

and hand it to the engine.

---

# 181. No Analyzer-Specific CLI Plumbing

Avoid:

```text
if memory:
    new MemoryAnalyzer()
if gc:
    new GcAnalyzer()
```

The CLI should select:

```text
analysis packs/categories
```

and the planner resolves modules.

---

# 182. User Overrides

Users may specify:

```text
--memory-budget
--working-directory
--analysis memory,gc
--max-concurrency 2
--report json,html
```

These become immutable configuration.

---

# 183. Diagnostic Explain Mode

A valuable feature:

```text
--explain-plan
```

which outputs:

```text
Selected analyses:
    ...

Required capabilities:
    ...

Artifacts:
    ...

Estimated memory:
    ...

Estimated disk:
    ...

Passes:
    ...
```

This is invaluable for debugging planner behavior.

---

# 184. Execution Explainability

Likewise:

```text
--verbose
```

should show:

```text
Pass 1 started
ObjectIndex spilled
Pass 1 completed
Dominator started
```

This makes large-dump behavior understandable.

---

# 185. Planner Heuristics

Initial planner should be deterministic and simple.

Recommended order:

1. validate sources
2. discover capabilities
3. select analyses
4. resolve requirements
5. build artifact dependencies
6. deduplicate artifacts
7. fuse compatible passes
8. estimate resources
9. schedule
10. execute

Do not start with machine-learning optimization.

---

# 186. Dynamic Adaptation

Later, runtime measurements can adjust:

```text
concurrency
spill thresholds
batch sizes
cache sizes
```

But semantic planning should remain deterministic.

---

# 187. Batch Size Contract

Hot enumeration APIs can accept:

```csharp
EnumerationOptions.BatchSize
```

but the source may ignore it.

Do not make batch size part of correctness.

It is an optimization hint.

---

# 188. Memory Pooling

Potentially use:

```text
ArrayPool<T>
MemoryPool<T>
```

for hot buffers.

But avoid pooling enormous buffers indefinitely.

Pool usage should be bounded.

---

# 189. Unsafe Code

Unsafe optimizations are justified in:

```text
binary decoding
index serialization
high-volume event parsing
```

but should remain isolated.

The semantic layer should stay safe managed code.

---

# 190. Thread Safety

Contracts should explicitly document whether objects are:

```text
thread-safe
single-consumer
multi-reader
```

Default assumption should be:

```text
not thread-safe unless stated
```

This prevents accidental concurrent CLRMD calls.

---

# 191. Disposal

Source sessions must own:

```text
CLRMD resources
trace readers
file handles
mappings
temporary files
```

Artifact handles should independently own their storage references.

Disposal order should follow the DAG lifecycle.

---

# 192. Investigation Lifecycle

Recommended:

```text
Created
 ↓
Validated
 ↓
Planned
 ↓
Executing
 ↓
Completed / Partial / Failed / Cancelled
 ↓
Reportable
 ↓
Disposed
```

This should be represented in execution state rather than scattered booleans.

---

# 193. Eventing from the Engine

The engine may expose progress:

```csharp
IAsyncEnumerable<AnalysisProgress>
```

instead of forcing callbacks.

This integrates naturally with CLI/UI.

---

# 194. AnalysisProgress

```csharp
public sealed record AnalysisProgress(
    PlanNodeId Node,
    AnalysisProgressKind Kind,
    long? ItemsProcessed,
    long? ItemsTotal,
    TimeSpan Elapsed);
```

---

# 195. Logging

Use structured logging.

Include:

```text
investigation_id
source_id
analysis_id
plan_node_id
artifact_id
```

This makes a 25 GB analysis diagnosable.

---

# 196. Correlation IDs

Every execution should carry:

```text
InvestigationId
SourceId
PlanNodeId
AnalysisId
```

into logs and metrics.

---

# 197. Metrics

Engine metrics:

```text
analysis.duration
analysis.bytes_read
analysis.bytes_written
analysis.items_processed
artifact.size
artifact.cache_hit
artifact.cache_miss
source.open.duration
```

These should be independent from user-facing findings.

---

# 198. Observability Must Not Affect Analysis

Telemetry should be:

```text
low overhead
bounded
optional
```

Never allocate huge diagnostic telemetry structures just to measure the analyzer.

---

# 199. Architecture Tests

Enforce:

```text
Core references no adapters
Analysis contracts reference no CLRMD
Analysis contracts reference no TraceEvent
Reports reference no adapters
Adapters implement semantic contracts
```

This protects the architecture as the codebase grows.

---

# 200. Final Recommended Contract Set

The minimum stable public semantic surface should be approximately:

```text
InvestigationDefinition
SourceDefinition
SourceDescriptor
SourceRelationship

IAnalysisSource
ISourceSession

AnalysisCapability
AnalysisRequirement

IManagedHeapSource
IObjectReferenceSource
ITypeMetadataSource
IThreadSource

ITraceEventSource
IAllocationEventSource
IGcEventSource
ICpuSampleSource
IStackSource

IAnalysisModule
AnalysisDescriptor
AnalysisContext
AnalysisResult

IArtifact
IArtifactStore
ArtifactReference

Evidence
Finding
ConfidenceAssessment
ImpactAssessment

IReportGenerator
```

Do not grow this list casually.

---

# 201. The Most Important Architectural Boundary

There are actually **three** boundaries, not two.

```text
                 Semantic
Source ─────────→ Model ─────────→ Analysis
  │                 │                 │
  │                 │                 │
CLRMD/TraceEvent    │                 │
specific            │                 │
                    │                 │
                    └──── Evidence ───┘
```

The mistake would be to think:

```text
CLRMD abstraction
TraceEvent abstraction
```

are enough.

They are not.

The stable boundary is:

```text
raw diagnostic technology
        ↓
semantic observations
        ↓
analytical artifacts
        ↓
findings
```

---

# 202. Where the Current DumpDetective Architecture Should Move

The existing DumpDetective components such as:

```text
HeapAnalysisCache
MemoryBackedObjectIndexWriter
DiskBackedObjectIndexWriter
ObjectIndexReader
HeapEntry
```

should not be thrown away.

They should be repositioned.

For example:

```text
Current:
HeapAnalysisCache
    ↓
future:
ArtifactStore / Analysis artifact

Current:
HeapEntry
    ↓
future:
ObjectIndexEntry

Current:
DiskBackedObjectIndexWriter
    ↓
future:
Disk-backed Artifact implementation

Current:
MemoryBackedObjectIndexWriter
    ↓
future:
Memory-backed Artifact implementation
```

The important change is that analyzers stop knowing which implementation they received.

---

# 203. What Should NOT Be Migrated Directly

Avoid carrying forward:

```text
analyzer-specific caches
global mutable state
report-specific DTOs
CLRMD types in analyzer APIs
massive dictionaries exposed as domain state
```

Migration should preserve algorithms, not accidental architecture.

---

# 204. Recommended Implementation Order

Do not implement every interface immediately.

Phase 1:

```text
Core IDs
SourceDefinition
InvestigationDefinition
Source capabilities
AnalysisDescriptor
Finding
Evidence
```

Phase 2:

```text
IManagedHeapSource
IObjectReferenceSource
ITypeMetadataSource
```

Phase 3:

```text
ArtifactStore
ObjectIndex artifact
HeapSummary artifact
```

Phase 4:

```text
AnalysisPlanner
DAG
executor
```

Phase 5:

```text
existing dump analyzers migrated
```

Phase 6:

```text
Trace adapter
trace contracts
```

Phase 7:

```text
trace analyzers
```

Phase 8:

```text
trend
correlation
cross-source findings
```

---

# 205. Why Dump Support Should Be Migrated Before Trace

This may sound counterintuitive because the goal is unified architecture.

But DumpDetective already has:

```text
mature dump algorithms
large-dump indexing
performance data
validated assumptions
```

Use that as the first implementation proving ground.

The architecture should be designed for traces, but initially proven against dumps.

---

# 206. Migration Strategy for Existing Analyzers

For each analyzer:

### Step 1

Document:

```text
what it actually needs
```

not what its current constructor receives.

### Step 2

Declare capabilities.

### Step 3

Identify required artifacts.

### Step 4

Extract algorithm from CLRMD.

### Step 5

Make output immutable.

### Step 6

Produce evidence.

### Step 7

Move report formatting out.

### Step 8

Add resource profile.

### Step 9

Add validation.

---

# 207. Example Existing Analyzer Migration

Suppose:

```text
StringAnalyzer
```

currently:

```csharp
Analyze(ClrRuntime runtime)
```

Refactor toward:

```text
StringAnalyzer
requires:
    ManagedHeap
    ObjectEnumeration
    TypeMetadata
```

It consumes:

```text
HeapObject stream
Type metadata
```

and produces:

```text
StringSummary
StringFinding
```

No CLRMD reference.

---

# 208. Example WCF Analyzer

A WCF analyzer may require runtime-specific metadata.

Do not force it into generic memory semantics.

Declare:

```text
SourceKind = Dump
Capability = RuntimeMetadata
```

and expose a dedicated:

```csharp
IRuntimeMetadataSource
```

This preserves modularity without pretending all runtimes are generic.

---

# 209. Trace Analyzer Example

`AllocationHotspotAnalyzer`:

```text
requires:
    AllocationEvents
    optional:
        CPU samples
        GC events
```

produces:

```text
AllocationSummary
AllocationHotspotFinding
```

No dump dependency.

---

# 210. Cross-Source Analyzer Example

`AllocationRetentionCorrelationAnalyzer`:

```text
scope:
    Investigation

requires:
    Dump HeapSummary
    Dump TypeSummary
    Trace AllocationSummary
    TemporalRelationship
```

produces:

```text
CorrelationFinding
```

This makes cross-source behavior explicit.

---

# 211. Trend Analyzer Example

`ManagedMemoryTrendAnalyzer`:

```text
scope:
    SourceGroup

requires:
    multiple dump sources
    HeapSummary
```

produces:

```text
ManagedMemoryTrend
GrowthFinding
```

It should not open dumps itself.

---

# 212. Strong Rule for Higher-Level Analyzers

A trend/correlation analyzer should preferably consume artifacts rather than reopen raw sources.

Why:

```text
raw source = expensive
artifact = reusable
```

This gives a clean layering:

```text
source analysis
      ↓
artifact
      ↓
cross-source analysis
```

---

# 213. Potential Problem: Too Many Artifacts

This architecture can produce artifact explosion.

If every analyzer produces an artifact:

```text
36 analyzers
→ 100 artifacts
→ huge disk usage
```

Therefore:

> Artifacts should exist when they provide reuse, scale, or semantic value — not merely because an interface says they can.

Small analyzer results can remain in memory.

---

# 214. Potential Problem: Too Much Abstraction

A contract for every noun creates:

```text
IHeap
IHeap2
IHeapReader
IHeapEnumerator
IHeapQuery
IHeapProvider
IHeapAccessor
```

This is abstraction rot.

Prefer a few strong interfaces.

---

# 215. Potential Problem: Generic Semantic Model Loses Runtime Details

This is real.

If the abstraction says only:

```text
Object
Type
Thread
```

some advanced diagnostics become impossible.

Solution:

```text
core semantic interfaces
+
specialized capability interfaces
+
rare adapter-specific bridge
```

Not:

```text
everything generic
```

---

# 216. Potential Problem: DAG Becomes Overengineered

A DAG can become a monster.

Avoid:

```text
100 node types
custom scheduler language
general workflow expressions
distributed execution
```

Start with:

```text
Source
Artifact
Analysis
Correlation
Report
```

That is enough.

---

# 217. Potential Problem: Resource Estimates Are Wrong

They will be.

Therefore estimates should:

```text
guide scheduling
not guarantee safety
```

The executor still needs runtime monitoring and spill strategies.

---

# 218. Potential Problem: Disk Becomes the New Bottleneck

Disk-backed indexes solve RAM problems but can produce:

```text
hours of random I/O
```

Therefore storage design must consider:

```text
sequential layouts
sorted runs
batching
locality
mmap only where beneficial
```

Do not equate memory mapping with automatically fast storage.

---

# 219. Potential Problem: Unified Architecture Forces Common Denominator

This is the biggest conceptual danger.

If everything must fit:

```text
Object
Event
Thread
Metric
```

the system may become weak at both dumps and traces.

The correct unification is:

```text
common orchestration
common evidence
common findings
common artifacts
common reporting
```

while preserving:

```text
source-specific semantic richness
```

---

# 220. Potential Problem: Cross-Source Correlation Produces False Causality

This must be actively prevented.

The report language should distinguish:

```text
Observed
Correlated
Consistent with
Likely
Strongly supported
```

from:

```text
Caused by
Root cause
```

unless evidence justifies it.

---

# 221. Potential Problem: Cross-Snapshot Matching

Never promise exact object continuity across dumps.

Trend analysis should primarily compare:

```text
aggregates
semantic identities
stable application identifiers
```

not addresses.

---

# 222. Potential Problem: Analyzer Independence

If analyzers share mutable caches:

```text
Analyzer A
    modifies cache
Analyzer B
    assumes cache state
```

then the architecture becomes order-dependent.

Shared state must therefore be:

```text
immutable artifact
or
explicit storage service
```

---

# 223. Potential Problem: Reports Become the Real Business Logic

This happens easily.

Bad:

```text
ReportGenerator:
    detect leak
    calculate trend
    correlate trace
```

Correct:

```text
Analysis:
    interpret evidence

Report:
    present interpretation
```

---

# 224. Potential Problem: Confidence Becomes Cosmetic

If every analyzer says:

```text
High confidence
```

the field becomes meaningless.

Confidence should have documented factors and tests.

For each analyzer:

```text
what increases confidence?
what decreases confidence?
```

---

# 225. Potential Problem: Partial Data

A trace may miss providers.

A dump may have corrupted memory.

The semantic layer must preserve:

```text
unknown
unavailable
partial
```

rather than normalizing them into:

```text
zero
empty
false
```

This is critical for correctness.

---

# 226. Recommended Final Architecture

The refined architecture is:

```text
                         ┌─────────────────────┐
                         │       CLI/UI         │
                         └──────────┬──────────┘
                                    │
                         InvestigationDefinition
                                    │
                         ┌──────────▼──────────┐
                         │      Planner         │
                         │ capabilities         │
                         │ dependencies         │
                         │ passes               │
                         │ resources            │
                         └──────────┬──────────┘
                                    │
                              AnalysisPlan
                                    │
                         ┌──────────▼──────────┐
                         │      Executor        │
                         │ DAG + budgets        │
                         │ streaming + spill    │
                         └──────────┬──────────┘
                                    │
                ┌───────────────────┼───────────────────┐
                │                   │                   │
                ▼                   ▼                   ▼
          Dump analyses       Trace analyses      Cross-source
                                                   analyses
                │                   │                   │
                └───────────────────┼───────────────────┘
                                    ▼
                           Artifacts + Findings
                                    │
                         ┌──────────▼──────────┐
                         │  Evidence/Provenance │
                         └──────────┬──────────┘
                                    │
                         ┌──────────▼──────────┐
                         │      Reporting       │
                         └─────────────────────┘

Sources:
    DumpAdapter → CLRMD
    TraceAdapter → TraceEvent
```

---

# 227. Final Contract Philosophy

The architecture should follow five rules.

## Rule 1

**Raw diagnostic libraries stop at adapters.**

```text
CLRMD ─┐
       ├→ semantic source contracts
Trace ─┘
```

## Rule 2

**Analyzers declare what they need.**

```text
requirements
capabilities
artifacts
```

## Rule 3

**Huge data flows through streams and artifacts.**

```text
never default to materialization
```

## Rule 4

**Findings explain conclusions.**

```text
evidence
impact
confidence
remediation
false positives
```

## Rule 5

**Cross-source analysis operates on explicit evidence and uncertainty.**

```text
correlation ≠ causation
```

---

# 228. Recommended First Implementation Slice

Do not implement the entire architecture.

The highest-value vertical slice is:

```text
InvestigationDefinition
        ↓
DumpAdapter
        ↓
IManagedHeapSource
        ↓
HeapSummaryAnalyzer
        ↓
HeapSummaryArtifact
        ↓
Finding
        ↓
MarkdownReport
```

Then extend:

```text
ObjectIndexArtifact
        ↓
RetentionAnalyzer
        ↓
DominatorArtifact
```

Only once this works efficiently on the existing 3.3 GB and 25 GB dumps should the trace contracts be implemented.

The architecture is validated when:

```text
DumpAnalyzer
```

can run entirely through the new contracts without knowing that CLRMD exists.

Then:

```text
TraceAnalyzer
```

should be able to run through the same orchestration without pretending traces are dumps.

---

# 229. Architectural North Star

The final system should feel like this to an analyzer author:

```text
I declare:

    what evidence I need
    what capabilities I require
    what artifacts I consume
    what artifacts I produce
    what findings I can make
    how expensive I am

The engine figures out:

    where the data comes from
    how to obtain it
    how to share it
    how to store it
    how to schedule me
    how to stay within memory
    how to correlate my output
    how to report it
```

That is the correct separation of concerns.

The analyzer author should think about:

> **diagnostic reasoning**

—not:

> CLRMD object lifetimes, trace reader mechanics, temporary files, memory mapping, report formatting, or scheduler behavior.

At the same time, the platform engineer should be able to optimize:

```text
CLRMD enumeration
disk indexes
memory mapping
trace decoding
batch sizes
spill thresholds
parallelism
```

without rewriting analyzers.

That separation is what will allow DumpDetective 2 to scale from:

```text
one dump
```

to:

```text
25 GB dump + 45 minute nettrace + historical snapshots
```

without the architecture collapsing under its own abstractions.
