using DumpDetective.Models;
using DumpDetective.Utilities;

namespace DumpDetective.Analyzers
{
    internal sealed class HangPrinter : IAnalyzerReporter
    {
        public string AnalyzerName => "Hang Analysis";

        public bool CanHandle(AnalyzerDomainResult result) => result is HangDomainResult;

        public void Render(AnalyzerDomainResult result, OutputWriter writer)
        {
            if (result is not HangDomainResult domain)
                return;

            writer.WriteHeader("HANG ANALYSIS:");
            writer.WriteLine("HANG INDICATORS:");
            writer.WriteSeparator();
            writer.WriteLine($"Total Alive Threads: {domain.TotalAliveThreads:N0}");
            writer.WriteLine($"Waiting/Blocked Threads: {domain.WaitingThreadCount:N0}");
            writer.WriteLine($"Threads Holding Locks: {domain.ThreadsHoldingLocks:N0}");
            writer.WriteLine(string.Empty);
            writer.WriteLine($"Waiting Thread Percentage: {domain.WaitingPercent:F1}%");
            writer.WriteLine(string.Empty);

            string healthState = domain.HealthScore >= 90
                ? "🟢 Healthy"
                : domain.HealthScore >= 70
                    ? "🟡 Watch"
                    : "🔴 At Risk";
            writer.WriteLine($"Thread Health Score: {domain.HealthScore}/100  {healthState}");

            if (domain.WaitingPercent >= 80)
                writer.WriteLine("\n⚠️  SEVERE HANG risk detected.");
            else if (domain.WaitingPercent >= 50)
                writer.WriteLine("\n⚠️  POSSIBLE HANG risk detected.");

            writer.WriteLine("\nHANG WAIT CATEGORY BREAKDOWN:");
            writer.WriteSeparator();
            if (domain.WaitCategoryBreakdown.Count == 0)
            {
                writer.WriteLine("No waiting categories detected.");
            }
            else
            {
                foreach (var kvp in domain.WaitCategoryBreakdown.OrderByDescending(k => k.Value))
                    writer.WriteLine($"  {kvp.Key}: {kvp.Value:N0}");
            }

            writer.WriteLine("\nTHREAD POOL STATUS:");
            writer.WriteSeparator();
            if (!domain.RuntimeThreadPoolDataAvailable)
                writer.WriteLine("Runtime ThreadPool data unavailable (dump may be from managed-only snapshot).");

            writer.WriteLine(string.Empty);
            writer.WriteLine($"Queued Work Items (heap scan): {domain.QueuedWorkItems:N0}");
            writer.WriteLine($"Total Tasks: {domain.TotalTasks:N0}");
            writer.WriteLine($"Pending Tasks: {domain.PendingTasks:N0}");
            writer.WriteLine($"Faulted Tasks: {domain.FaultedTasks:N0}");
            writer.WriteLine($"Canceled Tasks: {domain.CanceledTasks:N0}");
            if (domain.TaskScanLimited)
                writer.WriteLine("Task scan limited due to heap size; totals may be partial.");

            if (domain.QueuedWorkItems > 100)
            {
                writer.WriteLine(string.Empty);
                writer.WriteLine($"⚠️  WARNING: {domain.QueuedWorkItems:N0} queued work items!");
                writer.WriteLine("    ThreadPool may be saturated - consider increasing threads or async patterns.");
            }

            writer.WriteLine("\nWAITING THREADS BREAKDOWN:");
            writer.WriteSeparator();
            var waitingThreads = domain.TopWaitingThreads ?? [];
            if (waitingThreads.Count == 0)
            {
                writer.WriteLine("No waiting-thread details available.");
            }
            else
            {
                foreach (var wt in waitingThreads)
                {
                    writer.WriteLine($"Thread {wt.ThreadId:N0} (OS: {wt.OSThreadId:N0})");
                    writer.WriteLine($"  Category: {wt.WaitType}");
                    writer.WriteLine($"  Reason: {wt.WaitReason}");
                    writer.WriteLine($"  Locks: {wt.LockCount:N0}");
                    writer.WriteLine($"  Top frame: {wt.TopStackFrame}");
                    writer.WriteLine(string.Empty);
                }
            }

            writer.WriteLine("\nASYNC TASK ANALYSIS:");
            writer.WriteSeparator();
            writer.WriteLine($"Total Task Continuations: {domain.TotalTaskContinuations:N0}");
            var continuationTypes = domain.TopContinuationTypes ?? [];
            if (continuationTypes.Count == 0)
            {
                writer.WriteLine("No continuation-type signatures detected.");
            }
            else
            {
                foreach (var type in continuationTypes)
                    writer.WriteLine($"  {type.Name}: {type.Count:N0}");
            }

            writer.WriteLine("\nDEADLOCK DETECTION:");
            writer.WriteSeparator();
            writer.WriteLine(domain.WaitingPercent >= 80
                ? "⚠️  Severe wait saturation suggests potential deadlock/contention storm."
                : "No obvious deadlock patterns detected.\nApplication may be functioning normally or experiencing other issues.");

            writer.WriteLine(StringConstants.Equals80);
        }
    }
}
