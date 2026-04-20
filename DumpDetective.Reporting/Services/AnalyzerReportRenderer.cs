using DumpDetective.Core.Abstractions;
using DumpDetective.Core.Models;
using DumpDetective.Core.Utilities;

namespace DumpDetective.Reporting.Services
{
    internal sealed class AnalyzerReportRenderer(IReadOnlyList<IAnalyzerReporter> reporters)
    {
        private readonly IReadOnlyList<IAnalyzerReporter> _reporters = reporters;

        public void Render(IReadOnlyDictionary<string, AnalyzerDomainResult> domainResults, IReportWriter writer)
        {
            foreach (var reporter in _reporters)
            {
                if (!domainResults.TryGetValue(reporter.AnalyzerName, out var result))
                    continue;

                if (!reporter.CanHandle(result))
                    continue;

                reporter.Render(result, writer);
            }
        }
    }
}



