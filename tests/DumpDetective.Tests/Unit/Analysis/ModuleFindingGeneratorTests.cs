using DumpDetective.Reporting.FindingGenerators;
using DumpDetective.Analysis.Models;
using DumpDetective.Core.Enums;

using FluentAssertions;

using Xunit;

namespace DumpDetective.Tests.Unit.Analysis;

public sealed class ModuleFindingGeneratorTests
{
    [Fact]
    public void Generate_NoCrossDomainLoads_EmitsNoCrossDomainFinding()
    {
        var gen = new ModuleFindingGenerator();
        var result = BuildResult(crossDomainLoads: []);

        var findings = gen.Generate(result);

        findings.Should().NotContain(f => f.Tags.Contains("cross-domain"));
    }

    [Fact]
    public void Generate_CrossDomainLoadsBelowThreshold_EmitsInfoFinding()
    {
        var gen = new ModuleFindingGenerator();
        var result = BuildResult(crossDomainLoads:
        [
            new CrossDomainModuleLoad("PluginHost.dll", "PluginHost, Version=1.0.0.0", DomainCount: 2, Size: 1024)
        ]);

        var findings = gen.Generate(result);

        var finding = findings.Should().ContainSingle(f => f.Tags.Contains("cross-domain")).Subject;
        finding.Severity.Should().Be(FindingSeverity.Info);
        finding.Evidence.Should().Contain("PluginHost.dll");
        finding.Evidence.Should().Contain("2 domains");
    }

    [Fact]
    public void Generate_WidelyLoadedModule_EmitsWarningFinding()
    {
        var gen = new ModuleFindingGenerator();
        var result = BuildResult(crossDomainLoads:
        [
            new CrossDomainModuleLoad("PluginHost.dll", "PluginHost, Version=1.0.0.0", DomainCount: 3, Size: 1024)
        ]);

        var findings = gen.Generate(result);

        var finding = findings.Should().ContainSingle(f => f.Tags.Contains("cross-domain")).Subject;
        finding.Severity.Should().Be(FindingSeverity.Warning);
    }

    [Fact]
    public void Generate_HeavyCrossDomainModule_EmitsWarningFinding()
    {
        var gen = new ModuleFindingGenerator();
        var result = BuildResult(
            heavyModuleWarningThresholdBytes: 200 * 1024 * 1024,
            crossDomainLoads:
            [
                new CrossDomainModuleLoad("PluginHost.dll", "PluginHost, Version=1.0.0.0", DomainCount: 2, Size: 300 * 1024 * 1024)
            ]);

        var findings = gen.Generate(result);

        var finding = findings.Should().ContainSingle(f => f.Tags.Contains("cross-domain")).Subject;
        finding.Severity.Should().Be(FindingSeverity.Warning);
    }

    [Fact]
    public void Generate_NoAssemblyRefMismatches_EmitsNoMismatchFinding()
    {
        var gen = new ModuleFindingGenerator();
        var result = BuildResult(assemblyRefVersionMismatches: []);

        var findings = gen.Generate(result);

        findings.Should().NotContain(f => f.Tags.Contains("assembly-ref"));
    }

    [Fact]
    public void Generate_AssemblyRefMismatches_EmitsWarningFinding()
    {
        var gen = new ModuleFindingGenerator();
        var result = BuildResult(assemblyRefVersionMismatches:
        [
            new AssemblyRefVersionMismatch("PluginHost.dll", "Newtonsoft.Json", "9.0.0.0", "13.0.0.0")
        ]);

        var findings = gen.Generate(result);

        var finding = findings.Should().ContainSingle(f => f.Tags.Contains("assembly-ref")).Subject;
        finding.Severity.Should().Be(FindingSeverity.Warning);
        finding.Evidence.Should().Contain("Newtonsoft.Json");
        finding.Evidence.Should().Contain("9.0.0.0");
        finding.Evidence.Should().Contain("13.0.0.0");
    }

    private static ModuleDomainResult BuildResult(
        IReadOnlyList<CrossDomainModuleLoad>? crossDomainLoads = null,
        IReadOnlyList<AssemblyRefVersionMismatch>? assemblyRefVersionMismatches = null,
        ulong heavyModuleWarningThresholdBytes = 200 * 1024 * 1024) =>
        new(
            TotalModules: 120,
            DynamicModules: 0,
            UniqueModuleNames: 100,
            VersionConflictGroups: 0,
            ConflictingAssemblyNames: [],
            TopModulesBySize: [],
            ConflictDetails: [],
            HeavyModuleWarningThresholdBytes: heavyModuleWarningThresholdBytes,
            UnknownIdentityDuplicateModules: new HashSet<string>(),
            CrossDomainModuleLoads: crossDomainLoads ?? [],
            AssemblyRefVersionMismatches: assemblyRefVersionMismatches ?? []);
}
