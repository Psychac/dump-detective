# ThreadStackClusterAnalyzer — Presets

Purpose: cluster similar stacks to identify hotspots.

Options:
- `MaxClusters`, `MinClusterSize`, `ProduceClusterExports`


Built-in presets (from `ThreadStackClusterAnalysisOptions.Preset`):
- **Fast:** `MaxFramesPerSignature = 4`, `MaxThreadIdsPerCluster = 5`, `TopSignaturesToShow = 3`, `TopClustersToShow = 8`.
- **Balanced (default):** class defaults: `MaxFramesPerSignature = 6`, `MaxThreadIdsPerCluster = 8`, `TopSignaturesToShow = 5`, `TopClustersToShow = 12`.
- **Full:** `MaxFramesPerSignature = 10`, `MaxThreadIdsPerCluster = 20`, `TopSignaturesToShow = 10`, `TopClustersToShow = 20`.

Rationale:
- **Fast:** tight caps to keep clustering cheap on large thread counts.
- **Balanced:** default trade-off for clustering quality vs work.
- **Full:** increase frames and cluster sizes to reveal subtle thread-stack hotspots; higher memory/CPU for aggregation.
