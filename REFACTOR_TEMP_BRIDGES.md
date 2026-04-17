# Temporary Refactor Bridges

Tracking file for `TEMP-REFRACTOR-BRIDGE` markers introduced during staged migration.

## Current Marked Bridges

- `DumpDetective.Core/Abstractions/IAnalyzer.cs`
  - `AnalysisContext.Cache` dynamic bridge property.
  - Remove when Spec 03 async contracts and enriched context are fully implemented.

- `DumpDetective.Analysis/Pipeline/AnalysisPipeline.cs`
  - Adapter mapping `DumpDetective.Analysis.Pipeline.AnalysisContext` to `DumpDetective.Core.Abstractions.AnalysisContext`.
  - Remove when analyzers use final async contract directly.

- `DumpDetective.Analysis/Configuration/AnalysisConfiguration.cs`
  - Temporary shim for analyzer constructor compatibility.
  - Replace with Spec 02 `System.CommandLine` + `IOptions<T>` model.

- `DumpDetective.Analysis/Utilities/ConsoleUx.cs`
  - Temporary no-op/compatibility console surface.
  - Remove after CLI/DI ownership is finalized.

- `DumpDetective.Analysis/GlobalUsings.cs`
  - Transitional global usings to reduce migration churn.
  - Minimize/remove after namespace and dependency cleanup.

- `DumpDetective.Reporting/GlobalUsings.cs`
  - Transitional global usings for reporter compilation.
  - Minimize/remove after reporter registration and boundaries are finalized.

- `DumpDetective.Reporting/Output/TextWriterExtensions.cs`
  - Bridge extension methods for `TextWriter` while `OutputWriter` boundary is in flux.
  - Remove if/when concrete reporting writer contract is restored.
