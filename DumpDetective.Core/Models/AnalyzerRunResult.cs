using System;
using System.Collections.Generic;
using System.Text;

namespace DumpDetective.Core.Models;
internal sealed record AnalyzerRunResult(string DetailedReport, AnalysisSnapshot Snapshot);
