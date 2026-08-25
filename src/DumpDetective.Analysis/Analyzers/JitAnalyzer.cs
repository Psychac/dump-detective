using DumpDetective.Core.Abstractions;
using DumpDetective.Core.Models;
using DumpDetective.Core.Options;

using Microsoft.Diagnostics.Runtime;

namespace DumpDetective.Analysis.Analyzers;

/// <summary>
/// Phase-2 analyzer covering §19.1 JIT heap usage, §19.2 compiled method analysis,
/// and §19.3 tiered compilation detection.
///
/// All data comes from:
///   - <c>ClrRuntime.EnumerateJitManagers()</c> — code heap byte totals
///   - <c>ClrRuntime.Threads</c> stack walks — active methods, frame distribution
///   - <c>ClrMethod.HotColdInfo</c>, <c>ClrMethod.NativeCode</c>, <c>ClrMethod.MethodDesc</c>,
///     <c>ClrMethod.CompilationType</c>, <c>ClrModule.IsDynamic</c>
///
/// No heap enumeration is performed — this is a purely runtime-metadata analyzer.
/// </summary>
public sealed class JitAnalyzer : IAnalyzer
{
    public string Name => "JIT Analysis";
    public string Category => "Performance";

    public ValueTask<AnalyzerDomainResult> AnalyzeAsync(
        AnalysisContext context,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        JitAnalysisOptions options = context.AnalysisOptions.JitAnalysis;
        return ValueTask.FromResult(Analyze(context.Runtime, options, cancellationToken).Stamp(this));
    }

    private static AnalyzerDomainResult Analyze(ClrRuntime runtime, JitAnalysisOptions options, CancellationToken cancellationToken)
    {
        // ── §19.1  JIT Code Heap Enumeration ────────────────────────────────
        ulong totalJitHeapBytes = 0;
        int jitManagerCount = 0;

        foreach (ClrJitManager mgr in runtime.EnumerateJitManagers())
        {
            jitManagerCount++;
            foreach (ClrNativeHeapInfo heap in mgr.EnumerateNativeHeaps())
                totalJitHeapBytes += heap.MemoryRange.Length;
        }

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
        var frameTypeCounts = new Dictionary<string, int>(
            capacity: 256, StringComparer.Ordinal);

        // Top active modules (module name → stack-hit count) — keyed the same way as
        // ClrModule.Name / LoadedModuleSnapshot.Name so this can be joined against ModuleDomainResult.
        var moduleFrameCounts = new Dictionary<string, int>(
            capacity: 64, StringComparer.Ordinal);

        IReadOnlyList<ClrThread> threads = runtime.Threads;
        for (int i = 0; i < threads.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            ClrThread thread = threads[i];
            if (!thread.IsAlive) continue;

            int frameIdx = 0;
            foreach (ClrStackFrame frame in thread.EnumerateStackTrace())
            {
                frameIdx++;

                // Check cancellation every 50 frames to allow responsive cancellation during deep stack walks
                if (frameIdx % 50 == 0)
                    cancellationToken.ThrowIfCancellationRequested();

                if (frame.Kind == ClrStackFrameKind.ManagedMethod)
                {
                    managedFrameCount++;
                    ClrMethod? method = frame.Method;
                    if (method is null) continue;

                    activeMethodsOnStacks++;

                    // ReadyToRun (precompiled) vs JIT-compiled frame classification. ClrMD reports
                    // R2R methods as MethodCompilationType.Ngen — the DAC has no dedicated R2R value,
                    // it reuses the legacy NGen classification for any precompiled-at-load-time method.
                    if (method.CompilationType == MethodCompilationType.Ngen)
                        readyToRunFrameCount++;

                    // Dynamic codegen detection: DynamicMethod / Reflection.Emit / expression-compiled
                    // delegates are all hosted in a dynamic module (ClrModule.IsDynamic), which is a
                    // direct runtime signal — no need for fragile "<DynamicClass>" name pattern matching.
                    if (method.Type?.Module?.IsDynamic == true)
                        dynamicMethodFrameCount++;

                    // Track active type hotspots
                    string typeName = method.Type?.Name ?? "Unknown";
                    if (frameTypeCounts.TryGetValue(typeName, out int prev))
                        frameTypeCounts[typeName] = prev + 1;
                    else
                        frameTypeCounts[typeName] = 1;

                    // Track active module hotspots (per-module JIT stack heatmap)
                    string moduleName = method.Type?.Module?.Name ?? "Unknown";
                    if (moduleFrameCounts.TryGetValue(moduleName, out int prevModuleCount))
                        moduleFrameCounts[moduleName] = prevModuleCount + 1;
                    else
                        moduleFrameCounts[moduleName] = 1;

                    // Tiered compilation detection: track all native codes per MethodDesc
                    ulong methodDesc = method.MethodDesc;
                    ulong nativeCode = method.NativeCode;

                    if (methodDesc != 0 && nativeCode != 0)
                    {
                        if (!methodDescToNativeCodes.TryGetValue(methodDesc, out var codes))
                        {
                            codes = new HashSet<ulong>();
                            methodDescToNativeCodes[methodDesc] = codes;
                        }
                        codes.Add(nativeCode);
                    }

                    // Large method tracking (deduplicated by NativeCode address)
                    if (nativeCode != 0 && !methodCandidates.ContainsKey(nativeCode))
                    {
                        HotColdRegions hcr = method.HotColdInfo;
                        uint hotSize = hcr.HotSize;
                        uint coldSize = hcr.ColdSize;

                        if (hotSize + coldSize >= options.LargeMethodThresholdBytes)
                        {
                            methodCandidates[nativeCode] = new JitMethodEntry(
                                method.Signature ?? typeName + "." + (method.Name ?? "?"),
                                typeName,
                                nativeCode,
                                hotSize,
                                coldSize,
                                method.CompilationType);
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
                maxThreadFrameDepthOSThreadId = thread.OSThreadId;
            }
        }

        // Identify tiered methods (MethodDescs with multiple distinct native codes)
        var tieredNativeCodes = new HashSet<ulong>();
        foreach (var kvp in methodDescToNativeCodes)
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
            TotalJitHeapBytes: totalJitHeapBytes,
            JitManagerCount: jitManagerCount,
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
            bool isReadyToRun = e.CompilationType == MethodCompilationType.Ngen;
            result.Add(new JitMethodSnapshot(e.Signature, e.DeclaringType,
                e.NativeCodeAddress, e.HotSize, e.ColdSize, isTiered, isReadyToRun));
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
        MethodCompilationType compilationType)
    {
        public readonly string Signature = signature;
        public readonly string DeclaringType = declaringType;
        public readonly ulong NativeCodeAddress = nativeCodeAddress;
        public readonly uint HotSize = hotSize;
        public readonly uint ColdSize = coldSize;
        public readonly MethodCompilationType CompilationType = compilationType;
    }

    public void Dispose() { }
}
