# AspNetDiagnosticsAnalyzer — Design Sketch

> Priority: **P3 — deferred, low value-to-effort ratio**.
> Do not implement until P2 analyzers are complete and a concrete user request confirms the
> value. This doc exists to define scope and record why it is deferred, not to drive immediate
> implementation.
>
> Feasibility: **Low**. Server-side ASP.NET Core request state is largely ephemeral and version-
> coupled to Kestrel/SignalR/middleware internals. Most dumps will show few or zero live requests
> at capture time, capping the diagnostic value regardless of implementation quality.
>
> Effort: **XL** (~6+ wk). Kestrel + SignalR + HttpContext internals are three independently
> complex API surfaces, each version-coupled. A single vertical slice (e.g. Kestrel connection
> count only) might be achievable in ~2 wk, but the full scope is XL.

---

## 1. Problem statement

ASP.NET Core leaks and hangs manifest in the server-side objects that represent active HTTP
connections and requests. Four categories of interest:

1. **Live `HttpContext` instances** — if requests are stuck (awaiting slow I/O, a deadlock, or a
   hung middleware) they hold `HttpContext` and their associated request/response buffers alive.
   Counting live `HttpContext` instances and their estimated retained size is a proxy for "how
   many requests are in-flight / stuck."

2. **Kestrel connection state** — `KestrelConnection<TContext>` and `ConnectionContext` represent
   individual TCP connections. A large number of `ConnectionContext` instances may indicate a
   connection leak (e.g. missed `await context.DisposeAsync()` in a middleware).

3. **SignalR hub connections** — `HubConnectionContext` instances represent active SignalR
   connections. A growing count of `HubConnectionContext` objects in a dump (across trend runs)
   signals connection leaks in hub implementations.

4. **Middleware pipeline state** — long-lived objects captured by closures in custom middleware
   (e.g. a per-request `DbContext` captured in a lambda that outlives the request).

---

## 2. Why this is deferred

### 2.1 Ephemeral state — low signal at dump-capture time

HTTP requests in a healthy application complete in tens to hundreds of milliseconds. A dump
captured from a running application at an arbitrary moment will show at most a few dozen live
`HttpContext` instances even under high load. The analyzer only becomes useful under two narrow
conditions:
- The application is under active load at dump-capture time **and** requests are stuck (hang or
  slow I/O). In this case, the stuck requests' `HttpContext` instances will be live.
- A leak has caused `HttpContext` or `ConnectionContext` instances to outlive their requests. This
  requires the dumping trigger to be "OOM / high memory" rather than "hang."

For routine crash/OOM dumps — the primary DumpDetective use case — the server-side request
signal is low or zero. The DI/EF/Cache/NativeInterop analyzers produce actionable findings for a
much broader set of dumps.

### 2.2 Version coupling — high maintenance cost

`HttpContext` is an abstract class with a concrete `DefaultHttpContext` implementation.
`DefaultHttpContext` holds a `FeatureCollection` that stores all per-request features as a
heterogeneous dictionary. The specific feature types (`IHttpRequestFeature`,
`IHttpResponseFeature`, etc.) and their concrete implementations are Kestrel-version-specific.
Kestrel's `KestrelConnection<TContext>` internals changed significantly between .NET 5 and .NET 8
(e.g. the connection multiplexing layer introduced in HTTP/3). SignalR's `HubConnectionContext`
similarly changed between ASP.NET Core 3.1 and 6+.

Implementing reliable field introspection across all of these requires a per-version field-layout
matrix at least as complex as the DI scope leak analyzer's, for significantly less diagnostic
value (see §2.1).

---

## 3. Applicable types (for when implementation begins)

### 3.1 HttpContext

```
Microsoft.AspNetCore.Http.DefaultHttpContext
Microsoft.AspNetCore.Http.HttpContext              // abstract base — use for candidate MT discovery
```

Fields of interest on `DefaultHttpContext`:
- `_features` (`FeatureReferences<FeatureInterfaces>`) — feature collection
- `_request` / `_response` (`DefaultHttpRequest` / `DefaultHttpResponse`) — request/response objects

Since `HttpContext` is abstract, use the same subclass-discovery approach as `EfCoreAnalyzer` §3.2
(walk `TypeAggregates`, check `BaseType` chain for `HttpContext`).

### 3.2 Kestrel connection state

```
Microsoft.AspNetCore.Server.Kestrel.Core.Internal.Http.Http1Connection
Microsoft.AspNetCore.Server.Kestrel.Core.Internal.Http.Http2Connection
Microsoft.AspNetCore.Server.Kestrel.Core.Internal.Http3.Http3Connection
Microsoft.AspNetCore.Connections.ConnectionContext
```

`ConnectionContext` is abstract; the concrete types above are `internal`. Use type-name prefix
matching (`"Microsoft.AspNetCore.Server.Kestrel"` and `"Microsoft.AspNetCore.Connections"`) to
discover candidates. Field introspection is version-specific and should not be attempted in an
initial implementation — count and estimated size are sufficient for v1.

### 3.3 SignalR

```
Microsoft.AspNetCore.SignalR.HubConnectionContext
Microsoft.AspNetCore.SignalR.Internal.DefaultHubDispatcher
```

`HubConnectionContext` is public and sealed (as of ASP.NET Core 7). Fields include:
- `_connectionContext` (`ConnectionContext`) — the underlying transport connection
- `_hubProtocol` — the protocol implementation
- `_active` (`bool`) — whether the connection is still active

### 3.4 Type-name pattern approach

Use `TypeNamePatternMatcher.HasAnyPrefix` with:
- `"Microsoft.AspNetCore.Http."` — HttpContext-related types
- `"Microsoft.AspNetCore.Server.Kestrel."` — Kestrel internals
- `"Microsoft.AspNetCore.SignalR."` — SignalR hubs and connections
- `"Microsoft.AspNetCore.Connections."` — connection abstractions

All of these are detected as "ASP.NET Core present" markers without requiring field introspection.

---

## 4. Scan design (for when implementation begins)

### 4.1 Phase 1 — count-only (no field introspection)

Implement as `IHeapIndexScanParticipant`. In `OnHeapEntry`, filter by the prefix-matched MT set
and accumulate per-type instance counts and total sizes. Report `IsPresent`, type-name breakdowns,
and estimated retained sizes. No field reads.

This is achievable in ~1 wk and provides the "is ASP.NET Core present and what is the scale of
live connections" signal without any version-layout risk.

### 4.2 Phase 2 — field introspection (gated on per-version spike)

For each concrete type family (`DefaultHttpContext`, `HubConnectionContext`), run a per-version
field-layout spike (same pattern as `DiScopeLeakAnalyzer` §2.2) before implementing field reads.
Gate behind a version guard; degrade gracefully to count-only if layout is unresolvable.

---

## 5. Domain result and output model

```
AspNetDiagnosticsDomainResult : AnalyzerDomainResult
  IsPresent                              bool
  LiveHttpContextCount                   int
  LiveHttpContextEstimatedBytes          ulong
  KestrelConnectionCount                 int
  KestrelConnectionEstimatedBytes        ulong
  SignalRHubConnectionCount              int
  SignalRHubConnectionEstimatedBytes     ulong
  OtherAspNetTypeCount                   int
  ScanCapped                             bool
  ContextSnapshots                       List<AspNetContextSnapshot>   // top-K HttpContexts only

AspNetContextSnapshot
  Address                                ulong
  ConcreteTypeName                       string
  EstimatedRetainedBytes                 ulong
  RequestPath                            string?    // if readable from _request, else null
  Evidence                               Evidence
```

---

## 6. Registration fan-out

| Artifact | Class name |
|----------|-----------|
| Domain result | `AspNetDiagnosticsDomainResult` |
| Finding generator | `AspNetDiagnosticsFindingGenerator : IFindingGenerator<AspNetDiagnosticsDomainResult>` |
| Trend comparer | `AspNetDiagnosticsTrendComparer` — delta on connection counts |
| Section builder | `AspNetDiagnosticsSectionBuilder : ISectionBuilder<AspNetDiagnosticsDomainResult>` |

---

## 7. Key risks and mitigations

| Risk | Mitigation |
|------|-----------|
| Zero live requests at dump-capture time | Set `LiveHttpContextCount = 0`; section builder renders "No live ASP.NET Core requests at capture time" with a note that this is expected for non-hang dumps |
| Kestrel/SignalR internals change between .NET versions | Phase 1 (count-only) has no version risk; gate Phase 2 field reads on per-version spike results |
| `DefaultHttpContext` subclass naming conventions vary | Use base-class chain discovery (same as `EfCoreAnalyzer` §3.2) |
| `RequestPath` field read fails (field moved or private) | Make `RequestPath` optional; set to `null` and do not fail the analyzer |

---

## 8. Implementation recommendation

**Do not start this analyzer until P2 is complete.** When capacity allows, implement Phase 1
(count-only, no field introspection) first — this is ~1 wk of work with zero version-layout risk
and still provides the "ASP.NET Core is present at scale X" signal. Defer Phase 2 (field
introspection and `RequestPath` extraction) to a separate iteration gated on a user-confirmed
need.

The most useful single metric from Phase 1 is `KestrelConnectionCount`: a large count of
`ConnectionContext` instances in a heap dump is a reliable signal of a connection leak, even
without field introspection.

---

## 9. What this analyzer does NOT do

- Inspect middleware pipeline registration or startup configuration (static analysis, not
  a dump concern).
- Report on ASP.NET Core Minimal API route registrations.
- Diagnose Kestrel transport-layer issues (TLS, socket state) — those are native-memory concerns.
- Analyse gRPC service instances (a different hosting model, not addressed here).
- Replace tools like `dotnet-counters` or Application Insights for live request monitoring;
  this analyzer provides a snapshot, not a live view.
