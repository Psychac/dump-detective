using Microsoft.Diagnostics.Runtime;
using DumpDetective.Utilities;

namespace DumpDetective.Analyzers
{
    internal class CrashAnalyzer
    {
        private readonly OutputWriter _writer;

        public CrashAnalyzer(OutputWriter writer)
        {
            _writer = writer;
        }

        public void Analyze(ClrRuntime runtime, ClrHeap heap)
        {
            _writer.WriteHeader("CRASH ANALYSIS:");
            _writer.WriteLine("Detecting exceptions and crash information...\n");

            var exceptionInfo = AnalyzeExceptions(heap, runtime);

            if (exceptionInfo.TotalExceptions == 0)
            {
                _writer.WriteLine("No exceptions detected in dump (likely not a crash dump).");
                _writer.WriteLine(StringConstants.Equals80);
                return;
            }

            PrintExceptionSummary(exceptionInfo);
            PrintExceptionDetails(exceptionInfo);

            _writer.WriteLine(StringConstants.Equals80);
        }

        private ExceptionAnalysis AnalyzeExceptions(ClrHeap heap, ClrRuntime runtime)
        {
            var analysis = new ExceptionAnalysis();
            var exceptionsByType = new Dictionary<string, List<ExceptionInstance>>();

            foreach (ClrObject obj in heap.EnumerateObjects())
            {
                if (!obj.IsValid || obj.Type == null)
                    continue;

                // Check if object is an exception
                if (obj.Type.Name?.Contains("Exception", StringComparison.Ordinal) == true)
                {
                    analysis.TotalExceptions++;

                    var exceptionInstance = ExtractExceptionInfo(obj, runtime);
                    
                    if (!exceptionsByType.TryGetValue(exceptionInstance.Type, out var list))
                    {
                        list = new List<ExceptionInstance>();
                        exceptionsByType[exceptionInstance.Type] = list;
                    }
                    list.Add(exceptionInstance);

                    // Check if this exception is on a thread (likely the crash)
                    if (exceptionInstance.ThreadId.HasValue)
                    {
                        analysis.ActiveExceptions++;
                    }
                }
            }

            analysis.ExceptionsByType = exceptionsByType
                .OrderByDescending(kvp => kvp.Value.Count)
                .ToDictionary(kvp => kvp.Key, kvp => kvp.Value);

            return analysis;
        }

        private ExceptionInstance ExtractExceptionInfo(ClrObject exceptionObj, ClrRuntime runtime)
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

                // Find thread that has this exception
                foreach (var thread in runtime.Threads)
                {
                    if (thread.CurrentException != null && thread.CurrentException.Address == exceptionObj.Address)
                    {
                        instance.ThreadId = (uint)thread.ManagedThreadId;
                        instance.OSThreadId = thread.OSThreadId;
                        instance.StackTrace = thread.EnumerateStackTrace().Take(10).ToList();
                        break;
                    }
                }
            }
            catch
            {
                // Continue with partial info
            }

            return instance;
        }

        private void PrintExceptionSummary(ExceptionAnalysis analysis)
        {
            _writer.WriteLine("EXCEPTION SUMMARY:");
            _writer.WriteSeparator();
            _writer.WriteLine($"Total Exception Objects: {analysis.TotalExceptions:N0}");
            _writer.WriteLine($"Active Exceptions (on threads): {analysis.ActiveExceptions}");
            _writer.WriteLine($"Unique Exception Types: {analysis.ExceptionsByType.Count}");

            if (analysis.ActiveExceptions > 0)
            {
                _writer.WriteLine($"\n⚠️  CRASH DETECTED: {analysis.ActiveExceptions} active exception(s) found!");
            }

            _writer.WriteLine($"\nTop Exception Types:");
            foreach (var kvp in analysis.ExceptionsByType.Take(10))
            {
                int activeCount = kvp.Value.Count(e => e.ThreadId.HasValue);
                string activeMarker = activeCount > 0 ? $" ({activeCount} active ⚠️)" : "";
                _writer.WriteLine($"  {kvp.Key}: {kvp.Value.Count:N0} instance(s){activeMarker}");
            }
        }

        private void PrintExceptionDetails(ExceptionAnalysis analysis)
        {
            _writer.WriteLine($"\n\nDETAILED EXCEPTION INFORMATION:");
            _writer.WriteSeparator();

            int exNum = 1;
            foreach (var kvp in analysis.ExceptionsByType)
            {
                // Prioritize active exceptions
                var activeExceptions = kvp.Value.Where(e => e.ThreadId.HasValue).ToList();
                var inactiveExceptions = kvp.Value.Where(e => !e.ThreadId.HasValue).Take(2).ToList();

                foreach (var ex in activeExceptions.Concat(inactiveExceptions).Take(5))
                {
                    _writer.WriteLine($"\n[{exNum++}] {ex.Type}");
                    _writer.WriteLine($"    Address: 0x{ex.Address:X}");

                    if (!string.IsNullOrEmpty(ex.Message))
                    {
                        string truncated = FormatHelper.TruncateString(ex.Message, 200);
                        _writer.WriteLine($"    Message: {truncated}");
                    }

                    if (ex.HResult != 0)
                    {
                        _writer.WriteLine($"    HRESULT: 0x{ex.HResult:X8}");
                    }

                    if (ex.InnerExceptionType != null)
                    {
                        _writer.WriteLine($"    Inner Exception: {ex.InnerExceptionType}");
                    }

                    if (ex.ThreadId.HasValue)
                    {
                        _writer.WriteLine($"    ⚠️  ACTIVE on Thread: {ex.ThreadId} (OS: {ex.OSThreadId})");
                        
                        if (ex.StackTrace.Count > 0)
                        {
                            _writer.WriteLine($"    Stack Trace:");
                            foreach (var frame in ex.StackTrace)
                            {
                                string method = frame.Method?.Signature ?? frame.ToString() ?? "Unknown";
                                _writer.WriteLine($"      {FormatHelper.TruncateString(method, 70)}");
                            }
                        }
                    }
                    else
                    {
                        _writer.WriteLine($"    Status: Inactive (collected exception object)");
                    }
                }

                if (kvp.Value.Count > 5)
                {
                    _writer.WriteLine($"\n    ... and {kvp.Value.Count - 5} more {kvp.Key} instance(s)");
                }
            }
        }
    }

    internal class ExceptionAnalysis
    {
        public int TotalExceptions { get; set; }
        public int ActiveExceptions { get; set; }
        public Dictionary<string, List<ExceptionInstance>> ExceptionsByType { get; set; } = new();
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
        public List<ClrStackFrame> StackTrace { get; set; } = new();
    }
}
