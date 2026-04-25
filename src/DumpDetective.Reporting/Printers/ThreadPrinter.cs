using DumpDetective.Core.Models;
using DumpDetective.Core.Abstractions;
using DumpDetective.Core.Utilities;
using DumpDetective.Reporting.Output;
using DumpDetective.Reporting.Output;

namespace DumpDetective.Reporting.Printers
{
    internal sealed class ThreadPrinter : IAnalyzerReporter
    {
        public string AnalyzerName => "Thread Analysis";
        public string DisplayTitle => "Thread Analysis";
        public int SortOrder => 140;

        public bool CanHandle(AnalyzerDomainResult result) => result is ThreadDomainResult;

        public void Render(AnalyzerDomainResult result, IReportWriter writer)
        {
            if (result is not ThreadDomainResult domain)
                return;

            writer.WriteHeader("THREAD ANALYSIS:");
            writer.WriteSubHeading("THREAD ANALYSIS:");
            writer.WriteSeparator();
            writer.WriteMetric("Total threads", $"{domain.TotalThreadCount:N0}");
            writer.WriteMetric("Alive threads", $"{domain.AliveThreadCount:N0}");
            writer.WriteMetric("Inactive threads", $"{domain.InactiveThreadCount:N0}");
            writer.WriteMetric("GC threads", $"{domain.GcThreadCount:N0}");
            writer.WriteMetric("Finalizer threads", $"{domain.FinalizerThreadCount:N0}");
            writer.WriteMetric("Background threads", $"{domain.BackgroundThreadCount:N0}");
            writer.WriteMetric("Blocked-pattern threads", $"{domain.BlockedThreadCount:N0}");
            writer.WriteMetric("Lock-holding threads", $"{domain.LockHoldingThreadCount:N0}");
            writer.WriteMetric("Threads with active exceptions", $"{domain.ThreadsWithActiveExceptionsCount:N0}");
            writer.WriteMetric("ThreadPool worker threads (alive)", $"{domain.ThreadPoolWorkerCount:N0}");
            writer.WriteMetric("Threads with async chains (MoveNext)", $"{domain.AsyncChainThreadCount:N0}  max depth: {domain.MaxAsyncChainDepth:N0}");

            writer.WriteDetailBlank();
            writer.WriteSubHeading("WAIT CATEGORY BREAKDOWN:");
            writer.WriteSeparator();
            if (domain.WaitPatternBreakdown.Count == 0)
            {
                writer.WriteDetailText("No wait categories detected.");
            }
            else
            {
                foreach (var kvp in domain.WaitPatternBreakdown.OrderByDescending(k => k.Value))
                    writer.WriteMetric(kvp.Key, $"{kvp.Value:N0}", indentLevel: 1);
            }

            writer.WriteDetailBlank();
            writer.WriteSubHeading("THREAD GROUPS:");
            writer.WriteSeparator();
            int activeThreads = Math.Max(0, domain.AliveThreadCount - domain.BlockedThreadCount);
            int gcOrSystemThreads = domain.GcThreadCount + domain.FinalizerThreadCount;
            writer.WriteMetric("Alive/Blocked/Active/GC-System", $"{domain.AliveThreadCount:N0} | {domain.BlockedThreadCount:N0} | {activeThreads:N0} | {gcOrSystemThreads:N0}");

            double activePct = domain.AliveThreadCount == 0 ? 0 : activeThreads * 100.0 / domain.AliveThreadCount;
            writer.WriteDetailBlank();
            writer.WriteDetailText($"⚙️  Active Processing                      {activeThreads,4:N0} threads ({activePct:F1}%)");
            writer.WriteSubHeading("Top frames:", indentLevel: 2);
            var activeHotspots = domain.TopActiveThreadHotspots ?? [];
            if (activeHotspots.Count == 0)
            {
                writer.WriteDetailText("No active-processing hotspot frames available.", indentLevel: 4);
            }
            else
            {
                foreach (var hotspot in activeHotspots.Take(5))
                    writer.WriteDetailText($"{hotspot.Count,2}  {hotspot.Name}", indentLevel: 4);
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

                writer.WriteDetailBlank();
                writer.WriteDetailText($"⌛  {group.Key,-36} {groupCount,4:N0} threads ({pct:F1}%)");
                writer.WriteMetric("Pattern", reason, indentLevel: 2);
                writer.WriteDetailText(lockHolding > 0
                    ? $"    Threads also holding locks: {lockHolding:N0}  ⚠️  cross-lock / escalation risk"
                    : "    Threads also holding locks: 0");
                writer.WriteMetric("Top frame", topFrame, indentLevel: 2);
            }

            writer.WriteDetailBlank();
            writer.WriteMetric("🧵  ThreadPool Workers", $"{domain.ThreadPoolWorkerCount:N0} threads (identified by flag or dispatch frame)");

            writer.WriteDetailBlank();
            writer.WriteSubHeading("♻️  GC / System Threads");
            writer.WriteDetailText(domain.FinalizerThreadCount == 0
                ? "    Finalizer Thread: Not observed"
                : domain.FinalizerThreadBlocked
                    ? "    Finalizer Thread: Potentially blocked ⚠️"
                    : "    Finalizer Thread: Running ✅");

            writer.WriteDetailBlank();
            writer.WriteSubHeading("THREADS WITH ACTIVE EXCEPTIONS:");
            writer.WriteSeparator();
            var threadsWithExceptions = domain.ThreadsWithActiveExceptions ?? [];
            if (threadsWithExceptions.Count == 0)
            {
                writer.WriteDetailText("No active thread exceptions detected.");
            }
            else
            {
                foreach (var thread in threadsWithExceptions)
                {
                    writer.WriteDetailText($"Thread {thread.OSThreadId:N0} (Managed: {thread.ThreadId:N0}):");
                    writer.WriteMetric("Exception", thread.ExceptionType, indentLevel: 1);
                    if (!string.IsNullOrWhiteSpace(thread.ExceptionMessage))
                        writer.WriteMetric("Message", thread.ExceptionMessage, indentLevel: 1);
                    writer.WriteMetric("State", thread.ThreadState, indentLevel: 1);
                    writer.WriteMetric("GC Mode", thread.GcMode, indentLevel: 1);
                    writer.WriteMetric("Locks/Stack Roots", $"{thread.LockCount:N0} | {thread.StackRootCount:N0}", indentLevel: 1);
                    writer.WriteSubHeading("Top frames:", indentLevel: 1);
                    foreach (var frame in thread.TopFrames.Take(8))
                        writer.WriteDetailText(frame, indentLevel: 2);
                    writer.WriteDetailBlank();
                }
            }

            writer.WriteDetailBlank();
            writer.WriteSubHeading("THREADS WITH LOCKS:");
            writer.WriteSeparator();
            var lockedThreads = domain.TopLockedThreads ?? [];
            if (lockedThreads.Count == 0)
            {
                writer.WriteDetailText("No lock-holding threads detected.");
            }
            else
            {
                foreach (var thread in lockedThreads)
                {
                    writer.WriteDetailText($"Thread {thread.OSThreadId:N0} (Managed: {thread.ThreadId:N0}):");
                    writer.WriteMetric("Lock Count/Stack Roots", $"{thread.LockCount:N0} | {thread.StackRootCount:N0}", indentLevel: 1);
                    writer.WriteSubHeading("Stack Trace (top 8 frames):", indentLevel: 1);
                    foreach (var frame in thread.TopFrames.Take(8))
                        writer.WriteDetailText(frame, indentLevel: 2);
                    writer.WriteDetailBlank();
                }
            }

            writer.WriteDetailBlank();
            writer.WriteSubHeading("POTENTIALLY BLOCKED THREADS:");
            writer.WriteSeparator();
            blockedThreads = domain.TopBlockedThreads ?? [];
            if (blockedThreads.Count == 0)
            {
                writer.WriteDetailText("No blocked-thread signatures detected.");
            }
            else
            {
                foreach (var thread in blockedThreads)
                {
                    writer.WriteDetailText($"Thread {thread.OSThreadId:N0} (Managed: {thread.ThreadId:N0}):");
                    writer.WriteMetric("Category", thread.WaitCategory ?? "Unknown", indentLevel: 1);
                    writer.WriteMetric("Reason", thread.WaitReason ?? "Unknown wait pattern", indentLevel: 1);
                    writer.WriteMetric("State", thread.ThreadState, indentLevel: 1);
                    writer.WriteMetric("GC Mode", thread.GcMode, indentLevel: 1);
                    writer.WriteMetric("Locks/Stack Roots", $"{thread.LockCount:N0} | {thread.StackRootCount:N0}", indentLevel: 1);
                    writer.WriteSubHeading("Top frames:", indentLevel: 1);
                    foreach (var frame in thread.TopFrames.Take(8))
                        writer.WriteDetailText(frame, indentLevel: 2);
                    writer.WriteDetailBlank();
                }
            }

            writer.WriteDetailBlank();
            writer.WriteSubHeading("TOP STACK HOTSPOTS (TOP FRAME):");
            writer.WriteSeparator();
            foreach (var hotspot in (domain.TopStackHotspots ?? []).Take(10))
                writer.WriteMetric(hotspot.Name, $"{hotspot.Count,2}", indentLevel: 1);

            writer.WriteDetailBlank();
            writer.WriteSubHeading("THREAD STATE DISTRIBUTION:");
            writer.WriteSeparator();
            foreach (var kvp in (domain.ThreadStateDistribution ?? new Dictionary<string, int>()).OrderByDescending(k => k.Value))
                writer.WriteMetric(kvp.Key, $"{kvp.Value:N0}");

            writer.WriteDetailBlank();
            writer.WriteSubHeading("APP DOMAIN DISTRIBUTION:");
            writer.WriteSeparator();
            foreach (var kvp in (domain.AppDomainDistribution ?? new Dictionary<string, int>()).OrderByDescending(k => k.Value))
                writer.WriteMetric(kvp.Key, $"{kvp.Value:N0}");

            writer.WriteDetailBlank();
            writer.WriteSubHeading("GC MODE DISTRIBUTION:");
            writer.WriteSeparator();
            var gcModeDistribution = domain.GcModeDistribution ?? new Dictionary<string, int>();
            if (gcModeDistribution.Count == 0)
            {
                writer.WriteDetailText("No GC mode distribution available.");
            }
            else
            {
                foreach (var kvp in gcModeDistribution.OrderByDescending(k => k.Value))
                    writer.WriteMetric(kvp.Key, $"{kvp.Value:N0}");
            }

            writer.WriteDetailBlank();
            writer.WriteSubHeading("ASYNC THREAD ISSUES:");
            writer.WriteSeparator();
            if (domain.AsyncChainThreadCount == 0)
                writer.WriteDetailText("No async/await issues detected.");
            else
                writer.WriteMetric("Threads with async chains (MoveNext)", $"{domain.AsyncChainThreadCount:N0}  max depth: {domain.MaxAsyncChainDepth:N0}");

            writer.WriteDetailBlank();
            writer.WriteSubHeading("FINALIZER THREAD:");
            writer.WriteSeparator();
            if (domain.FinalizerThreadCount == 0)
            {
                writer.WriteDetailText("No finalizer thread observed.");
            }
            else
            {
                writer.WriteMetric("Finalizer thread count", $"{domain.FinalizerThreadCount:N0}");
                writer.WriteMetric("Status", domain.FinalizerThreadBlocked
                    ? "Status: ⚠️ Potentially blocked"
                    : "Status: ✅ Running");
                if (domain.FinalizerOsThreadId.HasValue && domain.FinalizerManagedThreadId.HasValue)
                    writer.WriteMetric("OS Thread/Managed", $"{domain.FinalizerOsThreadId.Value:N0}  {domain.FinalizerManagedThreadId.Value:N0}");
                writer.WriteMetric("Lock Count", $"{domain.FinalizerLockCount:N0}");
                if (domain.FinalizerFrames is { Count: > 0 })
                {
                    writer.WriteSubHeading("Stack:");
                    foreach (var frame in domain.FinalizerFrames.Take(8))
                        writer.WriteDetailText(frame, indentLevel: 1);
                }
            }

            writer.WriteDetailBlank();
            writer.WriteSubHeading("THREAD HEALTH SIGNAL:");
            writer.WriteSeparator();
            writer.WriteDetailText(domain.BlockedThreadCount >= 10 || domain.ThreadsWithActiveExceptionsCount > 0
                ? "⚠️  Thread-state triage indicates elevated hang/contention risk."
                : "✅ Thread-state profile appears stable for this snapshot.");

            writer.WriteDetailDivider();
        }
    }
}



