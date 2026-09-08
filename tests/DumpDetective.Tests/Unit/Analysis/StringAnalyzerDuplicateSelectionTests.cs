using System.Reflection;

using DumpDetective.Analysis.Analyzers;
using DumpDetective.Core.Models;

using FluentAssertions;

using Xunit;

namespace DumpDetective.Tests.Unit.Analysis;

/// <summary>
/// P2-1: <see cref="StringAnalyzer"/>'s duplicate-selection filter must treat a pattern
/// occurring exactly <c>MinDuplicateStringCount</c> times as a duplicate (inclusive lower
/// bound), matching the documented "minimum occurrence count" semantics of the option.
/// </summary>
public sealed class StringAnalyzerDuplicateSelectionTests
{
    private static readonly Type FingerprintType =
        typeof(StringAnalyzer).GetNestedType("StringFingerprint", BindingFlags.NonPublic)!;

    private static readonly Type StatsDictType =
        typeof(Dictionary<,>).MakeGenericType(FingerprintType, typeof(StringLeakInfo));

    private static readonly MethodInfo SelectDuplicatesMethod =
        typeof(StringAnalyzer).GetMethod("SelectDuplicates", BindingFlags.NonPublic | BindingFlags.Static)!;

    [Fact]
    public void SelectDuplicates_IncludesPattern_WhenCountEqualsMinCount()
    {
        var dict = MakeStatsDict(
            (MakeFingerprint(0xAAAA, 10, 'a', 'z'), new StringLeakInfo { Count = 10, TotalSize = 1000 }));

        (List<StringLeakInfo> duplicates, int patternCount, ulong wastedBytes) = InvokeSelectDuplicates(dict, minCount: 10);

        duplicates.Should().ContainSingle();
        patternCount.Should().Be(1);
        wastedBytes.Should().Be(900);
    }

    [Fact]
    public void SelectDuplicates_ExcludesPattern_WhenCountBelowMinCount()
    {
        var dict = MakeStatsDict(
            (MakeFingerprint(0xAAAA, 10, 'a', 'z'), new StringLeakInfo { Count = 9, TotalSize = 900 }));

        (List<StringLeakInfo> duplicates, int patternCount, ulong wastedBytes) = InvokeSelectDuplicates(dict, minCount: 10);

        duplicates.Should().BeEmpty();
        patternCount.Should().Be(0);
        wastedBytes.Should().Be(0);
    }

    [Fact]
    public void SelectDuplicates_IncludesPattern_WhenCountAboveMinCount()
    {
        var dict = MakeStatsDict(
            (MakeFingerprint(0xAAAA, 10, 'a', 'z'), new StringLeakInfo { Count = 11, TotalSize = 1100 }));

        (List<StringLeakInfo> duplicates, int patternCount, ulong wastedBytes) = InvokeSelectDuplicates(dict, minCount: 10);

        duplicates.Should().ContainSingle();
        patternCount.Should().Be(1);
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    private static object MakeFingerprint(ulong hash, int length, char first, char last) =>
        Activator.CreateInstance(FingerprintType, hash, length, first, last)!;

    private static object MakeStatsDict(params (object key, StringLeakInfo val)[] entries)
    {
        var dict = (System.Collections.IDictionary)Activator.CreateInstance(StatsDictType)!;
        foreach (var (key, val) in entries)
            dict[key] = val;
        return dict;
    }

    private static (List<StringLeakInfo> Duplicates, int PatternCount, ulong WastedBytes) InvokeSelectDuplicates(
        object statsDict, int minCount)
    {
        object values = StatsDictType.GetProperty("Values")!.GetValue(statsDict)!;
        object?[] args = [values, minCount, null, null];
        var duplicates = (List<StringLeakInfo>)SelectDuplicatesMethod.Invoke(null, args)!;
        return (duplicates, (int)args[2]!, (ulong)args[3]!);
    }
}
