using DumpDetective.Models;
using DumpDetective.Utilities;

namespace DumpDetective.Analyzers
{
    internal sealed class ThreadPrinter : IAnalyzerReporter
    {
        public string AnalyzerName => "Thread Analysis";

        public bool CanHandle(AnalyzerDomainResult result) => result is ThreadDomainResult;

        public void Render(AnalyzerDomainResult result, OutputWriter writer)
        {
            if (result is not ThreadDomainResult domain)
                return;

            writer.WriteHeader("THREAD ANALYSIS:");
            writer.WriteLine("THREAD TRIAGE SUMMARY:");
            writer.WriteSeparator();
            writer.WriteLine($"Alive threads: {domain.AliveThreadCount:N0}");
            writer.WriteLine($"Blocked-pattern threads: {domain.BlockedThreadCount:N0}");
            writer.WriteLine($"Lock-holding threads: {domain.LockHoldingThreadCount:N0}");
            writer.WriteLine($"Threads with active exceptions: {domain.ThreadsWithActiveExceptionsCount:N0}");
            writer.WriteLine($"ThreadPool worker threads (alive): {domain.ThreadPoolWorkerCount:N0}");

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
            var blockedThreads = domain.TopBlockedThreads ?? [];
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
            foreach (var kvp in (domain.GcModeDistribution ?? new Dictionary<string, int>()).OrderByDescending(k => k.Value))
                writer.WriteLine($"{kvp.Key}: {kvp.Value:N0}");

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
                    ? "Status: ⚠️ Potentially blocked"
                    : "Status: ✅ Running");
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
                ? "⚠️  Thread-state triage indicates elevated hang/contention risk."
                : "✅ Thread-state profile appears stable for this snapshot.");

            writer.WriteLine(StringConstants.Equals80);
        }
    }
}
