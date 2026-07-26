# P1 Item 11: Close the Crash-Triage Gap — Minidump Exception Stream Investigation

**Status:** Investigation completed  
**Date:** 2026-07-26  
**Finding:** ClrMD 4.0 does not expose minidump exception stream APIs publicly; direct DBGHELP P/Invoke required

---

## Executive Summary

**The Gap Is Real and Closeable**

P1 Item 11 targets automated crash-triage from minidump exception streams (WinDbg `!analyze -v` equivalent). Current `CrashAnalyzer` only analyzes heap-resident Exception objects and active thread exceptions. Minidump exception streams contain crash context (faulting thread, exception code, fault address) that the heap scan misses.

Research confirms:
- ✅ Gap is validated as real (Deliverables 2, 3, 9)
- ✅ Not an architectural mismatch
- ✅ Closeable with direct minidump parsing
- ❌ ClrMD 4.0 does **not** expose public APIs for this

**Recommended approach:** Direct minidump file parsing via Windows DBGHELP P/Invoke (same approach as WinDbg, debuggers, and Windows Error Reporting).

---

## Background: Current State

### What CrashAnalyzer Currently Does

```csharp
// BuildActiveExceptionLookup: iterates threads for active exceptions
foreach (var thread in runtime.Threads)
{
    if (thread.CurrentException == null)
        continue;
    
    // Captures: Address, Type name, HResult from heap Exception object
    // Also: thread stack frames at crash time
}
```

**Data currently captured:**
- Exception type (from heap object)
- Exception message, HResult, inner exception
- Original stack trace from Exception._stackTraceString
- Active thread's current stack frames
- Active exception count/pressure

**Data currently NOT captured:**
- ❌ What exception actually crashed the process (minidump exception stream)
- ❌ Faulting thread ID (from crash context, not thread enumeration)
- ❌ Exception code (0xC0000005 = access violation, etc.)
- ❌ Fault address (where the crash occurred)
- ❌ Processor fault context (registers, etc.)

### The Minidump Exception Stream

Windows minidump format includes an optional `MINIDUMP_EXCEPTION_STREAM` that records:

```
MINIDUMP_EXCEPTION_STREAM {
  uint ThreadId              // Which thread faulted
  uint __Reserved
  MINIDUMP_EXCEPTION {
    uint ExceptionCode       // 0xC0000005 (AV), 0x80000003 (breakpoint), etc.
    uint ExceptionFlags      // EXCEPTION_NONCONTINUABLE, etc.
    ulong ExceptionRecord    // Address of native exception record
    ulong ExceptionAddress   // Where the fault occurred
    uint NumberParameters
    uint __UnusedAlignment
    ulong[15] ExceptionInformation  // Context-specific data
  }
}
```

**Example exception codes:**
- `0xC0000005` — Access Violation (reading/writing invalid memory)
- `0xC0000008` — Invalid Handle
- `0x80000003` — Breakpoint/Debug trap
- `0xE0434352` — .NET Runtime exception (CLR-specific)

---

## ClrMD 4.0 Research Findings

### What ClrMD 4.0 Currently Exposes

✅ **`thread.CurrentException`** → `ClrException`
- Properties: `Address`, `Type` (ClrType), `HResult`
- This is what DumpDetective currently uses
- Only covers active exceptions on threads (not the crash-time exception)

✅ **`ClrObject.IsException` and `ClrObject.AsException`**
- Same properties via heap object inspection

### What ClrMD 4.0 Reads But Doesn't Expose

🔍 **ClrMD 4.0 internally processes minidump streams:**
- Migration guide (v3→v4) mentions `DataTargetLimits.MaxMinidumpStreams` (default: 10,000)
- This proves ClrMD parses minidump stream structures
- Used internally for process introspection, but **NOT publicly exposed**

### ClrMD 4.0 Documentation Findings

**Getting Started guide** — covers:
- Thread enumeration via `runtime.Threads`
- Exception access via `thread.CurrentException` (returns `ClrException`)
- No mention of crash context or exception stream APIs

**Migration Guide (v3→v4)** — new features:
- `DataTargetOptions.UseLockFreeMemoryMapReader` — memory-mapped dump reads
- `DataTargetLimits` — configurable parsing bounds (including `MaxMinidumpStreams`)
- cDAC support for universal DAC binaries
- **No new exception stream APIs**

**Samples** — ClrStack example shows:
```csharp
if (thread.CurrentException is ClrException ex)
    Console.WriteLine("Exception: {0:X} ({1}), HRESULT={2:X}", 
        ex.Address, ex.Type.Name, ex.HResult);
```
Still only `thread.CurrentException`, no crash stream access.

---

## Options Analysis

### Option A: Use ClrMD Internal APIs ❌ Not Recommended

**Approach:** Reflection to access internal exception stream parsing

**Pros:**
- Uses existing ClrMD infrastructure
- No external P/Invoke

**Cons:**
- Fragile — internals can change between versions
- Breaks on ClrMD updates (currently 4.0.732401)
- Violates design principles (internal = not supported)
- No documentation to guide implementation

**Verdict:** High risk, low maintainability. Skip.

---

### Option B: Direct Minidump Parsing via DBGHELP ✅ Recommended

**Approach:** P/Invoke to Windows DBGHELP.DLL to read exception stream directly

**Pros:**
- ✅ Same approach used by WinDbg, debuggers, Windows Error Reporting
- ✅ Well-documented minidump format (Microsoft public spec)
- ✅ Low maintenance — DBGHELP is stable Windows API
- ✅ Fast — single stream read, no full dump reparse
- ✅ Clear boundary — separate reader, easy to test

**Cons:**
- ❌ Windows-only (no Linux/macOS support)
- ❌ P/Invoke boilerplate (MINIDUMP_EXCEPTION_STREAM struct definitions)
- ❌ Requires matching dump architecture (x86 vs x64)
- ⚠️ Optional stream — not all minidumps include exception stream

**Implementation Sketch:**
```csharp
// New file: src/DumpDetective.Analysis/Dump/MinidumpExceptionReader.cs
internal sealed class MinidumpExceptionReader
{
    // P/Invoke DBGHELP.DLL
    private const string DbgHelpDll = "dbghelp.dll";
    
    [DllImport(DbgHelpDll, SetLastError = true)]
    private static extern bool MiniDumpReadDumpStream(
        IntPtr hFile,
        MINIDUMP_STREAM_TYPE StreamType,
        IntPtr Dir,
        out IntPtr StreamPointer,
        out uint StreamSize);

    // struct MINIDUMP_EXCEPTION_STREAM { ... }
    
    public static MinidumpException? TryReadExceptionStream(
        string dumpPath, DataTarget dataTarget)
    {
        // Open dump file, call MiniDumpReadDumpStream, parse exception record
        // Return: null if stream not present, MinidumpException record if found
    }
}

// Augment CrashAnalyzer:
// In BeforeHeapIndexScan, call MinidumpExceptionReader after BuildActiveExceptionLookup
// Store result in instance state, merge into analysis output
```

**Effort estimate:** 2–4 days
- Day 1: P/Invoke struct definitions, basic reader
- Day 2: Integration into CrashAnalyzer, error handling
- Day 3–4: Tests, edge cases (missing stream, corrupt stream), documentation

**Verdict:** Viable, proven, maintainable. **Recommended path forward.**

---

### Option C: Request Feature from ClrMD Team (Future)

**Approach:** File issue on https://github.com/microsoft/clrmd requesting public API

```
Title: "Request: Public API to access minidump exception stream"
Body: CrashAnalyzer needs to read exception records (faulting thread, code, address)
from minidump exception streams. Currently only thread.CurrentException is exposed.
Please expose MINIDUMP_EXCEPTION_STREAM data or a helper to read it.
```

**Pros:**
- Most robust long-term solution
- No P/Invoke maintenance burden
- Cross-platform potential

**Cons:**
- Blocks on ClrMD roadmap/resources
- Likely 3–6 month timeline
- May not align with ClrMD priorities

**Verdict:** Pursue in parallel (file issue now) but don't block on it.

---

## Recommendation

**Implement Option B (Direct DBGHELP P/Invoke) for P1 Item 11**

### Why This Combination

1. **Option B** closes the gap immediately with a stable, proven approach
2. **Option C** (file the ClrMD issue in parallel) for future V2 that uses ClrMD native support
3. **Accept Windows-only scope** — minidump exception streams are a Windows feature; Linux/macOS targets lack them

### Next Steps

1. **Create spike task** — prototype `MinidumpExceptionReader` with 1–2 test dumps
2. **Validate DBGHELP availability** — confirm Windows 7+ has required APIs
3. **Augment `CrashDomainResult`** — add fields for exception code, faulting address
4. **Update `CrashAnalyzer`** — call reader, merge exception stream data
5. **File ClrMD issue** — document request for native API support (Option C)

### Deliverables

**Phase 1 (P1 Item 11):**
- `MinidumpExceptionReader` class (internal)
- `ExceptionStreamData` model (exception code, faulting thread, fault address)
- Integration into `CrashAnalyzer.BeforeHeapIndexScan`
- Augmented `CrashDomainResult` with exception code + fault address
- Reporting: display exception code and fault address alongside heap exception data
- Tests: round-trip validation against known crash dumps

**Phase 2 (Future, if ClrMD adds API):**
- Refactor `MinidumpExceptionReader` to use ClrMD native APIs when available
- Remove P/Invoke dependency on ClrMD-supported platforms
- Maintain backward compatibility with older ClrMD versions

---

## References

### Minidump Format
- **Microsoft minidump spec:** https://docs.microsoft.com/en-us/windows/win32/api/minidumpapiset/
- **MINIDUMP_EXCEPTION_STREAM:** https://docs.microsoft.com/en-us/windows/win32/api/minidumpapiset/ns-minidumpapiset-minidump_exception_stream
- **Exception codes:** https://docs.microsoft.com/en-us/openspecs/windows_protocols/ms-erref/

### ClrMD 4.0
- **GitHub:** https://github.com/microsoft/clrmd
- **Migration guide (v3→v4):** https://github.com/microsoft/clrmd/blob/main/doc/Migrating4.md
- **NuGet:** https://www.nuget.org/packages/Microsoft.Diagnostics.Runtime/ (version 4.0.732401)

### WinDbg Reference
- **!analyze -v command** — displays exception record, faulting thread, fault address, recommendation
- **Model for expected output** when minidump exception stream is available

### Related Roadmap Items
- **Deliverable 2** — "Historical crash evidence (why the process actually died)" flagged as Partial/Unclear
- **Deliverable 3** — "Verify whether CrashAnalyzer reads minidump exception stream or only heap-resident exceptions"
- **Deliverable 9** — "Automated crash-triage from minidump exception stream (`!analyze -v` equivalent)" marked as real, closeable gap

---

## Appendix: Minidump Exception Stream Example

**Hypothetical exception stream from a crash dump:**

```
ThreadId: 0x1234 (crash occurred on thread 4660)
ExceptionCode: 0xC0000005 (Access Violation)
ExceptionFlags: 0x00000000 (Continuable)
ExceptionAddress: 0x00007FF8_12345678 (faulting instruction address)
ExceptionInformation[0]: 0x00000000 (read attempt; 1 = write attempt)
ExceptionInformation[1]: 0xFFFFFFFF_FFFFFFFF (invalid address being accessed)
```

**How CrashAnalyzer should use this:**

```csharp
var exceptionData = MinidumpExceptionReader.TryReadExceptionStream(dumpPath);
if (exceptionData != null)
{
    analysis.FaultingThreadId = exceptionData.ThreadId;
    analysis.ExceptionCode = exceptionData.ExceptionCode;      // 0xC0000005
    analysis.FaultAddress = exceptionData.ExceptionAddress;    // 0x00007FF8_12345678
    analysis.ExceptionDescription = GetExceptionCodeName(exceptionData.ExceptionCode);
}

// Output in report:
// "Faulting thread: 4660 (0x1234)"
// "Exception: Access Violation (0xC0000005)"
// "Fault address: 0x00007FF8_12345678"
```

---

**Document created:** 2026-07-26  
**Authored by:** Investigation spike  
**Status:** Ready for implementation planning (P1 Item 11)
