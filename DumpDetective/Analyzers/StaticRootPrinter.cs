using DumpDetective.Models;
using DumpDetective.Utilities;

namespace DumpDetective.Analyzers
{
    internal sealed class StaticRootPrinter : IAnalyzerReporter
    {
        public string AnalyzerName => "Static Root Leak Detection";

        public bool CanHandle(AnalyzerDomainResult result) => result is StaticRootDomainResult;

        public void Render(AnalyzerDomainResult result, OutputWriter writer)
        {
            if (result is not StaticRootDomainResult domain)
                return;

            writer.WriteHeader("STATIC ROOT LEAK DETECTION:");
            writer.WriteLine("STATIC ROOT LEAK DETECTION:");
            writer.WriteSeparator();
            writer.WriteLine($"Concerning static roots: {domain.RootCount:N0}");
            writer.WriteLine($"Total retained bytes: {FormatHelper.FormatBytes(domain.TotalRetainedBytes)}");

            writer.WriteLine("\nTOP TYPES KEPT ALIVE:");
            writer.WriteSeparator();
            var roots = domain.TopRootsByRetainedBytes ?? [];
            if (roots.Count == 0)
            {
                writer.WriteLine("No root-level retained-byte breakdown available.");
            }
            else
            {
                foreach (var root in roots.Take(8))
                    writer.WriteLine($"  • {FormatHelper.TruncateString(root.Name, 90)}: {FormatHelper.FormatBytes(root.Bytes)} retained");
            }

            writer.WriteLine("\nRETENTION PRESSURE SIGNAL:");
            writer.WriteSeparator();
            writer.WriteLine(domain.RootCount >= 10
                ? "⚠️  High static-root pressure detected; review long-lived static ownership."
                : "ℹ️  Static-root pressure appears moderate in this dump.");
            writer.WriteLine(StringConstants.Equals80);
        }
    }
}
