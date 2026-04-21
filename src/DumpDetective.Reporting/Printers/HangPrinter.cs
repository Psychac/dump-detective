using DumpDetective.Core.Models;
using DumpDetective.Core.Abstractions;
using DumpDetective.Core.Utilities;
using DumpDetective.Reporting.Output;
using DumpDetective.Reporting.Output;

namespace DumpDetective.Reporting.Printers
{
    internal sealed class HangPrinter : IAnalyzerReporter
    {
        public string AnalyzerName => "Hang Analysis";

        public bool CanHandle(AnalyzerDomainResult result) => result is HangDomainResult;

        public void Render(AnalyzerDomainResult result, IReportWriter writer)
        {
            if (result is not HangDomainResult domain)
                return;

            writer.WriteHeader("HANG ANALYSIS:");
            writer.WriteSubHeading("HANG INDICATORS:");
            writer.WriteSeparator();
            writer.WriteMetric("Total Alive Threads", $"{domain.TotalAliveThreads:N0}");
            writer.WriteMetric("Waiting/Blocked Threads", $"{domain.WaitingThreadCount:N0}");
            writer.WriteMetric("Threads Holding Locks", $"{domain.ThreadsHoldingLocks:N0}");
            writer.WriteDetailBlank();
            writer.WriteMetric("Waiting Thread Percentage", $"{domain.WaitingPercent:F1}%");
            writer.WriteDetailBlank();

            string healthState = domain.HealthScore >= 90
                ? "🟢 Healthy"
                : domain.HealthScore >= 70
                    ? "🟡 Watch"
                    : "🔴 At Risk";
            writer.WriteMetric("Thread Health Score", $"{domain.HealthScore}/100  {healthState}");

            if (domain.WaitingPercent >= 80)
                writer.WriteDetailText("⚠️  SEVERE HANG risk detected.");
            else if (domain.WaitingPercent >= 50)
                writer.WriteDetailText("⚠️  POSSIBLE HANG risk detected.");

            writer.WriteDetailBlank();
            writer.WriteSubHeading("HANG WAIT CATEGORY BREAKDOWN:");
            writer.WriteSeparator();
            if (domain.WaitCategoryBreakdown.Count == 0)
            {
                writer.WriteLine("No waiting categories detected.");
            }
            else
            {
                foreach (var kvp in domain.WaitCategoryBreakdown.OrderByDescending(k => k.Value))
                    writer.WriteMetric(kvp.Key, $"{kvp.Value:N0}", indentLevel: 1);
            }

            writer.WriteDetailBlank();
            writer.WriteSubHeading("THREAD POOL STATUS:");
            writer.WriteSeparator();
            if (!domain.RuntimeThreadPoolDataAvailable)
                writer.WriteDetailText("Runtime ThreadPool data unavailable (dump may be from managed-only snapshot).");

            writer.WriteDetailBlank();
            writer.WriteMetric("Queued Work Items (heap scan)", $"{domain.QueuedWorkItems:N0}");
            writer.WriteMetric("Total Tasks", $"{domain.TotalTasks:N0}");
            writer.WriteMetric("Pending Tasks", $"{domain.PendingTasks:N0}");
            writer.WriteMetric("Faulted Tasks", $"{domain.FaultedTasks:N0}");
            writer.WriteMetric("Canceled Tasks", $"{domain.CanceledTasks:N0}");
            if (domain.TaskScanLimited)
                writer.WriteDetailText("Task scan limited due to heap size; totals may be partial.");

            if (domain.QueuedWorkItems > 100)
            {
                writer.WriteDetailBlank();
                writer.WriteDetailText($"⚠️  WARNING: {domain.QueuedWorkItems:N0} queued work items!");
                writer.WriteDetailText("ThreadPool may be saturated - consider increasing threads or async patterns.", indentLevel: 2);
            }

            writer.WriteDetailBlank();
            writer.WriteSubHeading("WAITING THREADS BREAKDOWN:");
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
                    writer.WriteDetailText($"Thread {wt.ThreadId:N0} (OS: {wt.OSThreadId:N0})");
                    writer.WriteMetric("Category", wt.WaitType, indentLevel: 1);
                    writer.WriteMetric("Reason", wt.WaitReason, indentLevel: 1);
                    writer.WriteMetric("Locks", $"{wt.LockCount:N0}", indentLevel: 1);
                    writer.WriteMetric("Top frame", wt.TopStackFrame, indentLevel: 1);
                    writer.WriteDetailBlank();
                }
            }

            writer.WriteDetailBlank();
            writer.WriteSubHeading("ASYNC TASK ANALYSIS:");
            writer.WriteSeparator();
            writer.WriteMetric("Total Task Continuations", $"{domain.TotalTaskContinuations:N0}");
            var continuationTypes = domain.TopContinuationTypes ?? [];
            if (continuationTypes.Count == 0)
            {
                writer.WriteLine("No continuation-type signatures detected.");
            }
            else
            {
                foreach (var type in continuationTypes)
                    writer.WriteMetric(type.Name, $"{type.Count:N0}", indentLevel: 1);
            }

            writer.WriteDetailBlank();
            writer.WriteSubHeading("DEADLOCK DETECTION:");
            writer.WriteSeparator();
            writer.WriteDetailText(domain.WaitingPercent >= 80
                ? "⚠️  Severe wait saturation suggests potential deadlock/contention storm."
                : "No obvious deadlock patterns detected.\nApplication may be functioning normally or experiencing other issues.");

            writer.WriteDetailDivider();
        }
    }
}



