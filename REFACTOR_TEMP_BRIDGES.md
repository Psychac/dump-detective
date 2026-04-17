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

- `DumpDetective.Reporting/Formatters/ReportFormatter` (legacy static partial files)
  - Legacy static formatter stack remains in repo for staged migration safety.
  - New canonical formatter pipeline uses `IReportFormatter` implementations; remove legacy static formatter stack after parity is fully validated.

- `DumpDetective.Cli/Services/DumpAnalysisService.cs`
  - Uses DI-registered analyzer factory, but analyzer creation remains runtime-option mapped via legacy `AnalysisConfiguration` shim.
  - Replace with fully option-bound DI analyzer registration once Spec 04/05 option binding is finalized.

- `DumpDetective.Cli/Hosting/ServiceRegistration.cs`
  - Host/DI is implemented with centralized registration and analyzer factory wiring.
  - Finalize with fully option-aware DI analyzer registration (remove factory bridge if no longer needed).

- `DumpDetective.Cli/Services/DefaultAnalyzerFactory.cs`
  - Transitional analyzer construction maps `ResolvedExecutionOptions` into legacy `AnalysisConfiguration` for three analyzers.
  - Remove when analyzers consume typed options directly from `AnalysisContext`.

