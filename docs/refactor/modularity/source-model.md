# Source Model — Artifacts, Capabilities, Identity, Time

Core design doc for the multi-source platform. Read this before any phase doc.
Parent: [../modularity-plan.md](../modularity-plan.md).

---

## 1. The reframe

Today's architecture treats "a dump" as the thing the application *is about*. The unified
dump+trace doc ([../../improvements/unified-dump-trace-architecture.md](../../improvements/unified-dump-trace-architecture.md))
proposes adding trace as a second, parallel pipeline that fuses with dump at a normalized signal
layer, plus a mode enum (`SingleDump | MultiDump | TraceOnly | Combined`) to route between them.

That works, but it embeds a scaling problem it names itself: mode explosion. Four modes today;
add a gcdump source and you have combinatorial routing (`DumpTrace`, `DumpGcdump`,
`TraceGcdump`, `DumpTraceGcdump`…), each needing an orchestrator.

**The alternative: there are no modes.** A dump is not the subject of analysis — a *process under
investigation* is. A dump is one **evidence artifact** about that process. So is a trace, a gcdump,
an ETW log, a GC log, a set of perf counters. The application analyzes a **session**: an ordered
collection of artifacts about one process (or one process across time).

Everything else follows from that:

| Today's "mode" | Under the session model |
|---|---|
| Single dump | Session with 1 dump artifact |
| Multi-dump trend | Session with N dump artifacts at N time anchors |
| Trace-only | Session with 1 trace artifact |
| Dump + trace combined | Session with 1 dump + 1 trace artifact |
| (unbuilt) dump + trace + gcdump | Session with 3 artifacts — **no new code path** |

No mode enum. No `CombinedOrchestrationService`. One orchestrator that inspects what artifacts a
session contains, computes which analyzers are satisfiable, and runs them. This is the single
highest-leverage decision in the whole plan, and it's what the rest of this document exists to
make possible.

---

## 2. Artifact — the unit of input

```csharp
public sealed record ArtifactDescriptor
{
    public ArtifactId Id { get; init; }              // session-unique, stable
    public string SourceKind { get; init; }          // "clr-dump", "nettrace", "gcdump", ...
    public string Path { get; init; }
    public TimeAnchor CapturedAt { get; init; }
    public ProcessIdentity Process { get; init; }    // pid, start time, image name, runtime version
    public IReadOnlySet<Capability> Provides { get; init; }
}
```

`ProcessIdentity` matters more than it looks: it's how the session decides whether two artifacts
are even *about the same thing*. Correlating a dump of process A with a trace of process B is a
category error, and the platform should refuse it (or loudly caveat it) rather than silently
producing confident nonsense.

**Ingest SPI** — each source kind implements:

```csharp
public interface IArtifactSource
{
    string SourceKind { get; }
    bool CanHandle(string path);
    ValueTask<ArtifactDescriptor> ProbeAsync(string path, CancellationToken ct);
    ValueTask<IArtifactIndex> IndexAsync(ArtifactDescriptor descriptor,
                                         IIndexStorage storage,
                                         IProgress<IndexProgress> progress,
                                         CancellationToken ct);
}
```

Dump ingest (ClrMD + the existing single-pass heap scan) becomes *an implementation of this*,
not the engine's reason for existing. Trace ingest is a second implementation. Neither knows the
other exists.

---

## 3. Capability vocabulary

The join between "what artifacts provide" and "what analyzers need". This is the mechanism that
replaces mode routing.

```
heap.objects            heap.types              heap.roots
heap.references         heap.reverse-references heap.generations
heap.segments           heap.handles            heap.statics
heap.strings            heap.finalizer-queue

runtime.modules         runtime.threads         runtime.stacks
runtime.exceptions      runtime.jit             runtime.locks

trace.cpu-samples       trace.gc-events         trace.alloc-samples
trace.contention-events trace.exception-events  trace.thread-timeline
trace.jit-events        trace.http-events       trace.custom-events

temporal.point          temporal.interval       temporal.series
```

Analyzers declare requirements, not source types:

```csharp
[AnalyzerModule(key: "gc-pressure", order: 110, tags: ["gc"])]
[RequiresCapability("heap.generations", "heap.objects")]
[OptionalCapability("trace.gc-events", Fidelity = FidelityBoost.Major)]
internal sealed class GcPressureAnalyzer : IAnalyzer { ... }
```

Three consequences worth naming explicitly:

1. **Graded fidelity, not binary availability.** `GcPressureAnalyzer` runs on a dump alone (heap
   composition → inferred pressure), on a trace alone (actual pause events), or on both (composition
   *plus* measured pause cost — the strongest result). It reports which capabilities it actually
   got, and its confidence reflects that. This is far better than having a `GcAnalyzer` and a
   separate `TraceGcAnalyzer` that produce two disconnected findings about the same phenomenon.
2. **New sources light up existing analyzers for free.** Add a gcdump source that provides
   `heap.objects` + `heap.types` and every analyzer requiring only those becomes runnable against
   gcdumps with zero analyzer changes.
3. **Capability gaps become an honest, first-class report output.** "17 of 34 analyzers ran; 12
   skipped for lack of `trace.*` capabilities — supply a `.nettrace` to enable them" is a
   genuinely useful thing to tell a user, and it falls out of this model rather than needing to be
   hand-maintained.

This also subsumes the existing `requiresEngineCapabilities` idea (e.g. `heap.reverse-references`
being expensive and skippable via `DD_SKIP_REVERSE_INDEX_BUILD=1`) — an optional index is just a
capability the session may or may not provide.

---

## 4. Entity identity — the actual hard problem

Cross-source correlation is a **join**, and a join needs keys. The unified doc says "correlate by
keys (type/method/module/thread/runtime dimensions)" — that's the right instinct, but stringly-typed
correlation keys will produce false matches at scale. This is where the design has to be precise.

```csharp
public abstract record EntityRef
{
    public abstract EntityKind Kind { get; }
    public abstract string JoinKey { get; }        // canonical, cross-source comparable
    public MatchFidelity Fidelity { get; init; }   // how trustworthy is JoinKey for this entity
}

public sealed record TypeRef : EntityRef      // JoinKey: canonicalized assembly-qualified name
{
    public string CanonicalName { get; init; }
    public ulong? MethodTable { get; init; }       // dump-local handle, NOT part of JoinKey
    public int? TypeToken { get; init; }
    public ModuleRef? Module { get; init; }
}

public sealed record MethodRef : EntityRef    // JoinKey: declaringType + name + normalized signature
{
    public TypeRef DeclaringType { get; init; }
    public string Name { get; init; }
    public string NormalizedSignature { get; init; }
    public ulong? MethodDesc { get; init; }        // dump-local
    public int? MethodToken { get; init; }         // trace-local
}

public sealed record ThreadRef : EntityRef    // JoinKey: osThreadId (+ process start time)
{
    public uint OsThreadId { get; init; }
    public int? ManagedThreadId { get; init; }
}

public sealed record ObjectRef : EntityRef    // artifact-scoped by nature — never cross-source
{
    public ulong Address { get; init; }
    public ArtifactId Artifact { get; init; }
}
```

**The critical split:** `JoinKey` is the canonical cross-source identity; `MethodTable`,
`MethodDesc`, `Address`, `MethodToken` are **source-local handles** carried along for drill-down
but never used for joining. A dump knows types by `MethodTable`; a trace knows them by name from
event payloads. They join on canonical name — and the platform must be honest that this join is
sometimes lossy.

### Canonicalization rules (and their fidelity)

| Entity shape | Rule | Fidelity |
|---|---|---|
| Simple type `System.String` | Assembly-qualified, version/culture/token stripped | **Exact** |
| Generic instantiation `List<string>` | Canonical form `System.Collections.Generic.List\`1[System.String]`, recursive normalization of args | **Exact** |
| Generic definition vs instantiation | Kept distinct; instantiation joins to definition as a *parent* relation, not identity | **Exact** |
| Array/pointer/byref | Suffix-normalized (`T[]`, `T[,]`, `T*`, `T&`) | **Exact** |
| Method overloads | Name + normalized param type list (canonicalized recursively) | **Exact** |
| Async state machine `<Foo>d__12` | Unwrapped to declaring method `Foo` + flagged as state machine | **High** (ordinal `12` is compile-order-dependent — don't join on it) |
| Lambda/closure `<>c__DisplayClass4_0` | Unwrapped to enclosing method where recoverable | **Low** — ordinals shift between builds; join only within one build |
| Local function `<Foo>g__Bar\|3_1` | Unwrapped to `Foo` + local name `Bar` | **Medium** |
| Anonymous type | Shape-based key (property names + types) | **Medium** |
| Dynamic/reflection-emitted | No stable identity | **None** — never joined |

`MatchFidelity` is not decoration: it **caps** the confidence of any finding derived from that
join (see [observation-and-correlation-model.md](observation-and-correlation-model.md) § 6). A
cross-source correlation resting on a `Low`-fidelity lambda match cannot be reported as
high-confidence, no matter how strongly the two observations agree.

---

## 5. Temporal model

A dump is a point; a trace is an interval; a multi-dump sequence is a sparse series. Making time
explicit is what lets trend, trace, and combined analysis be *the same mechanism*.

```csharp
public sealed record TimeAnchor
{
    public DateTime? WallClockUtc { get; init; }
    public TimeSpan? ProcessUptime { get; init; }
    public long? MonotonicTicks { get; init; }
    public AnchorConfidence Confidence { get; init; }  // Exact | Approximate | Unknown
}

public sealed record TemporalExtent
{
    public TemporalKind Kind { get; init; }   // Point | Interval | Series
    public TimeAnchor Start { get; init; }
    public TimeAnchor? End { get; init; }
}
```

### Alignment strategies, in preference order

1. **Explicit** — user supplies offsets (`--anchor trace.nettrace=+00:02:15`). Always wins.
2. **Wall clock** — both artifacts carry trustworthy capture timestamps. Good to ~seconds.
3. **Process uptime** — same `ProcessIdentity` with known start time; align on uptime. Robust to
   clock skew, which is why it's preferred over wall clock when both are available.
4. **Event landmark** — a distinctive event visible in both (a specific exception, a GC at a
   distinctive heap size). Powerful but heuristic.
5. **Unaligned** — no reliable alignment. **Correlation still runs, but restricted to
   non-temporal joins** (entity-based: "this type is hot in the trace and huge in the dump"), and
   every finding carries an explicit "artifacts not time-aligned" caveat.

Fake temporal precision is the most dangerous failure mode in a multi-source tool — a user will
believe "the leak started at 14:32" far more readily than they should. `AnchorConfidence` must
propagate into findings, not be swallowed during alignment.

---

## 6. Session

```csharp
public sealed record AnalysisSession
{
    public SessionId Id { get; init; }
    public IReadOnlyList<ArtifactDescriptor> Artifacts { get; init; }
    public ProcessIdentity? PrimaryProcess { get; init; }
    public SessionTimeline Timeline { get; init; }        // aligned view + alignment provenance
    public IReadOnlySet<Capability> AvailableCapabilities { get; init; }  // union over artifacts
    public CorrelationPolicy Correlation { get; init; }
    public OutputSpec Output { get; init; }
}
```

CLI surface collapses accordingly — one verb, N inputs:

```bash
dd analyze app.dmp                          # 1 artifact  → point-in-time analysis
dd analyze t0.dmp t1.dmp t2.dmp             # 3 artifacts → temporal series, trend synthesis
dd analyze app.nettrace                     # 1 artifact  → trace analysis
dd analyze app.dmp app.nettrace             # 2 artifacts → combined, correlated
dd analyze t0.dmp t1.dmp app.nettrace       # 3 artifacts → trend + trace correlation
```

`--baseline` / `--trend` flags become sugar over artifact ordering rather than mode switches, kept
for backward compatibility.

---

## 7. What this model deletes

Worth being concrete about the payoff, since the cost is high:

- The `SingleDump | MultiDump | TraceOnly | Combined` mode enum — never built.
- `SingleDumpOrchestrationService`, `TrendOrchestrationService`, and the proposed
  `TraceOrchestrationService` / `CombinedOrchestrationService` — replaced by one capability-driven
  session orchestrator.
- `SingleDumpReportDocument` / `TrendReportDocument` polymorphism — one session report shape with
  1..N artifacts.
- Most of the ~30 bespoke `IAnalyzerTrendComparer` implementations — see
  [observation-and-correlation-model.md](observation-and-correlation-model.md) § 5.
- The dump-vs-trace analyzer duplication that a parallel-pipeline design would force (`GcAnalyzer`
  + `TraceGcAnalyzer` + a correlator to reconcile them → one capability-graded `GcPressureAnalyzer`).

---

## 8. Open questions

- **Multi-process sessions.** A distributed hang spans processes. The model above assumes one
  `PrimaryProcess`. Extending to multi-process means `ProcessIdentity` becomes part of every
  `EntityRef` join key, which is a real generalization — deliberately deferred, but the shape
  above doesn't preclude it.
- **Live targets as artifacts.** Attaching to a running process is conceptually "an artifact that
  keeps producing capabilities." Interesting but not designed for here.
- **Capability versioning.** If `heap.objects` gains a field, is that a new capability or a
  versioned one? Leaning: capabilities carry a minor version, analyzers declare minimums.
- **How much of `EntityRef` is hot-path safe.** These are records with string keys; the project's
  performance rules forbid per-object allocation in scan loops. Resolution: `EntityRef` is an
  *observation-layer* type (thousands of instances), never a heap-scan-loop type (millions).
  Interning + integer entity IDs inside indices, `EntityRef` materialized only at observation
  boundaries. This constraint must not be relaxed.
