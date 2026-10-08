using DumpDetective.Analysis.Pipeline;
using DumpDetective.Analysis.Analyzers.Runtime;
using DumpDetective.Analysis.Models;
using DumpDetective.Analysis.SdkBridge;
using DumpDetective.Core.Options;
using DumpDetective.Sdk.Analysis;
using DumpDetective.Sdk.Artifacts;
using DumpDetective.Sdk.Identity;
using DumpDetective.Sdk.Observations;

using FluentAssertions;

using Xunit;

namespace DumpDetective.Tests.Unit.Analysis;

/// <summary>
/// Characterization tests for the Tier-1+Tier-2 retyping of <see cref="ModuleAnalyzer"/>
/// (docs/refactor/modularity/phase-1-full-extraction-retyping-plan.md) — no pre-existing per-analyzer
/// unit test coverage existed before this (only the real-dump
/// <c>ModuleAnalyzerTopTypesRealDumpTests</c>, updated separately to use
/// <see cref="ModuleAnalyzer"/>). Proves: module inventory/dedup, version-conflict vs.
/// unknown-identity-duplicate classification, AssemblyRef mismatch detection, AppDomain/cross-domain
/// analysis, and the <c>ModuleName</c>-grouped heap-footprint aggregation (including the two-pass
/// heavy-module gate fix found via the real-dump test).
/// </summary>
public sealed class ModuleAnalyzerRetypingCharacterizationTests
{
    [Fact]
    public async Task AnalyzeAsync_NoModules_ReturnsEmptyResult()
    {
        ModuleDomainResult result = await Analyze(new FakeRuntimeModuleQuery(), new FakeDumpObjectFieldQuery(), new FakeHeapTypeStatisticsQuery());

        result.TotalModules.Should().Be(0);
        result.TopModulesBySize.Should().BeEmpty();
    }

    [Fact]
    public async Task AnalyzeAsync_TwoDistinctModules_CountedAndDeduplicatedByAddress()
    {
        var modules = new FakeRuntimeModuleQuery();
        modules.AddModule(0x1000, "A.dll", "A, Version=1.0.0.0", domainId: 1);
        modules.AddModule(0x2000, "B.dll", "B, Version=1.0.0.0", domainId: 1);

        ModuleDomainResult result = await Analyze(modules, new FakeDumpObjectFieldQuery(), new FakeHeapTypeStatisticsQuery());

        result.TotalModules.Should().Be(2);
        result.UniqueModuleNames.Should().Be(2);
        result.VersionConflictGroups.Should().Be(0);
    }

    [Fact]
    public async Task AnalyzeAsync_SameModuleNameDifferentVersions_DetectedAsVersionConflict()
    {
        var modules = new FakeRuntimeModuleQuery();
        modules.AddModule(0x1000, "A.dll", "A, Version=1.0.0.0, Culture=neutral, PublicKeyToken=aaaaaaaaaaaaaaaa", domainId: 1);
        modules.AddModule(0x2000, "A.dll", "A, Version=2.0.0.0, Culture=neutral, PublicKeyToken=aaaaaaaaaaaaaaaa", domainId: 1);

        ModuleDomainResult result = await Analyze(modules, new FakeDumpObjectFieldQuery(), new FakeHeapTypeStatisticsQuery());

        result.VersionConflictGroups.Should().Be(1);
        result.ConflictingAssemblyNames.Should().ContainSingle().Which.Should().Be("A.dll");
        result.ConflictDetails.Should().ContainSingle().Which.Versions.Should().BeEquivalentTo(["1.0.0.0", "2.0.0.0"]);
    }

    [Fact]
    public async Task AnalyzeAsync_SameModuleNameUnknownIdentityDuplicates_NotCountedAsConflict()
    {
        // No version/public-key-token/file-hash resolvable -- duplicates of an "unknown" identity
        // are noted separately, not treated as a real version conflict.
        var modules = new FakeRuntimeModuleQuery();
        modules.AddModule(0x1000, "Dynamic.dll", "Dynamic", domainId: 1);
        modules.AddModule(0x2000, "Dynamic.dll", "Dynamic", domainId: 1);

        ModuleDomainResult result = await Analyze(modules, new FakeDumpObjectFieldQuery(), new FakeHeapTypeStatisticsQuery());

        result.VersionConflictGroups.Should().Be(0);
        result.UnknownIdentityDuplicateModules.Should().Contain("Dynamic.dll");
    }

    [Fact]
    public async Task AnalyzeAsync_ModuleWithAssemblyLoadContext_ResolvesNameViaTryReadField()
    {
        var modules = new FakeRuntimeModuleQuery();
        modules.AddModule(0x1000, "Plugin.dll", "Plugin, Version=1.0.0.0", domainId: 1);
        modules.SetAssemblyLoadContext(0x1000, alcAddress: 0x9000, isCollectible: true);

        var fields = new FakeDumpObjectFieldQuery();
        fields.SetStringField(0x9000, "_name", "PluginLoadContext");

        ModuleDomainResult result = await Analyze(modules, fields, new FakeHeapTypeStatisticsQuery());

        LoadedModuleSnapshot snapshot = result.TopModulesBySize.Should().ContainSingle().Subject;
        snapshot.HasAssemblyLoadContext.Should().BeTrue();
        snapshot.IsCollectibleAssemblyLoadContext.Should().BeTrue();
        snapshot.AssemblyLoadContextName.Should().Be("PluginLoadContext");
    }

    [Fact]
    public async Task AnalyzeAsync_AssemblyRefRequiresVersionNeverLoaded_FlaggedAsMismatch()
    {
        var modules = new FakeRuntimeModuleQuery();
        modules.AddModule(0x1000, "App.dll", "App, Version=1.0.0.0", domainId: 1);
        modules.AddModule(0x2000, "Dep.dll", "Dep, Version=1.0.0.0", domainId: 1);
        modules.SetAssemblyReferences(0x1000, [("Dep", "2.0.0.0")]);

        ModuleDomainResult result = await Analyze(modules, new FakeDumpObjectFieldQuery(), new FakeHeapTypeStatisticsQuery());

        AssemblyRefVersionMismatch mismatch = result.AssemblyRefVersionMismatches.Should().ContainSingle().Subject;
        mismatch.RequiringModule.Should().Be("App.dll");
        mismatch.RequiredAssemblyName.Should().Be("Dep");
        mismatch.RequiredVersion.Should().Be("2.0.0.0");
        mismatch.LoadedVersions.Should().Be("1.0.0.0");
    }

    [Fact]
    public async Task AnalyzeAsync_AssemblyRefRequiresLoadedVersion_NotFlagged()
    {
        var modules = new FakeRuntimeModuleQuery();
        modules.AddModule(0x1000, "App.dll", "App, Version=1.0.0.0", domainId: 1);
        modules.AddModule(0x2000, "Dep.dll", "Dep, Version=1.0.0.0", domainId: 1);
        modules.SetAssemblyReferences(0x1000, [("Dep", "1.0.0.0")]);

        ModuleDomainResult result = await Analyze(modules, new FakeDumpObjectFieldQuery(), new FakeHeapTypeStatisticsQuery());

        result.AssemblyRefVersionMismatches.Should().BeNull();
    }

    [Fact]
    public async Task AnalyzeAsync_ModuleLoadedIntoTwoNonFrameworkDomains_DetectedAsCrossDomainLoad()
    {
        var modules = new FakeRuntimeModuleQuery();
        modules.AddDomain(1, "DefaultDomain");
        modules.AddDomain(2, "PluginDomain");
        modules.AddDomainModule(1, 0x1000, "Shared.dll", "Shared, Version=1.0.0.0");
        modules.AddDomainModule(2, 0x2000, "Shared.dll", "Shared, Version=1.0.0.0");

        ModuleDomainResult result = await Analyze(modules, new FakeDumpObjectFieldQuery(), new FakeHeapTypeStatisticsQuery());

        result.CrossDomainModuleLoads.Should().ContainSingle().Which.ModuleName.Should().Be("Shared.dll");
        result.TotalDomains.Should().Be(2);
    }

    [Fact]
    public async Task AnalyzeAsync_FrameworkModuleInTwoDomains_NotDetectedAsCrossDomainLoad()
    {
        var modules = new FakeRuntimeModuleQuery();
        modules.AddDomain(1, "DefaultDomain");
        modules.AddDomain(2, "PluginDomain");
        modules.AddDomainModule(1, 0x1000, "System.Private.CoreLib.dll", "System.Private.CoreLib");
        modules.AddDomainModule(2, 0x2000, "System.Private.CoreLib.dll", "System.Private.CoreLib");

        ModuleDomainResult result = await Analyze(modules, new FakeDumpObjectFieldQuery(), new FakeHeapTypeStatisticsQuery());

        result.CrossDomainModuleLoads.Should().BeEmpty();
    }

    [Fact]
    public async Task AnalyzeAsync_DomainModuleWithDeclaredTypes_CountsLiveTypesFromHeapStatistics()
    {
        var modules = new FakeRuntimeModuleQuery();
        modules.AddDomain(1, "DefaultDomain");
        modules.AddDomainModule(1, 0x1000, "App.dll", "App, Version=1.0.0.0");
        modules.SetDeclaredTypeNames(0x1000, ["App.Widget", "App.Gadget", "App.NeverAllocated"]);

        var typeStats = new FakeHeapTypeStatisticsQuery();
        typeStats.SetType("App.Widget", instanceCount: 10, totalSize: 1000, moduleName: "App.dll");
        typeStats.SetType("App.Gadget", instanceCount: 5, totalSize: 500, moduleName: "App.dll");

        ModuleDomainResult result = await Analyze(modules, new FakeDumpObjectFieldQuery(), typeStats);

        ModuleTypeCountEntry entry = result.TopModulesByTypeCount.Should().ContainSingle().Subject;
        entry.TypeCount.Should().Be(3);
        entry.LiveTypeCount.Should().Be(2);
        entry.ObjectCount.Should().Be(15);
        entry.TotalBytes.Should().Be(1500UL);
    }

    [Fact]
    public async Task AnalyzeAsync_HeavyModule_GetsTopTypesBreakdownGatedOnModuleAggregateNotSingleTypeSize()
    {
        // Regression coverage for the bug the real-dump test caught: gating the top-types
        // breakdown on a single type's own TotalSize (instead of the module's aggregate total)
        // silently dropped the breakdown for modules whose heaviness comes from many small types.
        var typeStats = new FakeHeapTypeStatisticsQuery();
        var options = new ModuleAnalysisOptions { HeavyModuleWarningThresholdBytes = 1000 };
        for (int i = 0; i < 5; i++)
            typeStats.SetType($"App.Type{i}", instanceCount: 10, totalSize: 250, moduleName: "App.dll"); // 5 * 250 = 1250 >= 1000, no single type >= 1000

        ModuleDomainResult result = await Analyze(new FakeRuntimeModuleQuery(), new FakeDumpObjectFieldQuery(), typeStats, options);

        ModuleHeapStats heavyModule = result.TopModulesByHeapMemory.Should().ContainSingle().Subject;
        heavyModule.TotalBytes.Should().Be(1250UL);
        heavyModule.TopTypes.Should().NotBeNull();
        heavyModule.TopTypes!.Should().HaveCount(5);
    }

    [Fact]
    public async Task AnalyzeAsync_LightModule_HasNoTopTypesBreakdown()
    {
        var typeStats = new FakeHeapTypeStatisticsQuery();
        var options = new ModuleAnalysisOptions { HeavyModuleWarningThresholdBytes = 1000 };
        typeStats.SetType("App.Small", instanceCount: 1, totalSize: 10, moduleName: "App.dll");

        ModuleDomainResult result = await Analyze(new FakeRuntimeModuleQuery(), new FakeDumpObjectFieldQuery(), typeStats, options);

        ModuleHeapStats lightModule = result.TopModulesByHeapMemory.Should().ContainSingle().Subject;
        lightModule.TopTypes.Should().BeNull();
    }

    [Fact]
    public async Task AnalyzeAsync_DenseModule_DetectedAsDensityAnomaly()
    {
        var typeStats = new FakeHeapTypeStatisticsQuery();
        var options = new ModuleAnalysisOptions { DensityAnomalyMaxTypes = 2, DensityAnomalyMinBytes = 100 };
        typeStats.SetType("App.Big", instanceCount: 1, totalSize: 5000, moduleName: "App.dll");

        ModuleDomainResult result = await Analyze(new FakeRuntimeModuleQuery(), new FakeDumpObjectFieldQuery(), typeStats, options);

        result.HeavyTypeDensityModules.Should().ContainSingle().Which.ModuleName.Should().Be("App.dll");
    }

    [Fact]
    public async Task AnalyzeAsync_EmitsOneAppDomainFootprintFactPerNamedDomain()
    {
        var modules = new FakeRuntimeModuleQuery();
        modules.AddDomain(1, "DefaultDomain");
        modules.AddDomain(3, "/LM/W3SVC/1/ROOT");
        modules.AddDomain(4, "");
        modules.AddDomainModule(1, 0x1000, "A.dll", "A, Version=1.0.0.0");
        modules.AddDomainModule(3, 0x2000, "B.dll", "B, Version=1.0.0.0");
        modules.AddDomainModule(3, 0x3000, "C.dll", "C, Version=1.0.0.0");
        var sink = new CollectingObservationSink();
        DumpDetective.Sdk.Analysis.AnalysisContext context = new()
        {
            Observations = sink,
            ArtifactId = new ArtifactId("test"),
            RuntimeModules = modules,
            DumpObjectFields = new FakeDumpObjectFieldQuery(),
            HeapTypeStatistics = new FakeHeapTypeStatisticsQuery(),
            AnalyzerOptions = new ModuleAnalysisOptions(),
        };

        await new ModuleAnalyzer().AnalyzeAsync(context, CancellationToken.None);

        var domainFacts = sink.Observations.Where(o => o.ObservationType == "runtime.appdomain-footprint").ToList();
        domainFacts.Should().HaveCount(2, "the unnamed domain has no name to identify it by");
        domainFacts.Should().OnlyContain(o => o.Subjects.Count == 1 && o.Subjects[0] is AppDomainRef);
        domainFacts.Single(o => o.Subjects[0].JoinKey == "DefaultDomain").Measures["module.count"].Value.Should().Be(1);
        domainFacts.Single(o => o.Subjects[0].JoinKey == "/LM/W3SVC/1/ROOT").Measures["module.count"].Value.Should().Be(2);
    }

    private static async Task<ModuleDomainResult> Analyze(
        FakeRuntimeModuleQuery modules, FakeDumpObjectFieldQuery fields, FakeHeapTypeStatisticsQuery typeStats,
        ModuleAnalysisOptions? options = null)
    {
        ModuleAnalyzer analyzer = new();
        DumpDetective.Sdk.Analysis.AnalysisContext context = new() {
            Observations = NoOpObservationSink.Instance,
            ArtifactId = new ArtifactId("test"),
            RuntimeModules = modules,
            DumpObjectFields = fields,
            HeapTypeStatistics = typeStats,
            AnalyzerOptions = options ?? new ModuleAnalysisOptions(),
        };

        await analyzer.AnalyzeAsync(context, CancellationToken.None);
        return (ModuleDomainResult)analyzer.LastResult!;
    }

    private sealed class FakeRuntimeModuleQuery : IRuntimeModuleQuery
    {
        private readonly List<RuntimeModuleInfo> _modules = new();
        private readonly List<AppDomainInfo> _domains = new();
        private readonly Dictionary<int, List<RuntimeModuleInfo>> _modulesByDomain = new();
        private readonly Dictionary<ulong, (ulong Alc, bool Collectible)> _alc = new();
        private readonly Dictionary<ulong, IReadOnlyList<string>> _declaredTypes = new();
        private readonly Dictionary<ulong, IReadOnlyList<(string, string)>> _assemblyRefs = new();

        public void AddModule(ulong address, string name, string assemblyName, int domainId) =>
            _modules.Add(new RuntimeModuleInfo(address, name, $"C:\\{name}", assemblyName, Size: 1000, IsDynamic: false, IsPEFile: true, domainId));

        public void AddDomain(int domainId, string name) => _domains.Add(new AppDomainInfo(domainId, name, Address: (ulong)domainId * 0x10000));

        public void AddDomainModule(int domainId, ulong address, string name, string assemblyName)
        {
            if (!_modulesByDomain.TryGetValue(domainId, out List<RuntimeModuleInfo>? list))
                _modulesByDomain[domainId] = list = new List<RuntimeModuleInfo>();
            list.Add(new RuntimeModuleInfo(address, name, $"C:\\{name}", assemblyName, Size: 1000, IsDynamic: false, IsPEFile: true, domainId));
        }

        public void SetAssemblyLoadContext(ulong moduleAddress, ulong alcAddress, bool isCollectible) =>
            _alc[moduleAddress] = (alcAddress, isCollectible);

        public void SetDeclaredTypeNames(ulong moduleAddress, IReadOnlyList<string> names) => _declaredTypes[moduleAddress] = names;

        public void SetAssemblyReferences(ulong moduleAddress, IReadOnlyList<(string Name, string Version)> refs) => _assemblyRefs[moduleAddress] = refs;

        public IEnumerable<RuntimeModuleInfo> EnumerateModules() => _modules;
        public IEnumerable<AppDomainInfo> EnumerateAppDomains() => _domains;

        public IEnumerable<RuntimeModuleInfo> EnumerateModules(AppDomainInfo domain) =>
            _modulesByDomain.TryGetValue(domain.DomainId, out List<RuntimeModuleInfo>? list) ? list : [];

        public IEnumerable<string> EnumerateDeclaredTypeNames(ulong moduleAddress) =>
            _declaredTypes.TryGetValue(moduleAddress, out IReadOnlyList<string>? names) ? names : [];

        public bool TryResolveAssemblyLoadContext(ulong moduleAddress, out ulong alcAddress, out bool isCollectible)
        {
            if (_alc.TryGetValue(moduleAddress, out (ulong Alc, bool Collectible) v))
            {
                alcAddress = v.Alc;
                isCollectible = v.Collectible;
                return true;
            }
            alcAddress = 0;
            isCollectible = false;
            return false;
        }

        public IReadOnlyList<(string Name, string Version)> GetAssemblyReferences(ulong moduleAddress) =>
            _assemblyRefs.TryGetValue(moduleAddress, out IReadOnlyList<(string, string)>? refs) ? refs : [];
    }

    private sealed class FakeDumpObjectFieldQuery : IDumpObjectFieldQuery
    {
        private readonly Dictionary<(ulong, string), FieldValue> _fields = new();

        public void SetStringField(ulong address, string fieldName, string value) => _fields[(address, fieldName)] = FieldValue.ForString(value);

        public bool TryReadField(ulong address, string fieldName, out FieldValue value) =>
            _fields.TryGetValue((address, fieldName), out value);

        public bool TryGetStringValue(ulong address, out string? value) { value = null; return false; }
        public bool TryGetStringValue(ulong address, int maxLength, out string? value) { value = null; return false; }
        public bool TryGetFieldTypeName(ulong address, string fieldName, out string? typeName) { typeName = null; return false; }
        public IReadOnlyList<(string Name, ulong TargetAddress)> EnumerateObjectReferenceFields(ulong address) => [];
        public IReadOnlyList<InstanceFieldDescriptor> EnumerateInstanceFields(ulong address) => [];
        public IReadOnlyList<ThreadStackFrameRef> EnumerateExceptionStackTrace(ulong exceptionAddress, int maxFrames) => [];

        public bool HasDelegateField(ulong address) => false;

        public bool TryReadNestedStructField(ulong address, string outerFieldName, string innerFieldName, out FieldValue value)
        {
            value = FieldValue.Null;
            return false;
        }
    }

    private sealed class FakeHeapTypeStatisticsQuery : IHeapTypeStatisticsQuery
    {
        private readonly Dictionary<string, HeapTypeStatistics> _stats = new(StringComparer.Ordinal);

        public void SetType(string typeName, long instanceCount, ulong totalSize, string moduleName) =>
            _stats[typeName] = new HeapTypeStatistics(
                SdkTypeRefFactory.FromName(typeName, null),
                InstanceCount: instanceCount,
                TotalSize: totalSize,
                LohCount: 0,
                LohSize: 0,
                Gen0Count: 0,
                Gen1Count: 0,
                Gen2Count: 0,
                Gen2TotalSize: 0,
                IsFinalizableType: false,
                ModuleName: moduleName);

        public IReadOnlyDictionary<string, HeapTypeStatistics> GetTypeStatistics() => _stats;
        public bool HasExactGenerationData => true;
        public bool HasTypeShapeData => true;
        public (ulong Gen0Bytes, ulong Gen1Bytes, ulong Gen2Bytes) GetExactGenerationByteTotals() => (0, 0, 0);
        public long? ExactObjectCount => null;
        public ulong GetTotalIndexedBytes() => 0;
        public IReadOnlyList<TypeRef>? TryGetDistinctTypes() => null;
        public IReadOnlyList<long>? TryGetGlobalSizeBuckets() => null;
    }
}
