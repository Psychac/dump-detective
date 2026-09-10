using DumpDetective.Analysis.Models;
using DumpDetective.Analysis.SdkBridge;
using DumpDetective.Core.Models;
using DumpDetective.Core.Options;
using DumpDetective.Sdk.Analysis;
using DumpDetective.Sdk.Artifacts;

namespace DumpDetective.Analysis.Analyzers;

/// <summary>
/// Phase 1 retyping batch (docs/refactor/modularity/phase-1-full-extraction-retyping-plan.md):
/// retyped onto the SDK's capability-scoped <see cref="Sdk.Analysis.IAnalyzer"/>, sourcing JIT heap
/// totals through <see cref="IRuntimeJitQuery"/> (<c>runtime.jit</c>) and every per-method/per-frame
/// fact through <see cref="IRuntimeThreadQuery"/> (<c>runtime.threads</c>) — this analyzer never
/// enumerated JIT-compiled methods directly even pre-retyping; everything about individual methods
/// (signature, hot/cold size, tiering, R2R) came from walking thread stacks. Runs through the
/// existing pipeline via <see cref="JitAnalyzerLegacyAdapter"/>.
/// </summary>
/// <remarks>
/// First retyping batch to need brand-new runtime/thread capability surfaces built from scratch —
/// unlike heap-side batches, no prior analyzer had exercised <c>IRuntimeJitQuery</c>/
/// <c>IRuntimeThreadQuery</c> at all, so both interfaces were redesigned to match what this analyzer
/// (the only real consumer so far) actually needs, not the speculative shape they originally shipped
/// with (see each interface's own remarks).
/// </remarks>
public sealed class JitAnalyzer : IAnalyzer, IProducesAnalyzerDomainResult
{
    public string Name => "JIT Analysis";
    public string Category => "Performance";

    public AnalyzerDomainResult? LastResult { get; private set; }

    public ValueTask AnalyzeAsync(AnalysisContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        IRuntimeJitQuery jitQuery = context.RuntimeJit
            ?? throw new InvalidOperationException($"{Name} requires the '{CapabilityVocabulary.RuntimeJit}' capability.");
        IRuntimeThreadQuery threadQuery = context.RuntimeThreads
            ?? throw new InvalidOperationException($"{Name} requires the '{CapabilityVocabulary.RuntimeThreads}' capability.");

        JitAnalysisOptions options = context.AnalyzerOptions as JitAnalysisOptions ?? new JitAnalysisOptions();

        LastResult = Analyze(jitQuery, threadQuery, options, cancellationToken);
        return ValueTask.CompletedTask;
    }

    private static JitDomainResult Analyze(
        IRuntimeJitQuery jitQuery,
        IRuntimeThreadQuery threadQuery,
        JitAnalysisOptions options,
        CancellationToken cancellationToken)
    {
        // ── §19.2 + §19.3  Stack Walk — Active Methods, Frame Distribution ──
        int managedFrameCount = 0;
        int unmanagedFrameCount = 0;
        int activeMethodsOnStacks = 0;
        int readyToRunFrameCount = 0;
        int dynamicMethodFrameCount = 0;
        int maxThreadFrameDepth = 0;
        uint maxThreadFrameDepthOSThreadId = 0;

        // Tiered detection: MethodDesc → set of NativeCode addresses seen on stacks.
        // MethodDesc (not MetadataToken) is the key: distinct generic instantiations of the
        // same method share a MetadataToken but get distinct MethodDescs, so keying on
        // MethodDesc avoids conflating "generic instantiated differently" with "genuinely retiered".
        var methodDescToNativeCodes = new Dictionary<ulong, HashSet<ulong>>(capacity: 1024);
        int tieredMethodCount = 0;

        // Largest-method candidates keyed by NativeCode (dedup same JIT compilation)
        var methodCandidates = new Dictionary<ulong, JitMethodEntry>(capacity: 2048);

        // Top active frame types (type name → stack-hit count)
        var frameTypeCounts = new Dictionary<string, int>(capacity: 256, StringComparer.Ordinal);

        // Top active modules (module name → stack-hit count) — keyed the same way as
        // ClrModule.Name / LoadedModuleSnapshot.Name so this can be joined against ModuleDomainResult.
        var moduleFrameCounts = new Dictionary<string, int>(capacity: 64, StringComparer.Ordinal);

        foreach (RuntimeThreadRef thread in threadQuery.EnumerateThreads())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!thread.IsAlive) continue;

            int frameIdx = 0;
            foreach (ThreadStackFrameRef frame in threadQuery.EnumerateStackFrames(thread))
            {
                frameIdx++;

                // Check cancellation every 50 frames to allow responsive cancellation during deep stack walks
                if (frameIdx % 50 == 0)
                    cancellationToken.ThrowIfCancellationRequested();

                if (frame.IsManagedMethod)
                {
                    managedFrameCount++;
                    if (!frame.HasMethod) continue;

                    activeMethodsOnStacks++;

                    // ReadyToRun (precompiled) vs JIT-compiled frame classification.
                    if (frame.IsReadyToRun)
                        readyToRunFrameCount++;

                    // Dynamic codegen detection: DynamicMethod / Reflection.Emit / expression-compiled
                    // delegates are all hosted in a dynamic module, a direct runtime signal — no need
                    // for fragile "<DynamicClass>" name pattern matching.
                    if (frame.IsDynamicModule)
                        dynamicMethodFrameCount++;

                    // Track active type hotspots
                    if (frameTypeCounts.TryGetValue(frame.DeclaringTypeName, out int prev))
                        frameTypeCounts[frame.DeclaringTypeName] = prev + 1;
                    else
                        frameTypeCounts[frame.DeclaringTypeName] = 1;

                    // Track active module hotspots (per-module JIT stack heatmap)
                    if (moduleFrameCounts.TryGetValue(frame.ModuleName, out int prevModuleCount))
                        moduleFrameCounts[frame.ModuleName] = prevModuleCount + 1;
                    else
                        moduleFrameCounts[frame.ModuleName] = 1;

                    // Tiered compilation detection: track all native codes per MethodDesc
                    ulong methodDesc = frame.MethodDesc;
                    ulong nativeCode = frame.NativeCodeAddress;

                    if (methodDesc != 0 && nativeCode != 0)
                    {
                        if (!methodDescToNativeCodes.TryGetValue(methodDesc, out HashSet<ulong>? codes))
                        {
                            codes = new HashSet<ulong>();
                            methodDescToNativeCodes[methodDesc] = codes;
                        }
                        codes.Add(nativeCode);
                    }

                    // Large method tracking (deduplicated by NativeCode address)
                    if (nativeCode != 0 && !methodCandidates.ContainsKey(nativeCode))
                    {
                        if ((ulong)frame.HotSize + frame.ColdSize >= options.LargeMethodThresholdBytes)
                        {
                            methodCandidates[nativeCode] = new JitMethodEntry(
                                frame.MethodDisplayName, frame.DeclaringTypeName, nativeCode,
                                frame.HotSize, frame.ColdSize, frame.IsReadyToRun);
                        }
                    }
                }
                else
                {
                    unmanagedFrameCount++;
                }
            }

            if (frameIdx > maxThreadFrameDepth)
            {
                maxThreadFrameDepth = frameIdx;
                maxThreadFrameDepthOSThreadId = thread.Thread.OsThreadId;
            }
        }

        // Identify tiered methods (MethodDescs with multiple distinct native codes)
        var tieredNativeCodes = new HashSet<ulong>();
        foreach (KeyValuePair<ulong, HashSet<ulong>> kvp in methodDescToNativeCodes)
        {
            if (kvp.Value.Count > 1)
            {
                tieredMethodCount++;
                foreach (ulong code in kvp.Value)
                    tieredNativeCodes.Add(code);
            }
        }

        // Distinct methods observed on stacks, keyed by MethodDesc (see tiering comment above for
        // why MethodDesc rather than MetadataToken). Reuses methodDescToNativeCodes rather than a
        // second set: its key set already is exactly "distinct methods with a resolved NativeCode".
        int distinctMethodsOnStacks = methodDescToNativeCodes.Count;

        // ── Build result lists ───────────────────────────────────────────────
        var topMethods = BuildTopMethods(methodCandidates, tieredNativeCodes);
        var topFrameTypes = BuildTopFrameTypes(frameTypeCounts);
        var topActiveModules = BuildTopFrameTypes(moduleFrameCounts);

        return new JitDomainResult(
            TotalJitHeapBytes: jitQuery.TotalJitHeapBytes,
            JitManagerCount: jitQuery.JitManagerCount,
            ActiveMethodsOnStacks: activeMethodsOnStacks,
            DistinctMethodsOnStacks: distinctMethodsOnStacks,
            TopLargestMethods: topMethods,
            TopActiveFrameTypes: topFrameTypes,
            TopActiveModulesByFrameHits: topActiveModules,
            UnmanagedFrameCount: unmanagedFrameCount,
            ManagedFrameCount: managedFrameCount,
            ReadyToRunFrameCount: readyToRunFrameCount,
            DynamicMethodFrameCount: dynamicMethodFrameCount,
            TieredMethodCount: tieredMethodCount,
            MaxThreadFrameDepth: maxThreadFrameDepth,
            MaxThreadFrameDepthOSThreadId: maxThreadFrameDepthOSThreadId,
            LargeMethodThresholdBytes: options.LargeMethodThresholdBytes);
    }

    private static IReadOnlyList<JitMethodSnapshot> BuildTopMethods(
        Dictionary<ulong, JitMethodEntry> candidates,
        HashSet<ulong> tieredNativeCodes)
    {
        if (candidates.Count == 0) return [];

        // Sort by total native code size descending.
        var entries = new JitMethodEntry[candidates.Count];
        int idx = 0;
        foreach (JitMethodEntry e in candidates.Values) entries[idx++] = e;
        Array.Sort(entries, static (a, b) =>
        {
            ulong sizeA = (ulong)a.HotSize + a.ColdSize;
            ulong sizeB = (ulong)b.HotSize + b.ColdSize;
            return sizeB.CompareTo(sizeA);
        });

        var result = new List<JitMethodSnapshot>(entries.Length);
        foreach (JitMethodEntry e in entries)
        {
            bool isTiered = tieredNativeCodes.Contains(e.NativeCodeAddress);
            result.Add(new JitMethodSnapshot(e.Signature, e.DeclaringType,
                e.NativeCodeAddress, e.HotSize, e.ColdSize, isTiered, e.IsReadyToRun));
        }
        return result;
    }

    private static IReadOnlyList<NameCountEntry> BuildTopFrameTypes(Dictionary<string, int> counts)
    {
        if (counts.Count == 0) return [];

        var pairs = new KeyValuePair<string, int>[counts.Count];
        int idx = 0;
        foreach (KeyValuePair<string, int> kv in counts) pairs[idx++] = kv;
        Array.Sort(pairs, static (a, b) => b.Value.CompareTo(a.Value));

        var result = new List<NameCountEntry>(pairs.Length);
        foreach (KeyValuePair<string, int> kv in pairs)
            result.Add(new NameCountEntry(kv.Key, kv.Value));
        return result;
    }

    // Lightweight value type to avoid per-entry heap allocations in the hot loop
    private readonly struct JitMethodEntry(
        string signature,
        string declaringType,
        ulong nativeCodeAddress,
        uint hotSize,
        uint coldSize,
        bool isReadyToRun)
    {
        public readonly string Signature = signature;
        public readonly string DeclaringType = declaringType;
        public readonly ulong NativeCodeAddress = nativeCodeAddress;
        public readonly uint HotSize = hotSize;
        public readonly uint ColdSize = coldSize;
        public readonly bool IsReadyToRun = isReadyToRun;
    }

    public void Dispose() { }
}
