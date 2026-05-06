# Static Root Leak Detector — Presets

Purpose: identify static fields retaining large objects and surface top retained types and sample retained objects.

Where to look in the repo:
- Analyzer: src/DumpDetective.Analysis/Analyzers/StaticRootLeakDetector.cs
- Options: src/DumpDetective.Core/Options/StaticRootLeakAnalysisOptions.cs

Built-in presets (from `StaticRootLeakAnalysisOptions.Preset`):
- **Fast:**
	- `MaxRootsToReport = 8`
	- `TopRetainedTypesToReport = 3`
	- `SampleRetainedObjectsToInspect = 50`
	- `SignificantMemoryThresholdBytes = 2 * 1024 * 1024` (2 MB)
	- `SignificantObjectCountThreshold = 200`
	- `MaxRetainedObjectsToScan = 5_000`
- **Balanced (default):** class defaults:
	- `MaxRootsToReport = 15`, `TopRetainedTypesToReport = 5`, `SampleRetainedObjectsToInspect = 100`, `SignificantMemoryThresholdBytes = 1 MB`, `SignificantObjectCountThreshold = 100`, `MaxRetainedObjectsToScan = 10_000`.
- **Full:**
	- `MaxRootsToReport = 40`
	- `TopRetainedTypesToReport = 15`
	- `SampleRetainedObjectsToInspect = 500`
	- `SignificantMemoryThresholdBytes = 512 * 1024` (512 KB)
	- `SignificantObjectCountThreshold = 50`
	- `MaxRetainedObjectsToScan = 50_000`

Rationale:
- **Fast:** smaller root lists and sampling keep memory and CPU low while surfacing the most obvious static retention.
- **Balanced:** default balance between detection sensitivity and resource use.
- **Full:** aggressive sampling and larger root lists to find less-obvious static retention patterns; expect higher I/O and CPU.

Flow note:
- `MaxRetainedObjectsToScan` caps the expensive retainer-object enumeration; when capped the report annotates truncated scans.
