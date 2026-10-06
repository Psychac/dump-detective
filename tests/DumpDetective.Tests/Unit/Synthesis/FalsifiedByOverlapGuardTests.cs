using System.Text.Json;
using System.Text.RegularExpressions;
using DumpDetective.Tests.Golden;
using FluentAssertions;
using Xunit;

namespace DumpDetective.Tests.Unit.Synthesis;

/// <summary>
/// M1 step 3's Guard 1: two rules whose <c>falsifiedBy</c> text is near-identical are restating one test, which is the
/// padding the plan names as its execution risk. A ratchet: the overlaps that exist today are listed in
/// <c>Golden/KnownFalsifierOverlaps.txt</c>; a new overlap fails, and so does a listed pair that no longer overlaps.
/// </summary>
public sealed partial class FalsifiedByOverlapGuardTests
{
    private const double OverlapThreshold = 0.9;
    private const string KnownOverlapsFile = "KnownFalsifierOverlaps.txt";

    [Fact]
    public void NoNewRulePairStatesTheSameFalsifyingTest_AndNoListedPairIsStale()
    {
        HashSet<string> current = FindOverlappingRulePairs();
        HashSet<string> known = ReadKnownOverlaps();

        current.Except(known).Should().BeEmpty(
            "these rule pairs now share near-identical falsifiedBy text: give each claim its own falsifying test");
        known.Except(current).Should().BeEmpty(
            $"these pairs no longer overlap: delete them from {KnownOverlapsFile}");
    }

    private static HashSet<string> ReadKnownOverlaps()
    {
        string path = Path.Combine(RuleShapeGolden.FindGoldenDirectory(), "..", KnownOverlapsFile);
        var pairs = new HashSet<string>(StringComparer.Ordinal);
        foreach (string line in File.ReadAllLines(path))
        {
            if (line.Length > 0 && line[0] != '#')
                pairs.Add(line.Trim());
        }

        return pairs;
    }

    private static HashSet<string> FindOverlappingRulePairs()
    {
        List<(string Rule, HashSet<string> Tokens)> tests = CollectNormalizedTests();
        var pairs = new HashSet<string>(StringComparer.Ordinal);

        for (int i = 0; i < tests.Count; i++)
        {
            for (int j = i + 1; j < tests.Count; j++)
            {
                if (tests[i].Rule == tests[j].Rule)
                    continue;
                if (Jaccard(tests[i].Tokens, tests[j].Tokens) >= OverlapThreshold)
                    pairs.Add(PairKey(tests[i].Rule, tests[j].Rule));
            }
        }

        return pairs;
    }

    private static List<(string Rule, HashSet<string> Tokens)> CollectNormalizedTests()
    {
        var tests = new List<(string, HashSet<string>)>();
        foreach (string path in Directory.GetFiles(RuleShapeGolden.FindGoldenDirectory(), "*.golden"))
        {
            string rule = Path.GetFileNameWithoutExtension(path);
            var seenInRule = new HashSet<string>(StringComparer.Ordinal);
            using JsonDocument golden = JsonDocument.Parse(string.Join('\n', File.ReadLines(path).Skip(2)));

            foreach (JsonElement scenario in golden.RootElement.EnumerateArray())
            {
                foreach (JsonElement claim in scenario.GetProperty("claims").EnumerateArray())
                {
                    if (!claim.TryGetProperty("falsifiedBy", out JsonElement falsifiedBy))
                        continue;

                    foreach (JsonElement test in falsifiedBy.EnumerateArray())
                    {
                        string normalized = Normalize(test.GetProperty("test").GetString() ?? "");
                        if (seenInRule.Add(normalized))
                            tests.Add((rule, Tokenize(normalized)));
                    }
                }
            }
        }

        return tests;
    }

    // Numbers and quoted names are what a rule fills in per scenario; masking them compares the template, not the data.
    private static string Normalize(string test)
        => NumberPattern().Replace(QuotedNamePattern().Replace(test.ToLowerInvariant(), "_"), "#");

    private static HashSet<string> Tokenize(string normalized)
    {
        var tokens = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match token in TokenPattern().Matches(normalized))
            tokens.Add(token.Value);
        return tokens;
    }

    private static double Jaccard(HashSet<string> left, HashSet<string> right)
    {
        if (left.Count == 0 || right.Count == 0)
            return 0;

        int shared = 0;
        foreach (string token in left)
        {
            if (right.Contains(token))
                shared++;
        }

        return (double)shared / (left.Count + right.Count - shared);
    }

    private static string PairKey(string first, string second)
        => string.CompareOrdinal(first, second) <= 0 ? $"{first} | {second}" : $"{second} | {first}";

    [GeneratedRegex(@"'[^']*'")]
    private static partial Regex QuotedNamePattern();

    [GeneratedRegex(@"\d[\d,\.]*")]
    private static partial Regex NumberPattern();

    [GeneratedRegex(@"[a-z!_#\-]+")]
    private static partial Regex TokenPattern();
}
