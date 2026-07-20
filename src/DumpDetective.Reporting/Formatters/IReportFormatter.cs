using DumpDetective.Core.Configuration;
using DumpDetective.Reporting.Models;

namespace DumpDetective.Reporting.Formatters;

internal interface IReportFormatter
{
    ReportFormat Format { get; }
    string Render(AnalysisReportDocument doc);
}
