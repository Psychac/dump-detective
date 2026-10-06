using DumpDetective.Sdk.Synthesis;
using System.Xml.Linq;

using FluentAssertions;

using Xunit;

namespace DumpDetective.Tests.Unit.Architecture;

public sealed class DependencyDirectionTests
{
    [Fact]
    public void RefactoredProjects_ShouldFollowStrictDependencyDirection()
    {
        string repoRoot = FindRepositoryRoot();

        IReadOnlyCollection<string> coreRefs = ReadProjectReferenceNames(Path.Combine(repoRoot, "src", "DumpDetective.Core", "DumpDetective.Core.csproj"));
        IReadOnlyCollection<string> analysisRefs = ReadProjectReferenceNames(Path.Combine(repoRoot, "src", "DumpDetective.Analysis", "DumpDetective.Analysis.csproj"));
        IReadOnlyCollection<string> reportingRefs = ReadProjectReferenceNames(Path.Combine(repoRoot, "src", "DumpDetective.Reporting", "DumpDetective.Reporting.csproj"));
        IReadOnlyCollection<string> cliRefs = ReadProjectReferenceNames(Path.Combine(repoRoot, "src", "DumpDetective.Cli", "DumpDetective.Cli.csproj"));

        // DumpDetective.Sdk added 2026-10-06 (M1 step 6): AnalyzerRunResult now carries the Sdk's Claim and Severity
        // instead of Core's Claim and Severity. Core was never meant to be a leaf by design — it is the
        // leftover report-side model layer, shrinking toward Sdk — and the Sdk depends on nothing, so no cycle is possible.
        coreRefs.Should().Equal(["DumpDetective.Sdk"]);
        // DumpDetective.Sdk added 2026-09-10: the Phase 1 retyping pilot's dump-side Tier-1
        // capability-surface implementations (Sdk/HeapTypeStatisticsQuery.cs) live in this
        // project — see docs/refactor/modularity/phase-1-full-extraction-retyping-plan.md.
        analysisRefs.Should().Equal(["DumpDetective.Core", "DumpDetective.Platform", "DumpDetective.Sdk"]);
        // DumpDetective.Sdk added 2026-09-09: TraceSessionReport (the trace-only report.json
        // shape) belongs here, not in Cli — this project is the one that owns report-shape
        // contracts (see AnalysisReportDocument), the same reasoning that already puts
        // ReportOutputWriter's file-writing role in Cli instead.
        // DumpDetective.Plugins.Memory added 2026-09-15: Phase 3 migration step 3's first
        // domain package (docs/refactor/modularity/phase-3-plugin-packaging.md) — Reporting still
        // needs to reference it directly since DefaultAnalyzerFeatureModuleCatalog constructs its
        // analyzer/model/comparer types by typeof(), and PluginCatalogBuilder's TrendComparer
        // resolution still walks analyzerType.Assembly.
        // DumpDetective.Plugins.Gc added 2026-09-15: Phase 3 migration step 3's second domain
        // package — GCRoot/GCHandle/FinalizableObject/WeakReference. Unlike Plugins.Memory, this
        // one needed no leftover models in Analysis: LeakCandidateAnalyzer's cross-analyzer read of
        // GCHandleDomainResult/GCRootDomainResult was decoupled first (structural signal interfaces
        // in DumpDetective.Core.Models), so all four models moved cleanly with no carve-out.
        // DumpDetective.Plugins.Threads added 2026-09-15: Phase 3 migration step 3's third domain
        // package — Thread/ThreadStackCluster/Hang/LockGraph. This assertion wasn't updated when
        // that package shipped; caught here as a side effect of adding Plugins.Async.
        // DumpDetective.Plugins.Async added 2026-09-15: Phase 3 migration step 3's fourth domain
        // package — AsyncTask/AsyncStateMachine. No carve-out needed: neither domain result is read
        // by another analyzer, so unlike Plugins.Gc there was no cross-analyzer coupling to unwind.
        // DumpDetective.Plugins.Types added 2026-09-15: Phase 3 migration step 3's fifth domain
        // package — ObjectShape/Array/Boxing/Collection/String. Cleanest move yet: no cross-analyzer
        // DomainResult reads, and CollectionAnalyzer's ReferenceChainOptions dependency is only a
        // Core.Options record, not a reference to the not-yet-migrated ReferenceChainAnalyzer.
        // DumpDetective.Plugins.Leaks added 2026-09-15: Phase 3 migration step 3's sixth domain
        // package — LeakCandidate/StaticRoot/ReferenceChain/Dominator/EventLeak. New blocker not
        // seen in any prior package: DumpDetective.Analysis.Trend.TrendAnalyzer (shared, source-
        // neutral trend dispatcher) read DominatorDomainResult/StaticRootDomainResult/
        // LeakCandidateDomainResult by concrete type in its new-leak-signal heuristic. Fixed the
        // same way LeakCandidateAnalyzer's cross-analyzer coupling was fixed ahead of Plugins.Gc:
        // narrow structural signal interfaces (IRetainedObjectsByTypeSignal,
        // IStaticRootsByDescriptionSignal, ILeakCandidatesByTypeSignal) in DumpDetective.Core.Models.
        // EventLeakEvidence stayed behind in Analysis/Models/Evidence.cs (not moved with
        // EventLeakDomainResult) because EvidenceConfidence's EventLeakEvidence overload — a shared
        // utility every domain's finding generator relies on — needs it directly.
        // DumpDetective.Plugins.Runtime added 2026-09-15: Phase 3 migration step 3's seventh domain
        // package — Module/Crash/Jit/HeapTopology. Cleanest move of all seven: no cross-analyzer
        // DomainResult reads either direction, and every pipeline interface the quartet implements
        // (IParallelHeapIndexScanParticipant, ICustomizesAnalysisContext) is an Analysis/Pipeline
        // forward reference, same as the Threads/Async precedent.
        // DumpDetective.Plugins.Infra added 2026-09-15: Phase 3 migration step 3's eighth and final
        // domain package — DbConnection/WcfChannel/HttpObject/TimerLeak/SqlCommand/
        // SqlConnectionPool/SqlTransaction. No cross-analyzer DomainResult reads either direction;
        // TimerLeakDomainResult already read only through ILogicalTimerCountSignal. The shared
        // typed-resource pipeline infra (ITypedResourceCandidateSource, TypedResourceScanDriver,
        // TypedResourceSampler, TypedResourceHeapIndexScanState) stayed in Analysis instead of
        // moving here, since Plugins.Async/Plugins.Gc/Plugins.Types already forward-reference it —
        // moving it into Plugins.Infra would have created a forbidden Plugins.* -> Plugins.Infra
        // dependency.
        reportingRefs.Should().Equal(["DumpDetective.Analysis", "DumpDetective.Core", "DumpDetective.Plugins.Async", "DumpDetective.Plugins.Gc", "DumpDetective.Plugins.Infra", "DumpDetective.Plugins.Leaks", "DumpDetective.Plugins.Memory", "DumpDetective.Plugins.Runtime", "DumpDetective.Plugins.Threads", "DumpDetective.Plugins.Types", "DumpDetective.Sdk"]);
        // DumpDetective.Sources.NetTrace added 2026-09-09: the interim trace/dump router
        // (docs/refactor/modularity-plan.md § 8) needs Cli to reach the trace-only analysis path
        // directly, the same way it already reaches dump analysis through Analysis/Reporting.
        // DumpDetective.Orchestration added 2026-09-15: Phase 4 step 1's generic DAG engine
        // (docs/refactor/modularity/phase-4-session-orchestration.md) — Cli owns the concrete
        // single-dump node adapters (they depend on Analysis/Reporting types), Orchestration owns
        // only IPipelineNode/PipelineGraph/PipelineExecutor.
        cliRefs.Should().Equal(["DumpDetective.Analysis", "DumpDetective.Core", "DumpDetective.Orchestration", "DumpDetective.Reporting", "DumpDetective.Sources.NetTrace"]);
    }

    [Fact]
    public void PlatformProject_ShouldDependOnSdkOnly()
    {
        // Phase 2, § 8-trimmed: "source-agnostic; references Sdk only" — see
        // docs/refactor/modularity/phase-2-artifact-platform.md. Platform must never pull in Core
        // (which carries the ClrMD package reference), Analysis, Reporting, or Cli.
        string repoRoot = FindRepositoryRoot();
        string platformProjectPath = Path.Combine(repoRoot, "src", "DumpDetective.Platform", "DumpDetective.Platform.csproj");

        IReadOnlyCollection<string> projectRefs = ReadProjectReferenceNames(platformProjectPath);

        projectRefs.Should().Equal(["DumpDetective.Sdk"]);
    }

    [Fact]
    public void OrchestrationProject_ShouldDependOnSdkAndPlatformOnly()
    {
        // Phase 4 step 1: "references Sdk + Platform; no Sources.*, no Plugins.*" —
        // docs/refactor/modularity/phase-4-session-orchestration.md's target shape. Orchestration
        // is the generic DAG engine (IPipelineNode/PipelineGraph/PipelineExecutor); it must not
        // know about dumps, traces, or any concrete analyzer domain.
        string repoRoot = FindRepositoryRoot();
        string orchestrationProjectPath = Path.Combine(repoRoot, "src", "DumpDetective.Orchestration", "DumpDetective.Orchestration.csproj");

        IReadOnlyCollection<string> projectRefs = ReadProjectReferenceNames(orchestrationProjectPath);

        projectRefs.Should().Equal(["DumpDetective.Platform", "DumpDetective.Sdk"]);
    }

    [Fact]
    public void SdkProject_ShouldHaveZeroDependenciesBeyondTheBcl()
    {
        // Phase 1 migration step 7: "Add SDK-boundary ... rules to the architecture test." The
        // whole point of the SDK is a contract surface any artifact source, any analyzer, any
        // consumer can target without pulling in the rest of the product — see
        // docs/refactor/modularity/phase-1-contracts-sdk.md.
        string repoRoot = FindRepositoryRoot();
        string sdkProjectPath = Path.Combine(repoRoot, "src", "DumpDetective.Sdk", "DumpDetective.Sdk.csproj");

        IReadOnlyCollection<string> projectRefs = ReadProjectReferenceNames(sdkProjectPath);
        IReadOnlyCollection<string> packageRefs = ReadPackageReferenceNames(sdkProjectPath);

        projectRefs.Should().BeEmpty("the SDK must not depend on any other project in this repo.");
        packageRefs.Should().BeEmpty("the SDK must not depend on any NuGet package — BCL only.");
    }

    private static IReadOnlyCollection<string> ReadPackageReferenceNames(string projectPath)
    {
        XDocument document = XDocument.Load(projectPath);

        return document
            .Descendants("PackageReference")
            .Select(r => (string?)r.Attribute("Include"))
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Select(name => name!)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();
    }

    private static IReadOnlyCollection<string> ReadProjectReferenceNames(string projectPath)
    {
        XDocument document = XDocument.Load(projectPath);

        return document
            .Descendants("ProjectReference")
            .Select(r => (string?)r.Attribute("Include"))
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(path => Path.GetFileNameWithoutExtension(path!))
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? current = new(AppContext.BaseDirectory);

        while (current is not null)
        {
            string slnxPath = Path.Combine(current.FullName, "DumpDetective.slnx");
            if (File.Exists(slnxPath))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new DirectoryNotFoundException("Could not find repository root containing DumpDetective.slnx.");
    }
}
