using DumpDetective.Core.Models;

using System;
using System.Collections.Generic;
using System.Text;

namespace DumpDetective.Core.Abstractions;
internal interface IAnalyzerTrendComparer
{
    string AnalyzerName { get; }
    IReadOnlyList<AnalyzerMetric> ExtractMetrics(AnalyzerDomainResult result);
    IReadOnlyList<MetricDelta> Compare(AnalyzerDomainResult baseline, AnalyzerDomainResult current);
}
