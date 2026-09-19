using DumpDetective.Analysis.Models;
using DumpDetective.Core.Models;
using DumpDetective.Reporting.FindingGenerators;
using DumpDetective.Reporting.SynthesisRules;
using DumpDetective.Sdk.Artifacts;
using DumpDetective.Sdk.Identity;
using DumpDetective.Sdk.Observations;
using DumpDetective.Sdk.Synthesis;
using DumpDetective.Sdk.Temporal;

using FluentAssertions;

using Xunit;

namespace DumpDetective.Tests.Unit.Synthesis;

/// <summary>
/// Field-for-field parity between <see cref="ModuleFindingGenerator"/> (old) and
/// <see cref="ModuleSynthesisRule"/> (new) — see
/// docs/refactor/modularity/phase-5-observations-synthesis.md's per-analyzer playbook.
/// </summary>
public sealed class ModuleSynthesisRuleParityTests
{
    private readonly ModuleFindingGenerator _oldGenerator = new();
    private readonly ModuleSynthesisRule _newRule = new();
    private static readonly ArtifactId TestArtifact = new("test-artifact");

    [Fact]
    public Task NoCrossDomainLoads_EmitsNoCrossDomainFinding() => AssertParity(BuildResult(crossDomainLoads: []));

    [Fact]
    public Task CrossDomainLoadsBelowThreshold_EmitsInfoFinding() => AssertParity(BuildResult(crossDomainLoads:
    [
        new CrossDomainModuleLoad("PluginHost.dll", "PluginHost, Version=1.0.0.0", DomainCount: 2, Size: 1024),
    ]));

    [Fact]
    public Task WidelyLoadedModule_EmitsWarningFinding() => AssertParity(BuildResult(crossDomainLoads:
    [
        new CrossDomainModuleLoad("PluginHost.dll", "PluginHost, Version=1.0.0.0", DomainCount: 3, Size: 1024),
    ]));

    [Fact]
    public Task HeavyCrossDomainModule_EmitsWarningFinding() => AssertParity(BuildResult(
        heavyModuleWarningThresholdBytes: 200 * 1024 * 1024,
        crossDomainLoads: [new CrossDomainModuleLoad("PluginHost.dll", "PluginHost, Version=1.0.0.0", DomainCount: 2, Size: 300 * 1024 * 1024)]));

    [Fact]
    public Task MultipleCrossDomainLoads_ListsUpToThreeInEvidence() => AssertParity(BuildResult(crossDomainLoads:
    [
        new CrossDomainModuleLoad("A.dll", "A", DomainCount: 2, Size: 1024),
        new CrossDomainModuleLoad("B.dll", "B", DomainCount: 2, Size: 1024),
        new CrossDomainModuleLoad("C.dll", "C", DomainCount: 2, Size: 1024),
        new CrossDomainModuleLoad("D.dll", "D", DomainCount: 2, Size: 1024),
    ]));

    [Fact]
    public Task NoAssemblyRefMismatches_EmitsNoMismatchFinding() => AssertParity(BuildResult(assemblyRefVersionMismatches: []));

    [Fact]
    public Task AssemblyRefMismatches_EmitsWarningFinding() => AssertParity(BuildResult(assemblyRefVersionMismatches:
    [
        new AssemblyRefVersionMismatch("PluginHost.dll", "Newtonsoft.Json", "9.0.0.0", "13.0.0.0"),
    ]));

    [Fact]
    public Task ThreeOrMoreVersionConflicts_EmitsCriticalFinding() => AssertParity(BuildResult(versionConflictGroups: 3));

    [Fact]
    public Task OneOrTwoVersionConflicts_EmitsWarningFinding() => AssertParity(BuildResult(versionConflictGroups: 1));

    [Fact]
    public Task NoVersionConflicts_EmitsInfoFinding() => AssertParity(BuildResult(versionConflictGroups: 0));

    [Fact]
    public Task VersionConflicts_WithDetails_ListsThemInEvidence() => AssertParity(BuildResult(
        versionConflictGroups: 2,
        conflictDetails:
        [
            new ModuleConflictGroup("Newtonsoft.Json.dll", [], ["9.0.0.0", "13.0.0.0"]),
            new ModuleConflictGroup("Serilog.dll", [], ["2.0.0.0", "2.10.0.0"]),
        ]));

    [Fact]
    public Task DynamicModulesAtOrAboveOneHundred_EmitsCriticalFinding() => AssertParity(BuildResult(totalDynamicModules: 150));

    [Fact]
    public Task DynamicModulesAtOrAboveTwenty_BelowOneHundred_EmitsWarningFinding() => AssertParity(BuildResult(totalDynamicModules: 30));

    [Fact]
    public Task DynamicModulesBelowTwenty_EmitsNoDynamicModuleFinding() => AssertParity(BuildResult(totalDynamicModules: 5));

    [Fact]
    public Task HeavyModule_AboveThreshold_EmitsWarningFinding() => AssertParity(BuildResult(
        heavyModuleWarningThresholdBytes: 100 * 1024 * 1024,
        topModulesByHeapMemory: [new ModuleHeapStats("BigModule.dll", "BigModule", UniqueTypeCount: 12, ObjectCount: 5000, TotalBytes: 200 * 1024 * 1024, LohBytes: 0, Gen2ObjectCount: 100)]));

    [Fact]
    public Task HeavyModule_BelowThreshold_EmitsInfoFinding() => AssertParity(BuildResult(
        heavyModuleWarningThresholdBytes: 200 * 1024 * 1024,
        topModulesByHeapMemory: [new ModuleHeapStats("SmallModule.dll", "SmallModule", UniqueTypeCount: 3, ObjectCount: 100, TotalBytes: 1024, LohBytes: 0, Gen2ObjectCount: 0)]));

    [Fact]
    public Task NoHeapMemoryModules_EmitsNoHeavyModuleFinding() => AssertParity(BuildResult(topModulesByHeapMemory: []));

    [Fact]
    public Task TypeDensityAnomaly_EmitsWarningFinding() => AssertParity(BuildResult(heavyTypeDensityModules:
    [
        new ModuleTypeDensity("BufferPool.dll", "BufferPool", UniqueTypeCount: 2, ObjectCount: 500, TotalBytes: 10_000_000, BytesPerType: 5_000_000),
    ]));

    [Fact]
    public Task NoTypeDensityAnomaly_EmitsNoTypeDensityFinding() => AssertParity(BuildResult(heavyTypeDensityModules: []));

    [Fact]
    public Task AnonymousModulesAtOrAboveFive_EmitsWarningFinding() => AssertParity(BuildResult(anonymousModuleCount: 7));

    [Fact]
    public Task AnonymousModulesBelowFive_EmitsNoAnonymousModuleFinding() => AssertParity(BuildResult(anonymousModuleCount: 2));

    [Fact]
    public Task UnknownIdentityDuplicates_EmitsWarningFinding() => AssertParity(BuildResult(
        unknownIdentityDuplicateModules: new HashSet<string> { "Foo.dll", "Bar.dll" }));

    [Fact]
    public Task NoUnknownIdentityDuplicates_EmitsNoUnknownIdentityFinding() => AssertParity(BuildResult(
        unknownIdentityDuplicateModules: new HashSet<string>()));

    [Fact]
    public Task AllSignalsFireTogether() => AssertParity(BuildResult(
        versionConflictGroups: 3,
        conflictDetails: [new ModuleConflictGroup("Newtonsoft.Json.dll", [], ["9.0.0.0", "13.0.0.0"])],
        heavyModuleWarningThresholdBytes: 100 * 1024 * 1024,
        topModulesByHeapMemory: [new ModuleHeapStats("BigModule.dll", "BigModule", UniqueTypeCount: 12, ObjectCount: 5000, TotalBytes: 200 * 1024 * 1024, LohBytes: 0, Gen2ObjectCount: 100)],
        heavyTypeDensityModules: [new ModuleTypeDensity("BufferPool.dll", "BufferPool", UniqueTypeCount: 2, ObjectCount: 500, TotalBytes: 10_000_000, BytesPerType: 5_000_000)],
        totalDynamicModules: 150,
        anonymousModuleCount: 7,
        crossDomainLoads: [new CrossDomainModuleLoad("PluginHost.dll", "PluginHost", DomainCount: 3, Size: 1024)],
        assemblyRefVersionMismatches: [new AssemblyRefVersionMismatch("PluginHost.dll", "Newtonsoft.Json", "9.0.0.0", "13.0.0.0")],
        unknownIdentityDuplicateModules: new HashSet<string> { "Foo.dll", "Bar.dll" }));

    private static ModuleDomainResult BuildResult(
        IReadOnlyList<CrossDomainModuleLoad>? crossDomainLoads = null,
        IReadOnlyList<AssemblyRefVersionMismatch>? assemblyRefVersionMismatches = null,
        ulong heavyModuleWarningThresholdBytes = 200 * 1024 * 1024,
        int versionConflictGroups = 0,
        IReadOnlyList<ModuleConflictGroup>? conflictDetails = null,
        int totalDynamicModules = 0,
        IReadOnlyList<ModuleHeapStats>? topModulesByHeapMemory = null,
        IReadOnlyList<ModuleTypeDensity>? heavyTypeDensityModules = null,
        int anonymousModuleCount = 0,
        IReadOnlySet<string>? unknownIdentityDuplicateModules = null) =>
        new(
            TotalModules: 120,
            DynamicModules: 0,
            UniqueModuleNames: 100,
            VersionConflictGroups: versionConflictGroups,
            ConflictingAssemblyNames: [],
            TopModulesBySize: [],
            ConflictDetails: conflictDetails ?? [],
            HeavyModuleWarningThresholdBytes: heavyModuleWarningThresholdBytes,
            UnknownIdentityDuplicateModules: unknownIdentityDuplicateModules ?? new HashSet<string>(),
            TopModulesByHeapMemory: topModulesByHeapMemory ?? [],
            HeavyTypeDensityModules: heavyTypeDensityModules ?? [],
            AnonymousModuleCount: anonymousModuleCount,
            CrossDomainModuleLoads: crossDomainLoads ?? [],
            AssemblyRefVersionMismatches: assemblyRefVersionMismatches ?? [],
            TotalDynamicModules: totalDynamicModules);

    private async Task AssertParity(ModuleDomainResult result)
    {
        IReadOnlyList<InsightFinding> oldFindings = _oldGenerator.Generate(result);
        IReadOnlyList<Observation> observations = ToObservations(result);
        IReadOnlyList<Finding> newFindings = await _newRule.SynthesizeAsync(
            new ObservationMatchSet(observations), new SynthesisContext());

        newFindings.Should().HaveCount(oldFindings.Count);

        for (int i = 0; i < oldFindings.Count; i++)
        {
            InsightFinding oldFinding = oldFindings[i];
            Finding newFinding = newFindings[i];

            newFinding.Title.Should().Be(oldFinding.Title, "finding[{0}].Title", i);
            newFinding.Narrative.Should().Be(oldFinding.Evidence, "finding[{0}].Narrative/Evidence", i);
            newFinding.Recommendation.Should().Be(oldFinding.Recommendation, "finding[{0}].Recommendation", i);
            newFinding.Severity.ToString().Should().Be(oldFinding.Severity.ToString(), "finding[{0}].Severity", i);
            newFinding.Category.Should().Be(oldFinding.Category, "finding[{0}].Category", i);
            newFinding.Analyzer.Should().Be(oldFinding.Analyzer, "finding[{0}].Analyzer", i);
            newFinding.Tags.Should().Equal(oldFinding.Tags, "finding[{0}].Tags", i);
            newFinding.MetricValue.Should().Be(oldFinding.MetricValue, "finding[{0}].MetricValue", i);
            newFinding.MetricUnit.Should().Be(oldFinding.MetricUnit, "finding[{0}].MetricUnit", i);
        }
    }

    private static IReadOnlyList<Observation> ToObservations(ModuleDomainResult result)
    {
        var capabilitiesUsed = new HashSet<Capability> { CapabilityVocabulary.DumpObjectFields, CapabilityVocabulary.HeapTypes, CapabilityVocabulary.RuntimeModules };
        var observations = new List<Observation>
        {
            new()
            {
                Id = ObservationId.NewId(),
                ObservationType = "runtime.module-population",
                Subjects = [],
                When = Point(),
                Measures = new Dictionary<string, Measure>
                {
                    ["total.count"] = new Measure(result.TotalModules, MeasureUnit.Count, MeasureSemantics.Absolute),
                    ["dynamic.count"] = new Measure(result.DynamicModules, MeasureUnit.Count, MeasureSemantics.Absolute),
                    ["version-conflict.count"] = new Measure(result.VersionConflictGroups, MeasureUnit.Count, MeasureSemantics.Absolute),
                    ["heavy-module-warning.threshold-bytes"] = new Measure(result.HeavyModuleWarningThresholdBytes, MeasureUnit.Bytes, MeasureSemantics.Absolute),
                    ["total-dynamic.count"] = new Measure(result.TotalDynamicModules, MeasureUnit.Count, MeasureSemantics.Absolute),
                    ["total-domains.count"] = new Measure(result.TotalDomains, MeasureUnit.Count, MeasureSemantics.Absolute),
                    ["anonymous-module.count"] = new Measure(result.AnonymousModuleCount, MeasureUnit.Count, MeasureSemantics.Absolute),
                    ["unknown-identity-duplicate.count"] = new Measure(result.UnknownIdentityDuplicateModules.Count, MeasureUnit.Count, MeasureSemantics.Absolute),
                },
                Provenance = Provenance(capabilitiesUsed),
                Confidence = 1.0,
            },
        };

        foreach (ModuleConflictGroup group in result.ConflictDetails)
        {
            observations.Add(new Observation
            {
                Id = ObservationId.NewId(),
                ObservationType = "runtime.module-conflict-group",
                Subjects = [new ModuleRef { SimpleName = group.ModuleName, Fidelity = MatchFidelity.Exact }],
                When = Point(),
                Measures = new Dictionary<string, Measure>(),
                Provenance = Provenance(capabilitiesUsed),
                Confidence = 1.0,
                Evidence = [new EvidenceRef { Artifact = TestArtifact, Locator = "versions", Description = string.Join(", ", group.Versions) }],
            });
        }

        foreach (ModuleHeapStats module in result.TopModulesByHeapMemory ?? [])
        {
            observations.Add(new Observation
            {
                Id = ObservationId.NewId(),
                ObservationType = "runtime.module-heap-footprint",
                Subjects = [new ModuleRef { SimpleName = module.ModuleName, Fidelity = MatchFidelity.Exact }],
                When = Point(),
                Measures = new Dictionary<string, Measure>
                {
                    ["unique-type.count"] = new Measure(module.UniqueTypeCount, MeasureUnit.Count, MeasureSemantics.Absolute),
                    ["object.count"] = new Measure(module.ObjectCount, MeasureUnit.Count, MeasureSemantics.Absolute),
                    ["shallow.bytes"] = new Measure(module.TotalBytes, MeasureUnit.Bytes, MeasureSemantics.Absolute),
                    ["loh.bytes"] = new Measure(module.LohBytes, MeasureUnit.Bytes, MeasureSemantics.Absolute),
                    ["gen2.object-count"] = new Measure(module.Gen2ObjectCount, MeasureUnit.Count, MeasureSemantics.Absolute),
                },
                Provenance = Provenance(capabilitiesUsed),
                Confidence = 1.0,
            });
        }

        foreach (ModuleTypeDensity density in result.HeavyTypeDensityModules ?? [])
        {
            observations.Add(new Observation
            {
                Id = ObservationId.NewId(),
                ObservationType = "runtime.module-type-density",
                Subjects = [new ModuleRef { SimpleName = density.ModuleName, Fidelity = MatchFidelity.Exact }],
                When = Point(),
                Measures = new Dictionary<string, Measure>
                {
                    ["unique-type.count"] = new Measure(density.UniqueTypeCount, MeasureUnit.Count, MeasureSemantics.Absolute),
                    ["shallow.bytes"] = new Measure(density.TotalBytes, MeasureUnit.Bytes, MeasureSemantics.Absolute),
                    ["bytes-per-type.bytes"] = new Measure(density.BytesPerType, MeasureUnit.Bytes, MeasureSemantics.Absolute),
                },
                Provenance = Provenance(capabilitiesUsed),
                Confidence = 1.0,
            });
        }

        foreach (CrossDomainModuleLoad load in result.CrossDomainModuleLoads ?? [])
        {
            observations.Add(new Observation
            {
                Id = ObservationId.NewId(),
                ObservationType = "runtime.module-cross-domain-load",
                Subjects = [new ModuleRef { SimpleName = load.ModuleName, Fidelity = MatchFidelity.Exact }],
                When = Point(),
                Measures = new Dictionary<string, Measure>
                {
                    ["domain.count"] = new Measure(load.DomainCount, MeasureUnit.Count, MeasureSemantics.Absolute),
                    ["shallow.bytes"] = new Measure(load.Size, MeasureUnit.Bytes, MeasureSemantics.Absolute),
                },
                Provenance = Provenance(capabilitiesUsed),
                Confidence = 1.0,
            });
        }

        foreach (AssemblyRefVersionMismatch mismatch in result.AssemblyRefVersionMismatches ?? [])
        {
            observations.Add(new Observation
            {
                Id = ObservationId.NewId(),
                ObservationType = "runtime.module-assemblyref-mismatch",
                Subjects = [new ModuleRef { SimpleName = mismatch.RequiringModule, Fidelity = MatchFidelity.Exact }],
                When = Point(),
                Measures = new Dictionary<string, Measure>(),
                Provenance = Provenance(capabilitiesUsed),
                Confidence = 1.0,
                Evidence = [new EvidenceRef
                {
                    Artifact = TestArtifact,
                    Locator = "required-version",
                    Description = $"{mismatch.RequiredAssemblyName} {mismatch.RequiredVersion} (loaded: {mismatch.LoadedVersions})",
                }],
            });
        }

        foreach (string moduleName in result.UnknownIdentityDuplicateModules)
        {
            observations.Add(new Observation
            {
                Id = ObservationId.NewId(),
                ObservationType = "runtime.module-unknown-identity",
                Subjects = [new ModuleRef { SimpleName = moduleName, Fidelity = MatchFidelity.Exact }],
                When = Point(),
                Measures = new Dictionary<string, Measure>(),
                Provenance = Provenance(capabilitiesUsed),
                Confidence = 1.0,
            });
        }

        return observations;
    }

    private static TemporalExtent Point() => new() { Kind = TemporalKind.Point, Start = new TimeAnchor { Confidence = AnchorConfidence.Unknown } };

    private static Provenance Provenance(HashSet<Capability> capabilitiesUsed) => new()
    {
        Artifact = TestArtifact,
        AnalyzerKey = "Module Analysis",
        CapabilitiesUsed = capabilitiesUsed,
        Fidelity = FidelityLevel.Full,
    };
}
