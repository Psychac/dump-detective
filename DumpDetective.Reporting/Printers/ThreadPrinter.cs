using System.IO;
using DumpDetective.Core.Models;
using DumpDetective.Core.Utilities;

namespace DumpDetective.Reporting.Printers
{
    internal sealed class ThreadPrinter : IAnalyzerReporter
    {
        public string AnalyzerName => "Thread Analysis";

        public bool CanHandle(AnalyzerDomainResult result) => result is ThreadDomainResult;

        public void Render(AnalyzerDomainResult result, TextWriter writer)
        {
            if (result is not ThreadDomainResult domain)
                return;

            writer.WriteHeader("THREAD ANALYSIS:");
            writer.WriteLine("THREAD ANALYSIS:");
            writer.WriteSeparator();
            writer.WriteLine($"Total threads: {domain.TotalThreadCount:N0}");
            writer.WriteLine($"Alive threads: {domain.AliveThreadCount:N0}");
            writer.WriteLine($"Inactive threads: {domain.InactiveThreadCount:N0}");
            writer.WriteLine($"GC threads: {domain.GcThreadCount:N0}");
            writer.WriteLine($"Finalizer threads: {domain.FinalizerThreadCount:N0}");
            writer.WriteLine($"Background threads: {domain.BackgroundThreadCount:N0}");
            writer.WriteLine($"Blocked-pattern threads: {domain.BlockedThreadCount:N0}");
            writer.WriteLine($"Lock-holding threads: {domain.LockHoldingThreadCount:N0}");
            writer.WriteLine($"Threads with active exceptions: {domain.ThreadsWithActiveExceptionsCount:N0}");
            writer.WriteLine($"ThreadPool worker threads (alive): {domain.ThreadPoolWorkerCount:N0}");
            writer.WriteLine($"Threads with async chains (MoveNext): {domain.AsyncChainThreadCount:N0}  max depth: {domain.MaxAsyncChainDepth:N0}");

            writer.WriteLine("\nWAIT CATEGORY BREAKDOWN:");
            writer.WriteSeparator();
            if (domain.WaitPatternBreakdown.Count == 0)
            {
                writer.WriteLine("No wait categories detected.");
            }
            else
            {
                foreach (var kvp in domain.WaitPatternBreakdown.OrderByDescending(k => k.Value))
                    writer.WriteLine($"  {kvp.Key}: {kvp.Value:N0}");
            }

            writer.WriteLine("\nTHREAD GROUPS:");
            writer.WriteSeparator();
            int activeThreads = Math.Max(0, domain.AliveThreadCount - domain.BlockedThreadCount);
            int gcOrSystemThreads = domain.GcThreadCount + domain.FinalizerThreadCount;
            writer.WriteLine($"Alive: {domain.AliveThreadCount:N0}  |  Blocked/Waiting: {domain.BlockedThreadCount:N0}  |  Active: {activeThreads:N0}  |  GC/System: {gcOrSystemThreads:N0}");

            double activePct = domain.AliveThreadCount == 0 ? 0 : activeThreads * 100.0 / domain.AliveThreadCount;
            writer.WriteLine(string.Empty);
            writer.WriteLine($"âš™ï¸  Active Processing                      {activeThreads,4:N0} threads ({activePct:F1}%)");
            writer.WriteLine("    Top frames:");
            var activeHotspots = domain.TopActiveThreadHotspots ?? [];
            if (activeHotspots.Count == 0)
            {
                writer.WriteLine("        No active-processing hotspot frames available.");
            }
            else
            {
                foreach (var hotspot in activeHotspots.Take(5))
                    writer.WriteLine($"        {hotspot.Count,2}  {hotspot.Name}");
            }

            var blockedThreads = domain.TopBlockedThreads ?? [];
            foreach (var group in blockedThreads
                .GroupBy(t => t.WaitCategory ?? "Unknown")
                .OrderByDescending(g => g.Count()))
            {
                int groupCount = group.Count();
                double pct = domain.AliveThreadCount == 0 ? 0 : groupCount * 100.0 / domain.AliveThreadCount;
                int lockHolding = group.Count(t => t.LockCount > 0);
                string reason = group.Select(t => t.WaitReason).FirstOrDefault(r => !string.IsNullOrWhiteSpace(r)) ?? "Wait-pattern details unavailable.";
                string topFrame = group.SelectMany(t => t.TopFrames).FirstOrDefault(f => !string.IsNullOrWhiteSpace(f)) ?? "<unknown>";

                writer.WriteLine(string.Empty);
                writer.WriteLine($"âŒ›  {group.Key,-36} {groupCount,4:N0} threads ({pct:F1}%)");
                writer.WriteLine($"    Pattern: {reason}");
                writer.WriteLine(lockHolding > 0
                    ? $"    Threads also holding locks: {lockHolding:N0}  âš ï¸  cross-lock / escalation risk"
                    : "    Threads also holding locks: 0");
                writer.WriteLine($"    Top frame: {topFrame}");
            }

            writer.WriteLine(string.Empty);
            writer.WriteLine($"ðŸ§µ  ThreadPool Workers                     {domain.ThreadPoolWorkerCount:N0} threads (identified by flag or dispatch frame)");

            writer.WriteLine(string.Empty);
            writer.WriteLine("â™»ï¸  GC / System Threads");
            writer.WriteLine(domain.FinalizerThreadCount == 0
                ? "    Finalizer Thread: Not observed"
                : domain.FinalizerThreadBlocked
                    ? "    Finalizer Thread: Potentially blocked âš ï¸"
                    : "    Finalizer Thread: Running âœ…");

            writer.WriteLine("\nTHREADS WITH ACTIVE EXCEPTIONS:");
            writer.WriteSeparator();
            var threadsWithExceptions = domain.ThreadsWithActiveExceptions ?? [];
            if (threadsWithExceptions.Count == 0)
            {
                writer.WriteLine("No active thread exceptions detected.");
            }
            else
            {
                foreach (var thread in threadsWithExceptions)
                {
                    writer.WriteLine($"Thread {thread.OSThreadId:N0} (Managed: {thread.ThreadId:N0}):");
                    writer.WriteLine($"  Exception: {thread.ExceptionType}");
                    if (!string.IsNullOrWhiteSpace(thread.ExceptionMessage))
                        writer.WriteLine($"  Message: {thread.ExceptionMessage}");
                    writer.WriteLine($"  State: {thread.ThreadState}");
                    writer.WriteLine($"  GC Mode: {thread.GcMode}");
                    writer.WriteLine($"  Locks: {thread.LockCount:N0} | Stack Roots: {thread.StackRootCount:N0}");
                    writer.WriteLine("  Top frames:");
                    foreach (var frame in thread.TopFrames.Take(8))
                        writer.WriteLine($"    {frame}");
                    writer.WriteLine(string.Empty);
                }
            }

            writer.WriteLine("\nTHREADS WITH LOCKS:");
            writer.WriteSeparator();
            var lockedThreads = domain.TopLockedThreads ?? [];
            if (lockedThreads.Count == 0)
            {
                writer.WriteLine("No lock-holding threads detected.");
            }
            else
            {
                foreach (var thread in lockedThreads)
                {
                    writer.WriteLine($"Thread {thread.OSThreadId:N0} (Managed: {thread.ThreadId:N0}):");
                    writer.WriteLine($"  Lock Count: {thread.LockCount:N0} | Stack Roots: {thread.StackRootCount:N0}");
                    writer.WriteLine("  Stack Trace (top 8 frames):");
                    foreach (var frame in thread.TopFrames.Take(8))
                        writer.WriteLine($"    {frame}");
                    writer.WriteLine(string.Empty);
                }
            }

            writer.WriteLine("\nPOTENTIALLY BLOCKED THREADS:");
            writer.WriteSeparator();
            blockedThreads = domain.TopBlockedThreads ?? [];
            if (blockedThreads.Count == 0)
            {
                writer.WriteLine("No blocked-thread signatures detected.");
            }
            else
            {
                foreach (var thread in blockedThreads)
                {
                    writer.WriteLine($"Thread {thread.OSThreadId:N0} (Managed: {thread.ThreadId:N0}):");
                    writer.WriteLine($"  Category: {thread.WaitCategory ?? "Unknown"}");
                    writer.WriteLine($"  Reason: {thread.WaitReason ?? "Unknown wait pattern"}");
                    writer.WriteLine($"  State: {thread.ThreadState}");
                    writer.WriteLine($"  GC Mode: {thread.GcMode}");
                    writer.WriteLine($"  Locks: {thread.LockCount:N0} | Stack Roots: {thread.StackRootCount:N0}");
                    writer.WriteLine("  Top frames:");
                    foreach (var frame in thread.TopFrames.Take(8))
                        writer.WriteLine($"    {frame}");
                    writer.WriteLine(string.Empty);
                }
            }

            writer.WriteLine("\nTOP STACK HOTSPOTS (TOP FRAME):");
            writer.WriteSeparator();
            foreach (var hotspot in (domain.TopStackHotspots ?? []).Take(10))
                writer.WriteLine($"  {hotspot.Count,2}  {hotspot.Name}");

            writer.WriteLine("\nTHREAD STATE DISTRIBUTION:");
            writer.WriteSeparator();
            foreach (var kvp in (domain.ThreadStateDistribution ?? new Dictionary<string, int>()).OrderByDescending(k => k.Value))
                writer.WriteLine($"{kvp.Key}: {kvp.Value:N0}");

            writer.WriteLine("\nAPP DOMAIN DISTRIBUTION:");
            writer.WriteSeparator();
            foreach (var kvp in (domain.AppDomainDistribution ?? new Dictionary<string, int>()).OrderByDescending(k => k.Value))
                writer.WriteLine($"{kvp.Key}: {kvp.Value:N0}");

            writer.WriteLine("\nGC MODE DISTRIBUTION:");
            writer.WriteSeparator();
            var gcModeDistribution = domain.GcModeDistribution ?? new Dictionary<string, int>();
            if (gcModeDistribution.Count == 0)
            {
                writer.WriteLine("No GC mode distribution available.");
            }
            else
            {
                foreach (var kvp in gcModeDistribution.OrderByDescending(k => k.Value))
                    writer.WriteLine($"{kvp.Key}: {kvp.Value:N0}");
            }

            writer.WriteLine("\nASYNC THREAD ISSUES:");
            writer.WriteSeparator();
            if (domain.AsyncChainThreadCount == 0)
                writer.WriteLine("No async/await issues detected.");
            else
                writer.WriteLine($"Threads with async chains (MoveNext): {domain.AsyncChainThreadCount:N0}  max depth: {domain.MaxAsyncChainDepth:N0}");

            writer.WriteLine("\nFINALIZER THREAD:");
            writer.WriteSeparator();
            if (domain.FinalizerThreadCount == 0)
            {
                writer.WriteLine("No finalizer thread observed.");
            }
            else
            {
                writer.WriteLine($"Finalizer thread count: {domain.FinalizerThreadCount:N0}");
                writer.WriteLine(domain.FinalizerThreadBlocked
                    ? "Status: âš ï¸ Potentially blocked"
                    : "Status: âœ… Running");
                if (domain.FinalizerOsThreadId.HasValue && domain.FinalizerManagedThreadId.HasValue)
                    writer.WriteLine($"OS Thread: {domain.FinalizerOsThreadId.Value:N0}  Managed: {domain.FinalizerManagedThreadId.Value:N0}");
                writer.WriteLine($"Lock Count: {domain.FinalizerLockCount:N0}");
                if (domain.FinalizerFrames is { Count: > 0 })
                {
                    writer.WriteLine("Stack:");
                    foreach (var frame in domain.FinalizerFrames.Take(8))
                        writer.WriteLine($"  {frame}");
                }
            }

            writer.WriteLine("\nTHREAD HEALTH SIGNAL:");
            writer.WriteSeparator();
            writer.WriteLine(domain.BlockedThreadCount >= 10 || domain.ThreadsWithActiveExceptionsCount > 0
                ? "âš ï¸  Thread-state triage indicates elevated hang/contention risk."
                : "âœ… Thread-state profile appears stable for this snapshot.");

            writer.WriteLine(StringConstants.Equals80);
        }
    }
}



