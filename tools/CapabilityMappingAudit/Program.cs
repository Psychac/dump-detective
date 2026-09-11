// Phase 0 item 3 (docs/refactor/modularity/phase-0-foundation.md): capability mapping.
// Generates docs/refactor/modularity/capability-mapping.md from source, on demand.
// Run: dotnet run --project tools/CapabilityMappingAudit
//
// Required capabilities are derived mechanically (syntax-only, same standard as
// tools/ContractSurfaceAudit) from the actual ClrMD/IHeapAnalysisCache calls and, for the 11
// already-retyped analyzers, the actual Sdk.Analysis.AnalysisContext properties read — both
// checked against ground truth (CapabilityVocabulary.cs, the Tier-1 surface table in
// phase-1-full-extraction-retyping-plan.md, and the dump-side *Query.cs implementations
// themselves) rather than guessed. Optional (trace) capabilities are a hand-curated table below:
// unlike "required," this is a design judgment (what would this analyzer's finding quality gain
// from a live event stream), not something derivable from today's dump-only source.

using System.Text;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

string repoRoot = FindRepoRoot(AppContext.BaseDirectory);
string analyzersDir = Path.Combine(repoRoot, "src", "DumpDetective.Analysis", "Analyzers");
string outputPath = Path.Combine(repoRoot, "docs", "refactor", "modularity", "capability-mapping.md");

// ── Ground truth: CapabilityVocabulary.cs constants (src/DumpDetective.Sdk/Artifacts/CapabilityVocabulary.cs) ──

const string HeapObjects = "heap.objects";
const string HeapTypes = "heap.types";
const string HeapRoots = "heap.roots";
const string HeapReferences = "heap.references";
const string HeapReverseReferences = "heap.reverse-references";
const string HeapSegments = "heap.segments";
const string HeapHandles = "heap.handles";
const string HeapFinalizerQueue = "heap.finalizer-queue";
const string HeapReachability = "heap.reachability";
const string HeapDominators = "heap.dominators";
const string RuntimeModules = "runtime.modules";
const string RuntimeThreads = "runtime.threads";
const string RuntimeStacks = "runtime.stacks";
const string RuntimeJit = "runtime.jit";
const string RuntimeLocks = "runtime.locks";
const string DumpObjectFields = "dump.object-fields";

const string TraceCpuSamples = "trace.cpu-samples";
const string TraceGcEvents = "trace.gc-events";
const string TraceAllocSamples = "trace.alloc-samples";
const string TraceContentionEvents = "trace.contention-events";
const string TraceExceptionEvents = "trace.exception-events";
const string TraceThreadTimeline = "trace.thread-timeline";
const string TraceJitEvents = "trace.jit-events";
const string TraceHttpEvents = "trace.http-events";
const string TraceCustomEvents = "trace.custom-events";

// ── SDK AnalysisContext property -> capability (1:1, ground truth: AnalysisContext.cs + CapabilityVocabulary.cs) ──

var sdkContextPropertyToCapability = new Dictionary<string, string>(StringComparer.Ordinal)
{
    ["HeapObjects"] = HeapObjects,
    ["HeapObjectLookup"] = HeapObjects,
    ["HeapRoots"] = HeapRoots,
    ["HeapHandles"] = HeapHandles,
    ["HeapSegments"] = HeapSegments,
    ["HeapFinalizerQueue"] = HeapFinalizerQueue,
    ["HeapSyncBlocks"] = RuntimeLocks,
    ["HeapTypeStatistics"] = HeapTypes,
    ["HeapReferences"] = HeapReferences,
    ["HeapReverseReferences"] = HeapReverseReferences,
    ["HeapReachability"] = HeapReachability,
    ["HeapDominators"] = HeapDominators,
    ["RuntimeThreads"] = RuntimeThreads,
    ["RuntimeModules"] = RuntimeModules,
    ["RuntimeJit"] = RuntimeJit,
    ["DumpObjectFields"] = DumpObjectFields,
};

// ── Legacy IHeapAnalysisCache member -> capability (ground truth: dump-side *Query.cs bridge implementations) ──

var cacheMemberToCapability = new Dictionary<string, string>(StringComparer.Ordinal)
{
    ["GetStaticRootedAddresses"] = HeapRoots,
    ["GetPinnedRootedAddresses"] = HeapRoots,
    ["GetStaticFieldsByRootAddress"] = HeapRoots,
    ["GetOrBuildValidRoots"] = HeapRoots,
    ["GetOrBuildRootTriples"] = HeapRoots,
    ["TryResolveStackFrameOwner"] = RuntimeStacks,
    ["GetOrCountThreadStackRoots"] = RuntimeStacks,
    ["GetOrBuildTypeStatistics"] = HeapTypes,
    ["GetSampleInstanceAddress"] = HeapTypes,
    ["TryGetDistinctMethodTables"] = HeapTypes,
    ["TryGetGlobalSizeBuckets"] = HeapTypes, // bridged 1:1 by IHeapTypeStatisticsQuery.TryGetGlobalSizeBuckets()
    ["MethodTableHasOutgoingRefs"] = HeapReferences,
    ["EnumerateIndexedEntriesAsTuples"] = HeapObjects,
    ["TryGetObjectMetadata"] = HeapObjects,
    ["TryGetReverseIndexProvider"] = HeapReverseReferences,
    ["TryGetForwardIndexProvider"] = HeapReferences,
    ["TryGetReachableAddressProvider"] = HeapReachability,
    ["TryGetDominatorTreeProvider"] = HeapDominators,
    ["TryGetThreadRetentionProvider"] = HeapDominators,
    // SizeTier, ObjectScanCount, CacheHits, CacheMisses: cache diagnostics, not a capability — excluded.
};

// ── Raw ClrMD API (legacy analyzers calling context.Heap/context.Runtime directly) -> capability ──

var rawApiPatternToCapability = new (Regex Pattern, string Capability)[]
{
    (new Regex(@"\bEnumerateObjects\s*\("), HeapObjects),
    (new Regex(@"\.GetObject\s*\("), HeapObjects),
    (new Regex(@"\bGetTypeByMethodTable\s*\("), HeapTypes),
    (new Regex(@"\bEnumerateRoots\s*\("), HeapRoots),
    (new Regex(@"\.EnumerateHandles\s*\("), HeapHandles),
    (new Regex(@"\bGetSegmentByAddress\s*\("), HeapSegments),
    (new Regex(@"\bEnumerateFinalizableObjects\s*\("), HeapFinalizerQueue),
    (new Regex(@"\bEnumerateSyncBlocks\s*\("), RuntimeLocks),
    (new Regex(@"\bEnumerateJitManagers\s*\("), RuntimeJit),
    (new Regex(@"[Rr]untime\.Threads\b"), RuntimeThreads),
    (new Regex(@"[Rr]untime\.AppDomains\b"), RuntimeModules),
    (new Regex(@"\.Fields\b"), DumpObjectFields),
    (new Regex(@"\bGetFieldByName\s*\("), DumpObjectFields),
    (new Regex(@"\bReadObjectField\s*\("), DumpObjectFields),
    (new Regex(@"\bReadStringField\s*\("), DumpObjectFields),
    (new Regex(@"\bTryReadField\s*\("), DumpObjectFields),
    (new Regex(@"\bTryReadStringField\s*\("), DumpObjectFields),
    // Several legacy analyzers bypass IHeapAnalysisCache entirely and cast context.Cache to the
    // concrete HeapAnalysisCache to read the Phase-1 index's TypeAggregates directly
    // (AllocationPatternAnalyzer's own comment: "Pure Phase-2 post-processor... Reads
    // TypeAggregates built during Phase 1"). This is the same underlying per-type generation/size
    // data IHeapTypeStatisticsQuery.GetTypeStatistics() exposes under heap.types — verified by
    // reading HeapTypeStatisticsQuery.cs, whose HeapTypeStatistics record carries the identical
    // Gen0/1/2Count/LohSize/IsFinalizableType fields TypeAggregateIndexEntry does.
    (new Regex(@"\bTryGetHeapIndex\s*\("), HeapTypes),
    (new Regex(@"\bTypeAggregates\b"), HeapTypes),
};

// ── Hand-curated: optional trace capabilities per analyzer (design judgment, not derivable from source) ──

var optionalTraceCapabilities = new Dictionary<string, (string[] Capabilities, string Rationale)>(StringComparer.Ordinal)
{
    ["AllocationPatternAnalyzer"] = ([TraceAllocSamples], "Direct match — real allocation events replace inference from static snapshot deltas."),
    ["ArrayAnalyzer"] = ([TraceAllocSamples], "Allocation hotspot attribution for array types over time, not just a point-in-time census."),
    ["AsyncStateMachineAnalyzer"] = ([TraceThreadTimeline], "Fire-and-forget detection strengthens with observed task scheduling/completion activity, not just suspended-state census."),
    ["AsyncTaskAnalyzer"] = ([TraceThreadTimeline], "Same as AsyncStateMachineAnalyzer — task lifecycle observed over time beats a single snapshot."),
    ["BoxingAnalyzer"] = ([TraceAllocSamples], "Boxing allocation hotspots are directly an allocation-event concern."),
    ["CollectionAnalyzer"] = ([TraceAllocSamples, TraceGcEvents], "Growth-over-time and survival-across-collections both sharpen 'wasteful/unbounded collection' claims beyond one snapshot."),
    ["CrashAnalyzer"] = ([TraceExceptionEvents], "Direct match — exception frequency/type distribution leading up to the dump."),
    ["DbConnectionAnalyzer"] = ([], "No matching capability in the current vocabulary (no trace.db-events); dump-only signal today."),
    ["DominatorAnalyzer"] = ([TraceAllocSamples, TraceGcEvents], "Retained-size growth attributed to an allocation site, and survival across GCs, both require a time series a single dump can't provide."),
    ["EventLeakAnalyzer"] = ([TraceAllocSamples], "Subscriber-count growth over time is the actual leak signal; a snapshot only sees the current count."),
    ["FinalizableObjectAnalyzer"] = ([TraceGcEvents], "Finalizer queue drain rate is a GC-event-stream concern, not visible in a single snapshot."),
    ["GCGenerationAnalyzer"] = ([TraceGcEvents], "Real per-generation collection frequency/pause data beats inferring generation pressure from static counts."),
    ["GCHandleAnalyzer"] = ([TraceGcEvents], "Handle churn/collection frequency over time strengthens a handle-leak claim beyond a live-count snapshot."),
    ["GCRootAnalyzer"] = ([TraceAllocSamples], "Allocation-site attribution for retained objects — 'when/where was this rooted object allocated'."),
    ["HangAnalyzer"] = ([TraceThreadTimeline, TraceContentionEvents], "Deadlock/hang detection strengthens with observed blocking duration and contention history, not just one blocked-stack snapshot."),
    ["HeapTopologyAnalyzer"] = ([TraceGcEvents], "Segment/heap growth over time contextualizes a single topology snapshot."),
    ["HttpObjectAnalyzer"] = ([TraceHttpEvents], "Direct match — real HTTP request/response activity, not just live HttpClient/handler object counts."),
    ["JitAnalyzer"] = ([TraceJitEvents], "Direct match — real JIT compilation events (tiering, recompilation) beat a static compiled-method census."),
    ["LeakCandidateAnalyzer"] = ([TraceAllocSamples, TraceGcEvents], "Cross-analyzer leak scoring benefits from the same growth/survival time series as DominatorAnalyzer."),
    ["LockGraphAnalyzer"] = ([TraceContentionEvents], "Direct match — real contention duration/frequency beats a static sync-block snapshot."),
    ["LohFragmentationAnalyzer"] = ([TraceGcEvents], "Compaction frequency/GC events over time contextualize a single fragmentation snapshot."),
    ["MemoryAnalyzer"] = ([TraceGcEvents, TraceAllocSamples], "Heap size trend and allocation rate over time, not just one point-in-time total."),
    ["ModuleAnalyzer"] = ([], "Loaded-module list is inherently a snapshot concept; no trace capability meaningfully improves it."),
    ["ObjectShapeAnalyzer"] = ([], "Field layout/padding waste is a static type-shape concern; no trace capability applies."),
    ["ReferenceChainAnalyzer"] = ([TraceAllocSamples], "Allocation-site attribution for the chain's retained object, same reasoning as GCRootAnalyzer."),
    ["SegmentReservationAnalyzer"] = ([TraceGcEvents], "Reserved-vs-committed growth over time, not just one snapshot ratio."),
    ["SqlCommandAnalyzer"] = ([], "No matching capability in the current vocabulary (no trace.db-events); dump-only signal today."),
    ["SqlConnectionPoolAnalyzer"] = ([], "No matching capability in the current vocabulary (no trace.db-events); dump-only signal today."),
    ["SqlTransactionAnalyzer"] = ([], "No matching capability in the current vocabulary (no trace.db-events); dump-only signal today."),
    ["StaticRootLeakDetector"] = ([TraceAllocSamples], "Allocation-site attribution for statically-rooted growth, same reasoning as GCRootAnalyzer."),
    ["StringAnalyzer"] = ([TraceAllocSamples], "String allocation hotspot attribution by call site/type over time."),
    ["ThreadAnalyzer"] = ([TraceThreadTimeline, TraceContentionEvents], "Thread lifecycle and blocking history over time beat a single stack-snapshot census."),
    ["ThreadStackClusterAnalyzer"] = ([TraceCpuSamples, TraceThreadTimeline], "Correlating stack-signature clusters with actual CPU sample attribution strengthens which clusters matter."),
    ["TimerLeakAnalyzer"] = ([TraceThreadTimeline], "Timer callback firing activity over time distinguishes a live timer from an abandoned one better than a live-count snapshot."),
    ["WcfChannelAnalyzer"] = ([TraceCustomEvents], "No dedicated WCF trace capability exists; trace.custom-events is the only plausible escape hatch, unconfirmed."),
    ["WeakReferenceAnalyzer"] = ([TraceGcEvents], "Collection timing relative to when a weak reference clears is inherently a GC-event-stream concern."),
};

var rows = new List<AnalyzerCapabilityRow>();

foreach (string file in Directory.GetFiles(analyzersDir, "*.cs", SearchOption.TopDirectoryOnly)
    .OrderBy(f => f, StringComparer.Ordinal))
{
    string text = File.ReadAllText(file);
    var tree = CSharpSyntaxTree.ParseText(text, path: file);
    var root = tree.GetCompilationUnitRoot();

    ClassDeclarationSyntax? classDecl = root.DescendantNodes()
        .OfType<ClassDeclarationSyntax>()
        .FirstOrDefault(c => c.BaseList?.Types.Any(t => GetSimpleName(t.Type) is "IAnalyzer" or "IDeferredAnalyzer") == true);
    if (classDecl is null)
    {
        continue;
    }

    string className = classDecl.Identifier.Text;
    bool isSdkAnalyzer = root.Usings.Any(u => u.Name?.ToString() == "DumpDetective.Sdk.Analysis");
    string classBodyText = classDecl.ToFullString();

    var required = new SortedSet<string>(StringComparer.Ordinal);

    if (isSdkAnalyzer)
    {
        foreach ((string property, string capability) in sdkContextPropertyToCapability)
        {
            if (Regex.IsMatch(classBodyText, $@"\bcontext\.{Regex.Escape(property)}\b"))
            {
                required.Add(capability);
            }
        }
    }
    else
    {
        foreach ((string member, string capability) in cacheMemberToCapability)
        {
            if (Regex.IsMatch(classBodyText, $@"\b{Regex.Escape(member)}\b"))
            {
                required.Add(capability);
            }
        }
        foreach ((Regex pattern, string capability) in rawApiPatternToCapability)
        {
            if (pattern.IsMatch(classBodyText))
            {
                required.Add(capability);
            }
        }
    }

    (string[] Capabilities, string Rationale) optional = optionalTraceCapabilities.TryGetValue(className, out var found)
        ? found
        : (Array.Empty<string>(), "Not yet assessed.");

    rows.Add(new AnalyzerCapabilityRow(className, isSdkAnalyzer, required.ToList(), optional.Capabilities.ToList(), optional.Rationale));
}

WriteReport(outputPath, rows);
Console.WriteLine($"Wrote {rows.Count} analyzer rows to {outputPath}");

static string GetSimpleName(TypeSyntax type) => type switch
{
    QualifiedNameSyntax q => q.Right.Identifier.Text,
    IdentifierNameSyntax id => id.Identifier.Text,
    GenericNameSyntax g => g.Identifier.Text,
    _ => type.ToString(),
};

static string FindRepoRoot(string startDir)
{
    var dir = new DirectoryInfo(startDir);
    while (dir is not null)
    {
        if (Directory.Exists(Path.Combine(dir.FullName, "src", "DumpDetective.Core")))
        {
            return dir.FullName;
        }
        dir = dir.Parent;
    }
    throw new InvalidOperationException($"Could not locate repo root (src/DumpDetective.Core) above '{startDir}'.");
}

static void WriteReport(string outputPath, List<AnalyzerCapabilityRow> rows)
{
    var sb = new StringBuilder();
    sb.AppendLine("# Capability mapping");
    sb.AppendLine();
    sb.AppendLine("Generated by `tools/CapabilityMappingAudit` — **required-capability columns are");
    sb.AppendLine("scripted** (do not hand-edit); the **optional (trace) column is a hand-curated table");
    sb.AppendLine("embedded in the generator itself** (edit `optionalTraceCapabilities` in");
    sb.AppendLine("`tools/CapabilityMappingAudit/Program.cs`, then re-run). Re-run with:");
    sb.AppendLine("`dotnet run --project tools/CapabilityMappingAudit`.");
    sb.AppendLine();
    sb.AppendLine(
        "Phase 0 item 3 (see [phase-0-foundation.md](phase-0-foundation.md)): for each analyzer, " +
        "which capabilities from [source-model.md § 3](source-model.md) it actually needs, and " +
        "which it would optionally benefit from once trace exists. Direct input to Phase 3's " +
        "`[RequiresCapability]` attributes and the graded-fidelity design.");
    sb.AppendLine();
    int sdkCount = rows.Count(r => r.IsSdkAnalyzer);
    sb.AppendLine(
        $"**Required** capabilities are derived mechanically from real source: for the {sdkCount} " +
        "analyzers already retyped onto `Sdk.Analysis.IAnalyzer`, from the `AnalysisContext` " +
        "properties they actually read (each capability-scoped 1:1 — see " +
        "`src/DumpDetective.Sdk/Analysis/AnalysisContext.cs`); for the remaining " +
        $"{rows.Count - sdkCount} still on " +
        "`Core.Abstractions.IAnalyzer` (or `IDeferredAnalyzer`), from the actual `IHeapAnalysisCache` members and raw ClrMD " +
        "API calls (`heap.EnumerateObjects`, `Runtime.Threads`, `.Fields`, etc.) found in the " +
        "analyzer's own class body, each mapped to the capability that the corresponding dump-side " +
        "`*Query.cs` bridge implementation (`src/DumpDetective.Analysis/Sdk/`) actually exposes it " +
        "through — not guessed. Syntax-only regex matching, same limitations noted in " +
        "[contract-surface-inventory.md](contract-surface-inventory.md) apply (possible false " +
        "positives from identifier collisions, false negatives through local/field aliasing).");
    sb.AppendLine();
    sb.AppendLine(
        "**Optional (once trace exists)** is design judgment, not derivable from dump-only source " +
        "— each entry states its rationale so it can be disputed/revised. Several analyzers list no " +
        "optional capability because the current `CapabilityVocabulary` (see " +
        "`src/DumpDetective.Sdk/Artifacts/CapabilityVocabulary.cs`) has no matching trace concept " +
        "(e.g. no `trace.db-events` for the four DB/SQL analyzers) — flagged as vocabulary gaps for " +
        "Phase 3/6, not silently omitted.");
    sb.AppendLine();

    sb.AppendLine("| Analyzer | Variant | Required capabilities | Optional (trace) capabilities | Rationale |");
    sb.AppendLine("|---|---|---|---|---|");
    foreach (AnalyzerCapabilityRow row in rows)
    {
        string variant = row.IsSdkAnalyzer ? "SDK" : "Legacy";
        sb.AppendLine(
            $"| `{row.ClassName}` | {variant} | {FormatList(row.Required)} | " +
            $"{FormatList(row.OptionalTrace)} | {row.Rationale} |");
    }
    sb.AppendLine();

    int noOptional = rows.Count(r => r.OptionalTrace.Count == 0);
    sb.AppendLine($"**{rows.Count} analyzers mapped — {noOptional} currently have no optional trace " +
                   "capability, either because the analyzer's concern is inherently a snapshot " +
                   "(e.g. `ModuleAnalyzer`, `ObjectShapeAnalyzer`) or because no matching capability " +
                   "exists in the vocabulary yet (the four DB/SQL analyzers).**");

    Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
    File.WriteAllText(outputPath, sb.ToString());
}

static string FormatList(IReadOnlyCollection<string> values)
{
    if (values.Count == 0)
    {
        return "—";
    }
    return string.Join("<br>", values.Select(v => $"`{v}`"));
}

internal sealed record AnalyzerCapabilityRow(
    string ClassName,
    bool IsSdkAnalyzer,
    List<string> Required,
    List<string> OptionalTrace,
    string Rationale);
