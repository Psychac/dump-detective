using DumpDetective.Models;
using DumpDetective.Utilities;

namespace DumpDetective.Analyzers
{
    internal sealed class GCHandlePrinter : IAnalyzerReporter
    {
        public string AnalyzerName => "GC Handle Analysis";

        public bool CanHandle(AnalyzerDomainResult result) => result is GCHandleDomainResult;

        public void Render(AnalyzerDomainResult result, OutputWriter writer)
        {
            if (result is not GCHandleDomainResult domain)
                return;

            writer.WriteHeader("GC HANDLE ANALYSIS:");
            writer.WriteLine("HANDLE SUMMARY:");
            writer.WriteSeparator();
            writer.WriteLine($"Total handles: {domain.TotalHandles:N0}");
            writer.WriteLine($"Strong-like handles: {domain.StrongLikeHandles:N0}");
            writer.WriteLine($"Weak-like handles: {domain.WeakLikeHandles:N0}");

            writer.WriteLine("\nHANDLES BY KIND:");
            writer.WriteSeparator();
            var byKind = domain.HandlesByKind ?? [];
            if (byKind.Count == 0)
            {
                writer.WriteLine("No handle-kind distribution available.");
            }
            else
            {
                foreach (var entry in byKind)
                    writer.WriteLine($"  • {FormatHelper.TruncateString(entry.Name, 50)}: {entry.Count:N0}");
            }

            writer.WriteLine("\nTOP TYPES REFERENCED BY HANDLES:");
            writer.WriteSeparator();
            var topTargets = domain.TopTargetTypes ?? [];
            if (topTargets.Count == 0)
            {
                writer.WriteLine("No resolved handle target types available.");
            }
            else
            {
                foreach (var entry in topTargets)
                    writer.WriteLine($"  • {FormatHelper.TruncateString(entry.Name, 70)}: {entry.Count:N0}");
            }

            writer.WriteLine("\nTOP TYPES REFERENCED BY PINNED HANDLES:");
            writer.WriteSeparator();
            var topPinned = domain.TopPinnedTargetTypes ?? [];
            if (topPinned.Count == 0)
            {
                writer.WriteLine("No pinned-handle target type details available.");
            }
            else
            {
                foreach (var entry in topPinned)
                    writer.WriteLine($"  • {FormatHelper.TruncateString(entry.Name, 70)}: {entry.Count:N0}");
            }

            writer.WriteLine("\nHANDLE PRESSURE SIGNAL:");
            writer.WriteSeparator();
            writer.WriteLine($"Pinned handle targets: {domain.PinnedHandleTargets:N0}");

            writer.WriteLine(domain.TotalHandles >= 10_000 || domain.PinnedHandleTargets >= 1_000
                ? "⚠️  Elevated handle pressure detected."
                : "✅ Handle pressure appears within expected range.");
            writer.WriteLine(StringConstants.Equals80);
        }
    }
}
