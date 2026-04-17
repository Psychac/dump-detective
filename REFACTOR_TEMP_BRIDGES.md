# Temporary Refactor Bridges

Tracking file for `TEMP-REFRACTOR-BRIDGE` markers introduced during staged migration.

## Current Marked Bridges

- `DumpDetective.Core/Abstractions/IAnalyzer.cs`
  - `AnalysisContext.Cache` now uses typed cache abstraction (`IHeapAnalysisCache`).
  - Dynamic cache bridge has been removed.


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

- `DumpDetective.Cli/Services/DumpAnalysisService.cs`
  - Temporary execution bridge that resolves/validates config and prints startup summary only.
  - Replace with full pipeline + reporting orchestration in Spec 03/04.

- `DumpDetective.Cli/Hosting/ServiceRegistration.cs`
  - Temporary manual factory wiring.
  - Replace with full `IHostBuilder`/DI registration model in Spec 04.

