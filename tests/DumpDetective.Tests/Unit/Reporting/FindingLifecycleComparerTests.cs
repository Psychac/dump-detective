using DumpDetective.Sdk.Synthesis;
using System.Collections.Generic;

using DumpDetective.Core.Enums;
using DumpDetective.Core.Models;
using DumpDetective.Reporting.Trend;

using FluentAssertions;

using Xunit;

namespace DumpDetective.Tests.Unit.Reporting;

/// <summary>
/// No test file existed for this comparer before it grew a second responsibility
/// (<see cref="FindingSeverityChange"/>) — see docs/refactor/modularity/phase-5-observations-synthesis.md
/// ## Work step 4. Covers the union-of-fingerprints classification (new/persistent/resolved) plus
/// the escalation/de-escalation split severity changes now carry.
/// </summary>
public sealed class FindingLifecycleComparerTests
{
    private static Claim MakeFinding(string fingerprint, Severity severity) =>
        ClaimFactory.Create(
            analyzer: "TestAnalyzer",
            category: "Cat",
            severity: severity,
            title: "Title " + fingerprint,
            narrative: "Evidence",
            recommendation: "Recommendation",
            tags: [],
            fingerprint: fingerprint);

    private static AnalysisSnapshot MakeSnapshot(int index, params Claim[] findings) =>
        new(index, $"dump{index}.dmp", [], findings, new Dictionary<string, AnalyzerDomainResult>(), System.DateTime.UtcNow);

    [Fact]
    public void Compare_ClassifiesByFingerprintPresence()
    {
        Claim onlyInBaseline = MakeFinding("resolved-one", Severity.Warning);
        Claim unchanged = MakeFinding("persistent-one", Severity.Warning);
        Claim onlyInCurrentBaselineVersion = unchanged;
        Claim onlyInCurrent = MakeFinding("new-one", Severity.Critical);

        AnalysisSnapshot baseline = MakeSnapshot(0, onlyInBaseline, onlyInCurrentBaselineVersion);
        AnalysisSnapshot current = MakeSnapshot(1, unchanged, onlyInCurrent);

        FindingLifecycleResult result = FindingLifecycleComparer.Compare(baseline, current);

        result.NewFindings.Should().ContainSingle(f => f.Fingerprint == "new-one");
        result.PersistentFindings.Should().ContainSingle(f => f.Fingerprint == "persistent-one");
        result.ResolvedFindings.Should().ContainSingle(f => f.Fingerprint == "resolved-one");
    }

    [Fact]
    public void Compare_RecordsSeverityChange_OnEscalation()
    {
        AnalysisSnapshot baseline = MakeSnapshot(0, MakeFinding("fp", Severity.Warning));
        AnalysisSnapshot current = MakeSnapshot(1, MakeFinding("fp", Severity.Critical));

        FindingLifecycleResult result = FindingLifecycleComparer.Compare(baseline, current);

        FindingSeverityChange change = result.SeverityChanges.Should().ContainSingle().Subject;
        change.BaselineSeverity.Should().Be(Severity.Warning);
        change.Current.Severity.Should().Be(Severity.Critical);
        change.IsEscalation.Should().BeTrue();
    }

    [Fact]
    public void Compare_RecordsSeverityChange_OnDeescalation_ButNotAsEscalation()
    {
        AnalysisSnapshot baseline = MakeSnapshot(0, MakeFinding("fp", Severity.Critical));
        AnalysisSnapshot current = MakeSnapshot(1, MakeFinding("fp", Severity.Info));

        FindingLifecycleResult result = FindingLifecycleComparer.Compare(baseline, current);

        FindingSeverityChange change = result.SeverityChanges.Should().ContainSingle().Subject;
        change.IsEscalation.Should().BeFalse();
    }

    [Fact]
    public void Compare_NoSeverityChange_WhenPersistentFindingUnchanged()
    {
        AnalysisSnapshot baseline = MakeSnapshot(0, MakeFinding("fp", Severity.Warning));
        AnalysisSnapshot current = MakeSnapshot(1, MakeFinding("fp", Severity.Warning));

        FindingLifecycleResult result = FindingLifecycleComparer.Compare(baseline, current);

        result.PersistentFindings.Should().ContainSingle();
        result.SeverityChanges.Should().BeEmpty();
    }

    [Fact]
    public void Compare_NoSeverityChange_ForNewOrResolvedFindings()
    {
        AnalysisSnapshot baseline = MakeSnapshot(0, MakeFinding("resolved", Severity.Critical));
        AnalysisSnapshot current = MakeSnapshot(1, MakeFinding("new", Severity.Critical));

        FindingLifecycleResult result = FindingLifecycleComparer.Compare(baseline, current);

        result.SeverityChanges.Should().BeEmpty();
    }
}
