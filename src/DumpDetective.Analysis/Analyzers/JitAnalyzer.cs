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
///   - <c>ClrMethod.HotColdInfo</c>, <c>ClrMethod.NativeCode</c>, <c>ClrMethod.MetadataToken</c>
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

        // Tiered detection: token → set of NativeCode addresses seen on stacks
        var tokenToNativeCodes = new Dictionary<int, HashSet<ulong>>(capacity: 1024);
        int tieredMethodCount = 0;

        // Largest-method candidates keyed by NativeCode (dedup same JIT compilation)
        var methodCandidates = new Dictionary<ulong, JitMethodEntry>(capacity: 2048);

        // Top active frame types (type name → stack-hit count)
        var frameTypeCounts = new Dictionary<string, int>(
            capacity: 256, StringComparer.Ordinal);

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

                    // Track active type hotspots
                    string typeName = method.Type?.Name ?? "Unknown";
                    if (frameTypeCounts.TryGetValue(typeName, out int prev))
                        frameTypeCounts[typeName] = prev + 1;
                    else
                        frameTypeCounts[typeName] = 1;

                    // Tiered compilation detection: track all native codes per token
                    int token = method.MetadataToken;
                    ulong nativeCode = method.NativeCode;

                    if (token != 0 && nativeCode != 0)
                    {
                        if (!tokenToNativeCodes.TryGetValue(token, out var codes))
                        {
                            codes = new HashSet<ulong>();
                            tokenToNativeCodes[token] = codes;
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
                                coldSize);
                        }
                    }
                }
                else
                {
                    unmanagedFrameCount++;
                }
            }
        }

        // Identify tiered tokens (tokens with multiple distinct native codes)
        var tieredNativeCodes = new HashSet<ulong>();
        foreach (var kvp in tokenToNativeCodes)
        {
            if (kvp.Value.Count > 1)
            {
                tieredMethodCount++;
                foreach (ulong code in kvp.Value)
                    tieredNativeCodes.Add(code);
            }
        }

        // ── Build result lists ───────────────────────────────────────────────
        var topMethods = BuildTopMethods(methodCandidates, tieredNativeCodes);
        var topFrameTypes = BuildTopFrameTypes(frameTypeCounts);

        return new JitDomainResult(
            TotalJitHeapBytes: totalJitHeapBytes,
            JitManagerCount: jitManagerCount,
            ActiveMethodsOnStacks: activeMethodsOnStacks,
            TopLargestMethods: topMethods,
            TopActiveFrameTypes: topFrameTypes,
            UnmanagedFrameCount: unmanagedFrameCount,
            ManagedFrameCount: managedFrameCount,
            TieredMethodCount: tieredMethodCount,
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
                e.NativeCodeAddress, e.HotSize, e.ColdSize, isTiered));
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
        uint coldSize)
    {
        public readonly string Signature = signature;
        public readonly string DeclaringType = declaringType;
        public readonly ulong NativeCodeAddress = nativeCodeAddress;
        public readonly uint HotSize = hotSize;
        public readonly uint ColdSize = coldSize;
    }

    public void Dispose() { }
}
