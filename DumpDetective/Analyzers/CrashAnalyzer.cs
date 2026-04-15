using Microsoft.Diagnostics.Runtime;
using DumpDetective.Models;
using DumpDetective.Utilities;

namespace DumpDetective.Analyzers
{
    internal class CrashAnalyzer : IAnalyzer
    {
        private const int MaxExceptionsPerType = 10;
        private const int TopExceptionTypesCount = 10;
        private const int MaxDetailedExceptionsPerType = 5;
        private const int MaxOriginalStackFramesToPrint = 20;
        private const int MaxCurrentThreadFramesToPrint = 5;
        private const int TopCrashThreadCandidates = 5;
        private const int TopDetailedExceptionInstances = 25;

        public string Name => "Crash Analysis";

        public AnalyzerExecutionResult Execute(AnalysisContext context) => Analyze(context.Runtime, context.Heap);

        public AnalyzerExecutionResult Analyze(ClrRuntime runtime, ClrHeap heap)
        {
            var exceptionInfo = AnalyzeExceptions(heap, runtime);

            var domainResult = new CrashDomainResult(
                exceptionInfo.TotalExceptions,
                exceptionInfo.ActiveExceptions,
                new Dictionary<string, int>(exceptionInfo.ExceptionTypeCounts),
                new Dictionary<string, int>(exceptionInfo.ActiveExceptionTypeCounts),
                BuildCrashThreadSnapshots(exceptionInfo),
                BuildExceptionInstanceSnapshots(exceptionInfo));

            if (exceptionInfo.TotalExceptions == 0)
            {
                return new AnalyzerExecutionResult(
                    [new InsightFinding(
                        Analyzer: nameof(CrashAnalyzer),
                        Category: "Stability",
                        Severity: FindingSeverity.Info,
                        Title: "No exception objects detected",
                        Evidence: "Crash analysis found no exception objects in the heap snapshot.",
                        Recommendation: "Validate dump type and capture settings if a crash was expected.",
                        Tags: ["crash", "exception", "stability"],
                        MetricValue: 0,
                        MetricUnit: "active-exceptions")],
                    domainResult);
            }

            return new AnalyzerExecutionResult([CreateFinding(exceptionInfo)], domainResult);
        }

        private static IReadOnlyList<CrashThreadCandidateSnapshot> BuildCrashThreadSnapshots(ExceptionAnalysis analysis)
        {
            return analysis.CrashThreadCandidates
                .Take(TopCrashThreadCandidates)
                .Select(c => new CrashThreadCandidateSnapshot(
                    c.ThreadId,
                    c.OSThreadId,
                    c.ActiveExceptionCount,
                    c.PrimaryExceptionType,
                    c.CurrentThreadStack
                        .Take(MaxCurrentThreadFramesToPrint)
                        .Select(f => f.Method?.Signature ?? f.FrameName ?? f.ToString() ?? StringConstants.UnknownType)
                        .ToList()))
                .ToList();
        }

        private static IReadOnlyList<ExceptionInstanceSnapshot> BuildExceptionInstanceSnapshots(ExceptionAnalysis analysis)
        {
            var instances = analysis.ExceptionsByType
                .SelectMany(kvp => kvp.Value.Select(v => new { Type = kvp.Key, Instance = v }))
                .OrderByDescending(x => x.Instance.ThreadId.HasValue)
                .ThenByDescending(x => x.Instance.OriginalStackTrace.Count)
                .ThenByDescending(x => !string.IsNullOrWhiteSpace(x.Instance.Message))
                .Take(TopDetailedExceptionInstances)
                .Select(x => new ExceptionInstanceSnapshot(
                    x.Type,
                    x.Instance.Address,
                    string.IsNullOrWhiteSpace(x.Instance.Message) ? null : x.Instance.Message,
                    x.Instance.HResult == 0 ? null : x.Instance.HResult,
                    x.Instance.InnerExceptionType,
                    x.Instance.ThreadId.HasValue,
                    x.Instance.ThreadId,
                    x.Instance.OSThreadId,
                    x.Instance.CurrentThreadStack.Count == 0
                        ? null
                        : x.Instance.CurrentThreadStack
                            .Take(MaxCurrentThreadFramesToPrint)
                            .Select(f => f.Method?.Signature ?? f.FrameName ?? f.ToString() ?? StringConstants.UnknownType)
                            .ToList(),
                    x.Instance.OriginalStackTrace.Count == 0
                        ? null
                        : x.Instance.OriginalStackTrace.Take(MaxOriginalStackFramesToPrint).ToList()))
                .ToList();

            return instances;
        }

        private static InsightFinding CreateFinding(ExceptionAnalysis analysis)
        {
            FindingSeverity severity = analysis.ActiveExceptions > 0
                ? FindingSeverity.Critical
                : analysis.TotalExceptions > 0
                    ? FindingSeverity.Warning
                    : FindingSeverity.Info;

            return new InsightFinding(
                Analyzer: nameof(CrashAnalyzer),
                Category: "Stability",
                Severity: severity,
                Title: "Exception pressure in crash dump",
                Evidence: $"Total exceptions: {analysis.TotalExceptions:N0}; active thread exceptions: {analysis.ActiveExceptions:N0}; unique types: {analysis.ExceptionTypeCounts.Count:N0}.",
                Recommendation: analysis.ActiveExceptions > 0
                    ? "Prioritize active exception threads and top exception types for root-cause isolation."
                    : "Review top exception families for recurring fault paths.",
                Tags: ["crash", "exceptions", "threads"],
                MetricValue: analysis.ActiveExceptions,
                MetricUnit: "active-exceptions");
        }

        private ExceptionAnalysis AnalyzeExceptions(ClrHeap heap, ClrRuntime runtime)
        {
            var analysis = new ExceptionAnalysis();
            var exceptionsByType = new Dictionary<string, List<ExceptionInstance>>();
            var exceptionTypeCounts = new Dictionary<string, int>();
            var activeExceptionTypeCounts = new Dictionary<string, int>();
            var activeExceptions = BuildActiveExceptionLookup(runtime);
            var crashThreadCandidates = new Dictionary<uint, CrashThreadCandidate>();
            var scanCounter = new ObjectScanCounter("Crash exception scan");

            foreach (ClrObject obj in heap.EnumerateObjects())
            {
                scanCounter.Tick();

                if (!obj.IsValid || obj.Type == null)
                    continue;

                // Check if object is an exception
                string? typeName = obj.Type.Name;
                if (typeName?.Contains("Exception", StringComparison.Ordinal) == true)
                {
                    analysis.TotalExceptions++;
                    exceptionTypeCounts.TryGetValue(typeName, out int typeCount);
                    exceptionTypeCounts[typeName] = typeCount + 1;

                    bool isActive = activeExceptions.TryGetValue(obj.Address, out var activeExceptionContext);
                    if (isActive)
                    {
                        analysis.ActiveExceptions++;
                        activeExceptionTypeCounts.TryGetValue(typeName, out int activeTypeCount);
                        activeExceptionTypeCounts[typeName] = activeTypeCount + 1;

                        if (!crashThreadCandidates.TryGetValue(activeExceptionContext.ThreadId, out var candidate))
                        {
                            candidate = new CrashThreadCandidate
                            {
                                ThreadId = activeExceptionContext.ThreadId,
                                OSThreadId = activeExceptionContext.OSThreadId,
                                CurrentThreadStack = activeExceptionContext.CurrentThreadStack,
                                PrimaryExceptionType = typeName
                            };
                            crashThreadCandidates[activeExceptionContext.ThreadId] = candidate;
                        }

                        candidate.ActiveExceptionCount++;
                    }

                    // Only extract detailed info if we haven't hit the limit
                    if (!exceptionsByType.TryGetValue(typeName, out var list))
                    {
                        list = new List<ExceptionInstance>(capacity: MaxExceptionsPerType);
                        exceptionsByType[typeName] = list;
                    }

                    // Only store details for top N exceptions per type to save memory.
                    // Always include active exceptions, even beyond the cap.
                    if (list.Count < MaxExceptionsPerType || isActive)
                    {
                        var exceptionInstance = ExtractExceptionInfo(obj, activeExceptionContext);
                        list.Add(exceptionInstance);
                    }
                }
            }

            scanCounter.Complete();

            var sortedTypeNames = exceptionTypeCounts
                .OrderByDescending(kvp => kvp.Value)
                .Select(kvp => kvp.Key);

            var sortedExceptionsByType = new Dictionary<string, List<ExceptionInstance>>(exceptionsByType.Count);
            foreach (string typeName in sortedTypeNames)
            {
                if (exceptionsByType.TryGetValue(typeName, out var list))
                {
                    sortedExceptionsByType[typeName] = list;
                }
            }

            analysis.ExceptionTypeCounts = exceptionTypeCounts;
            analysis.ActiveExceptionTypeCounts = activeExceptionTypeCounts;
            analysis.ExceptionsByType = sortedExceptionsByType;
            analysis.CrashThreadCandidates = crashThreadCandidates.Values
                .OrderByDescending(c => c.ActiveExceptionCount)
                .ToList();

            return analysis;
        }

        private Dictionary<ulong, ActiveExceptionContext> BuildActiveExceptionLookup(ClrRuntime runtime)
        {
            var lookup = new Dictionary<ulong, ActiveExceptionContext>();
            var scanCounter = new ObjectScanCounter("Crash thread scan", reportEveryObjects: 100, reportEveryElapsed: TimeSpan.FromSeconds(1));

            foreach (var thread in runtime.Threads)
            {
                scanCounter.Tick();

                if (thread.CurrentException == null)
                    continue;

                lookup[thread.CurrentException.Address] = new ActiveExceptionContext
                {
                    ThreadId = (uint)thread.ManagedThreadId,
                    OSThreadId = thread.OSThreadId,
                    CurrentThreadStack = thread.EnumerateStackTrace().Take(10).ToList()
                };
            }

            scanCounter.Complete();

            return lookup;
        }

        private ExceptionInstance ExtractExceptionInfo(ClrObject exceptionObj, ActiveExceptionContext? activeContext)
        {
            var instance = new ExceptionInstance
            {
                Address = exceptionObj.Address,
                Type = exceptionObj.Type?.Name ?? StringConstants.UnknownType
            };

            try
            {
                // Get exception message
                var messageField = exceptionObj.Type?.GetFieldByName("_message");
                if (messageField != null)
                {
                    var messageObj = messageField.ReadObject(exceptionObj, interior: false);
                    if (messageObj.IsValid)
                    {
                        instance.Message = messageObj.AsString() ?? "";
                    }
                }

                // Get HRESULT
                var hresultField = exceptionObj.Type?.GetFieldByName("_HResult");
                if (hresultField != null)
                {
                    instance.HResult = hresultField.Read<int>(exceptionObj, interior: false);
                }

                // Get inner exception
                var innerExceptionField = exceptionObj.Type?.GetFieldByName("_innerException");
                if (innerExceptionField != null)
                {
                    var innerObj = innerExceptionField.ReadObject(exceptionObj, interior: false);
                    if (innerObj.IsValid && innerObj.Type != null)
                    {
                        instance.InnerExceptionType = innerObj.Type.Name;
                    }
                }

                // Get the ORIGINAL stack trace from exception object (not thread stack)
                instance.OriginalStackTrace = ExtractExceptionStackTrace(exceptionObj);

                if (activeContext != null)
                {
                    instance.ThreadId = activeContext.ThreadId;
                    instance.OSThreadId = activeContext.OSThreadId;
                    instance.CurrentThreadStack = activeContext.CurrentThreadStack;
                }
            }
            catch
            {
                // Continue with partial info
            }

            return instance;
        }

        private List<string> ExtractExceptionStackTrace(ClrObject exceptionObj)
        {
            var stackFrames = new List<string>();

            try
            {
                // Try to get _stackTraceString first (formatted string)
                var stackTraceStringField = exceptionObj.Type?.GetFieldByName("_stackTraceString");
                if (stackTraceStringField != null)
                {
                    var stackTraceObj = stackTraceStringField.ReadObject(exceptionObj, interior: false);
                    if (stackTraceObj.IsValid)
                    {
                        string? stackTraceStr = stackTraceObj.AsString();
                        if (!string.IsNullOrEmpty(stackTraceStr))
                        {
                            // Split by newlines and clean up
                            var lines = stackTraceStr.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                            foreach (var line in lines)
                            {
                                string trimmed = line.Trim();
                                if (!string.IsNullOrEmpty(trimmed))
                                {
                                    stackFrames.Add(trimmed);
                                }
                            }
                            return stackFrames;
                        }
                    }
                }

                // Try to parse _stackTrace field (native format)
                var stackTraceField = exceptionObj.Type?.GetFieldByName("_stackTrace");
                if (stackTraceField != null)
                {
                    var stackTraceObj = stackTraceField.ReadObject(exceptionObj, interior: false);
                    if (stackTraceObj.IsValid && stackTraceObj.IsArray)
                    {
                        var array = stackTraceObj.AsArray();
                        for (int i = 0; i < Math.Min(array.Length, 50); i++)
                        {
                            var element = array.GetObjectValue(i);
                            if (element.IsValid && element.Type != null)
                            {
                                // Each element is a StackTraceElement - try to extract method info
                                var methodField = element.Type.GetFieldByName("_method");
                                if (methodField != null)
                                {
                                    var methodObj = methodField.ReadObject(element, interior: false);
                                    if (methodObj.IsValid)
                                    {
                                        // Try to get method name
                                        var nameField = methodObj.Type?.GetFieldByName("_name");
                                        if (nameField != null)
                                        {
                                            var nameObj = nameField.ReadObject(methodObj, interior: false);
                                            if (nameObj.IsValid)
                                            {
                                                string? methodName = nameObj.AsString();
                                                if (!string.IsNullOrEmpty(methodName))
                                                {
                                                    stackFrames.Add($"   at {methodName}");
                                                }
                                            }
                                        }
                                    }
                                }
                            }
                        }
                    }
                }

                // If still no stack, try to get from exception's ToString()
                if (stackFrames.Count == 0)
                {
                    var remoteStackField = exceptionObj.Type?.GetFieldByName("_remoteStackTraceString");
                    if (remoteStackField != null)
                    {
                        var remoteStackObj = remoteStackField.ReadObject(exceptionObj, interior: false);
                        if (remoteStackObj.IsValid)
                        {
                            string? remoteStack = remoteStackObj.AsString();
                            if (!string.IsNullOrEmpty(remoteStack))
                            {
                                stackFrames.Add(remoteStack);
                            }
                        }
                    }
                }
            }
            catch
            {
                // Return what we have
            }

            return stackFrames;
        }

    }

    internal class ExceptionAnalysis
    {
        public int TotalExceptions { get; set; }
        public int ActiveExceptions { get; set; }
        public Dictionary<string, int> ExceptionTypeCounts { get; set; } = new();
        public Dictionary<string, int> ActiveExceptionTypeCounts { get; set; } = new();
        public Dictionary<string, List<ExceptionInstance>> ExceptionsByType { get; set; } = new();
        public List<CrashThreadCandidate> CrashThreadCandidates { get; set; } = new();
    }

    internal class CrashThreadCandidate
    {
        public uint ThreadId { get; set; }
        public uint OSThreadId { get; set; }
        public int ActiveExceptionCount { get; set; }
        public string PrimaryExceptionType { get; set; } = string.Empty;
        public List<ClrStackFrame> CurrentThreadStack { get; set; } = new();
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
        public uint? ThreadId { get; set; }
        public uint? OSThreadId { get; set; }
        public List<string> OriginalStackTrace { get; set; } = new();
        public List<ClrStackFrame> CurrentThreadStack { get; set; } = new();
    }
}
