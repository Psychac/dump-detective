using System.Collections.Generic;
using Microsoft.Diagnostics.Runtime;

namespace DumpDetective.Analysis.Analyzers
{
    // Internal helper types used by CrashAnalyzer and unit tests.
    internal class ExceptionAnalysis
    {
        public int TotalExceptions { get; set; }
        public int ActiveExceptions { get; set; }
        public Dictionary<string, int> ExceptionTypeCounts { get; set; } = new();
        public Dictionary<string, int> ActiveExceptionTypeCounts { get; set; } = new();
        public Dictionary<string, List<ExceptionInstance>> ExceptionsByType { get; set; } = new();
        public List<CrashThreadCandidate> CrashThreadCandidates { get; set; } = new();
        // GC generation distribution per exception type
        public Dictionary<string, int> ExceptionGen0Counts { get; set; } = new();
        public Dictionary<string, int> ExceptionGen1Counts { get; set; } = new();
        public Dictionary<string, int> ExceptionGen2Counts { get; set; } = new();
        public Dictionary<string, int> ExceptionLohCounts { get; set; } = new();
        // Set by BuildCrashThreadSnapshots after inference pass
        public int InferredTraceCount { get; set; }
    }

    internal class CrashThreadCandidate
    {
        public uint ThreadId { get; set; }
        public uint OSThreadId { get; set; }
        public int ActiveExceptionCount { get; set; }
        public string PrimaryExceptionType { get; set; } = string.Empty;
        public List<ClrStackFrame> CurrentThreadStack { get; set; } = new();
        public List<string> OriginalExceptionStack { get; set; } = new();
        // Representative metadata from the active exception (for inference heuristics)
        public string SampleMessage { get; set; } = string.Empty;
        public int SampleHResult { get; set; }
        public string? SampleInnerExceptionType { get; set; }
    }

    internal class ActiveExceptionContext
    {
        public uint ThreadId { get; set; }
        public uint OSThreadId { get; set; }
        public List<ClrStackFrame> CurrentThreadStack { get; set; } = new();
    }

    internal class ExceptionInstance
    {
        public ulong Address { get; set; }
        public string Type { get; set; } = string.Empty;
        public string Message { get; set; } = string.Empty;
        public int HResult { get; set; }
        public string? InnerExceptionType { get; set; }
        public int ChainDepth { get; set; } = 1;
        public uint? ThreadId { get; set; }
        public uint? OSThreadId { get; set; }
        public List<string> OriginalStackTrace { get; set; } = new();
        public List<ClrStackFrame> CurrentThreadStack { get; set; } = new();
    }
}
