# ThreadAnalyzer — Presets

Purpose: summarize threads, blocked calls, and stacks.

Options:
- `StackFrames`, `TopBlockedThreads`, `IncludeStackSamples`


Built-in presets (from `ThreadAnalysisOptions.Preset`):
- **Fast:** `MaxFramesForThreadScan = 4`, `MaxStackRootsToCount = 128`.
- **Balanced (default):** class defaults: `MaxFramesForThreadScan = 8`, `MaxStackRootsToCount = 256`.
- **Full:** `MaxFramesForThreadScan = 16`, `MaxStackRootsToCount = 1_024`.

Rationale:
- **Fast:** shallow stack sampling and smaller root counts for quick thread-level triage.
- **Balanced:** reasonable coverage of stack frames and root counts for typical investigations.
- **Full:** deeper frame capture and larger root counting to support exhaustive root/source analysis; higher CPU and memory.

Flow notes:
- Presets trade off amount of stack context retained and whether to capture sampled stacks for downstream analysis.
