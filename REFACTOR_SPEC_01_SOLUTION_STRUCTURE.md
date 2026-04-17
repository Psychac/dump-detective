# DumpDetective — Spec 01: Solution & Project Structure

> **Phase:** 0 + 1 (repo preparation + core models move)
> **Prerequisite:** None — this is the first spec to execute.

---

## 1. Solution Layout

```
DumpDetective.sln
│
├── src/
│   ├── DumpDetective.Core/          # Models, interfaces, shared utilities — no ClrMD
│   ├── DumpDetective.Analysis/      # Analyzers, pipeline, heap cache — depends on Core
│   ├── DumpDetective.Reporting/     # Formatters, printers, report builder — depends on Core + Analysis
│   └── DumpDetective.Cli/           # Entry point, DI wiring, CLI — depends on all
│
├── tests/
│   └── DumpDetective.Tests/         # xUnit — depends on Core, Analysis, Reporting
│
└── benchmarks/
    └── BenchmarkSuite1/             # Existing benchmark project — unchanged
```

---

## 2. Project Files

### 2.1 `DumpDetective.Core.csproj`
```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <LangVersion>14.0</LangVersion>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <RootNamespace>DumpDetective.Core</RootNamespace>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Microsoft.Diagnostics.Runtime" Version="3.*" />
    <PackageReference Include="Microsoft.Extensions.Options" Version="10.*" />
  </ItemGroup>
</Project>
```

### 2.2 `DumpDetective.Analysis.csproj`
```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <LangVersion>14.0</LangVersion>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <RootNamespace>DumpDetective.Analysis</RootNamespace>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Microsoft.Diagnostics.Runtime" Version="3.*" />
    <ProjectReference Include="..\DumpDetective.Core\DumpDetective.Core.csproj" />
  </ItemGroup>
</Project>
```

### 2.3 `DumpDetective.Reporting.csproj`
```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <LangVersion>14.0</LangVersion>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <RootNamespace>DumpDetective.Reporting</RootNamespace>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="..\DumpDetective.Core\DumpDetective.Core.csproj" />
    <ProjectReference Include="..\DumpDetective.Analysis\DumpDetective.Analysis.csproj" />
  </ItemGroup>
</Project>
```

### 2.4 `DumpDetective.Cli.csproj`
```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
    <LangVersion>14.0</LangVersion>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <RootNamespace>DumpDetective.Cli</RootNamespace>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="System.CommandLine" Version="2.0.0-beta4.*" />
    <PackageReference Include="Microsoft.Extensions.Hosting" Version="10.*" />
    <PackageReference Include="Microsoft.Extensions.Options.ConfigurationExtensions" Version="10.*" />
    <ProjectReference Include="..\DumpDetective.Core\DumpDetective.Core.csproj" />
    <ProjectReference Include="..\DumpDetective.Analysis\DumpDetective.Analysis.csproj" />
    <ProjectReference Include="..\DumpDetective.Reporting\DumpDetective.Reporting.csproj" />
  </ItemGroup>
</Project>
```

### 2.5 `DumpDetective.Tests.csproj`
```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <LangVersion>14.0</LangVersion>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <IsPackable>false</IsPackable>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.*" />
    <PackageReference Include="xunit" Version="2.*" />
    <PackageReference Include="xunit.runner.visualstudio" Version="2.*" />
    <PackageReference Include="FluentAssertions" Version="7.*" />
    <ProjectReference Include="..\DumpDetective.Core\DumpDetective.Core.csproj" />
    <ProjectReference Include="..\DumpDetective.Analysis\DumpDetective.Analysis.csproj" />
    <ProjectReference Include="..\DumpDetective.Reporting\DumpDetective.Reporting.csproj" />
  </ItemGroup>
</Project>
```

---

## 3. File-to-Project Mapping

### `DumpDetective.Core`

```
DumpDetective.Core/
├── Models/
│   ├── InsightFinding.cs          ← was DumpDetective\Models\InsightFinding.cs
│   ├── FindingSeverity.cs         ← extracted enum from InsightFinding.cs
│   ├── FindingFingerprint.cs      ← was DumpDetective\Models\FindingFingerprint.cs
│   ├── AnalysisSnapshot.cs        ← was DumpDetective\Models\AnalysisSnapshot.cs
│   ├── AnalyzerDomainResult.cs    ← was DumpDetective\Models\AnalyzerDomainResult.cs
│   ├── StringLeakInfo.cs          ← was DumpDetective\Models\StringLeakInfo.cs
│   ├── RootedTypeInfo.cs          ← was DumpDetective\Models\RootedTypeInfo.cs
│   ├── EventGroupInfo.cs          ← was DumpDetective\Models\EventGroupInfo.cs
│   ├── AnalyzerTrendContracts.cs  ← was DumpDetective\Models\AnalyzerTrendContracts.cs
│   ├── FindingTrendModels.cs      ← was DumpDetective\Models\FindingTrendModels.cs
│   └── AnalyzerRunResult.cs       ← was inline in Services (AnalysisRunResult)
│
├── Abstractions/
│   ├── IAnalyzer.cs               ← was DumpDetective\Analyzers\IAnalyzer.cs  (async — see Spec 03)
│   ├── IAnalyzerReporter.cs       ← was DumpDetective\Analyzers\IAnalyzerReporter.cs
│   └── IAnalyzerTrendComparer.cs  ← was DumpDetective\Services\Comparers\... (interface only)
│
├── Options/
│   ├── MemoryLeakOptions.cs       ← NEW (see Spec 02)
│   ├── ReferenceChainOptions.cs   ← NEW (see Spec 02)
│   ├── EventLeakOptions.cs        ← NEW (see Spec 02)
│   ├── DiagnosticsOptions.cs      ← NEW (see Spec 02)
│   └── ReportOptions.cs           ← NEW (see Spec 02)
│
├── Utilities/
│   ├── StringConstants.cs         ← was DumpDetective\Utilities\StringConstants.cs
│   ├── FormatHelper.cs            ← was DumpDetective\Utilities\FormatHelper.cs
│   ├── FindingTagger.cs           ← was DumpDetective\Utilities\FindingTagger.cs
│   └── TypeFilterHelper.cs        ← was DumpDetective\Utilities\TypeFilterHelper.cs
│
└── Configuration/
    └── ReportFormat.cs            ← was DumpDetective\Configuration\ReportFormat.cs
```

### `DumpDetective.Analysis`

```
DumpDetective.Analysis/
├── Analyzers/
│   ├── MemoryAnalyzer.cs
│   ├── MemoryLeakAnalyzer.cs
│   ├── GCGenerationAnalyzer.cs
│   ├── GCHandleAnalyzer.cs
│   ├── DependentHandleAnalyzer.cs
│   ├── CollectionAnalyzer.cs
│   ├── CrashAnalyzer.cs
│   ├── HangAnalyzer.cs
│   ├── LockGraphAnalyzer.cs
│   ├── LohFragmentationAnalyzer.cs
│   ├── ModuleAnalyzer.cs
│   ├── ReferenceChainAnalyzer.cs
│   ├── StaticRootLeakDetector.cs
│   ├── ThreadAnalyzer.cs
│   ├── ThreadStackClusterAnalyzer.cs
│   └── EventLeakAnalyzer.cs
│
├── Pipeline/
│   ├── AnalysisPipeline.cs        ← was DumpDetective\Analyzers\AnalysisPipeline.cs (async)
│   └── AnalysisContext.cs         ← was DumpDetective\Analyzers\IAnalyzer.cs (inner class, enriched)
│
├── Cache/
│   ├── HeapAnalysisCache.cs       ← was DumpDetective\Utilities\HeapAnalysisCache.cs
│   └── ObjectScanCounter.cs       ← was DumpDetective\Utilities\ObjectScanCounter.cs
│
├── Trend/
│   ├── TrendAnalyzer.cs           ← was DumpDetective\Services\TrendAnalyzer.cs
│   └── Comparers/                 ← was DumpDetective\Services\Comparers\
│       └── AnalyzerTrendComparers.cs
│
├── Diagnostics/
│   ├── MemoryDiagnostic.cs        ← was DumpDetective\Utilities\MemoryDiagnostic.cs
│   └── ObjectInspector.cs         ← was DumpDetective\Analyzers\ObjectInspector.cs
│
└── Utilities/
    └── DelegateHelper.cs          ← was DumpDetective\Utilities\DelegateHelper.cs
```

### `DumpDetective.Reporting`

```
DumpDetective.Reporting/
├── Formatters/
│   ├── IReportFormatter.cs        ← NEW interface (see Spec 05)
│   ├── HtmlReportFormatter.cs     ← was ReportFormatter.Html.cs (partial → class)
│   ├── MarkdownReportFormatter.cs ← was ReportFormatter.Markdown.cs
│   ├── TextReportFormatter.cs     ← was ReportFormatter.Text.cs
│   └── ReportFormatterParser.cs   ← was ReportFormatter.Parser.cs (internal helper)
│
├── Printers/
│   ├── MemoryPrinter.cs
│   ├── MemoryLeakPrinter.cs
│   ├── GCGenerationPrinter.cs
│   ├── GCHandlePrinter.cs
│   ├── DependentHandlePrinter.cs
│   ├── CollectionPrinter.cs
│   ├── CrashPrinter.cs
│   ├── HangPrinter.cs
│   ├── LockGraphPrinter.cs
│   ├── LohFragmentationPrinter.cs
│   ├── ModulePrinter.cs
│   ├── ReferenceChainPrinter.cs
│   ├── StaticRootPrinter.cs
│   ├── ThreadPrinter.cs
│   ├── ThreadStackClusterPrinter.cs
│   └── EventLeakPrinter.cs
│
├── Output/
│   └── OutputWriter.cs            ← was DumpDetective\Utilities\OutputWriter.cs
│
└── Services/
    ├── ReportBuilder.cs           ← was DumpDetective\Services\ReportBuilder.cs
    ├── AnalyzerReportRenderer.cs  ← was DumpDetective\Services\AnalyzerReportRenderer.cs
    └── TrendReportComposer.cs     ← NEW — trend rendering split from ReportBuilder (see Spec 05)
```

### `DumpDetective.Cli`

```
DumpDetective.Cli/
├── Program.cs                     ← rewritten (see Spec 04)
├── Commands/
│   └── RootCommandBuilder.cs      ← NEW — System.CommandLine wiring (see Spec 02)
├── Hosting/
│   └── ServiceRegistration.cs     ← NEW — DI registrations (see Spec 04)
├── Services/
│   ├── DumpAnalysisService.cs     ← was DumpDetective\Services\DumpAnalysisService.cs (adapted)
│   └── DumpLoader.cs              ← was DumpDetective\Services\DumpLoader.cs (adapted)
└── Console/
    └── ConsoleUx.cs               ← was DumpDetective\Utilities\ConsoleUx.cs
```

---

## 4. Namespace Convention

| Project | Root Namespace |
|---|---|
| `DumpDetective.Core` | `DumpDetective.Core` |
| `DumpDetective.Analysis` | `DumpDetective.Analysis` |
| `DumpDetective.Reporting` | `DumpDetective.Reporting` |
| `DumpDetective.Cli` | `DumpDetective.Cli` |
| `DumpDetective.Tests` | `DumpDetective.Tests` |

All files use **file-scoped namespaces** (`namespace Foo.Bar;`).

---

## 5. Phase 1 Checklist

- [ ] Create `DumpDetective.Core.csproj`
- [ ] Move all files listed under `DumpDetective.Core` above — update namespaces, fix using directives
- [ ] Update `DumpDetective` (original project) project references to add `DumpDetective.Core`
- [ ] Verify build is green before proceeding to Spec 02
