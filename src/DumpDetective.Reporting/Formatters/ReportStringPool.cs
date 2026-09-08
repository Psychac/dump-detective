using DumpDetective.Reporting.Models;

namespace DumpDetective.Reporting.Formatters;

/// <summary>
/// Cross-table string interning for the HTML payload
/// (docs/refactor/report-payload-size-reduction-design.md, F1). Compact-table string cells
/// repeat the same type names, categories, and booleans across many tables — e.g.
/// "System.Data.DataColumn" can appear tens of thousands of times across eight or more tables
/// in a large dump's report. This collapses each distinct string worth pooling into a single
/// payload-level entry and rewrites qualifying cells as an integer index. This is unambiguous
/// on the wire: a cell only ever becomes an index if its original value was a JSON string, so a
/// JSON number in that same cell position can only mean "pool index", never a coincidentally
/// numeric-looking literal.
/// </summary>
internal static class ReportStringPool
{
    /// <summary>
    /// Builds a pool from every string cell across <paramref name="domains"/>' and
    /// <paramref name="trendAnalyzerSections"/>' compact tables, returning new section graphs
    /// with pooled cells replaced by their index. The inputs are never mutated — <c>Render()</c>
    /// must stay safe to call more than once on the same document (existing golden tests rely
    /// on this). Returns the original references unchanged, with a null pool, when nothing in
    /// the document qualifies for pooling.
    /// </summary>
    public static (
        IReadOnlyList<ReportDomainSection>? Domains,
        IReadOnlyList<AnalyzerDetailSection>? TrendAnalyzerSections,
        IReadOnlyList<string>? Pool)
        Apply(
            IReadOnlyList<ReportDomainSection>? domains,
            IReadOnlyList<AnalyzerDetailSection>? trendAnalyzerSections)
    {
        var frequency = new Dictionary<string, int>(StringComparer.Ordinal);
        if (domains != null)
            foreach (ReportDomainSection domain in domains)
                CountSections(domain.Sections, frequency);
        if (trendAnalyzerSections != null)
            CountSections(trendAnalyzerSections, frequency);

        if (frequency.Count == 0)
            return (domains, trendAnalyzerSections, null);

        IReadOnlyList<string> pool = BuildPool(frequency, out Dictionary<string, int> index);
        if (pool.Count == 0)
            return (domains, trendAnalyzerSections, null);

        IReadOnlyList<ReportDomainSection>? newDomains = domains == null ? null : RewriteDomains(domains, index);
        IReadOnlyList<AnalyzerDetailSection>? newTrendSections =
            trendAnalyzerSections == null ? null : RewriteSections(trendAnalyzerSections, index);
        return (newDomains, newTrendSections, pool);
    }

    private static void CountSections(IReadOnlyList<AnalyzerDetailSection> sections, Dictionary<string, int> frequency)
    {
        foreach (AnalyzerDetailSection section in sections)
        {
            if (section.CompactTables == null) continue;
            foreach (CompactTable table in section.CompactTables)
                foreach (CompactRow row in table.Rows)
                    foreach (object? cell in row.Values)
                        if (cell is string s)
                            frequency[s] = frequency.TryGetValue(s, out int count) ? count + 1 : 1;
        }
    }

    private static IReadOnlyList<ReportDomainSection> RewriteDomains(
        IReadOnlyList<ReportDomainSection> domains, Dictionary<string, int> index)
    {
        var result = new List<ReportDomainSection>(domains.Count);
        foreach (ReportDomainSection domain in domains)
            result.Add(domain with { Sections = RewriteSections(domain.Sections, index) });
        return result;
    }

    private static IReadOnlyList<AnalyzerDetailSection> RewriteSections(
        IReadOnlyList<AnalyzerDetailSection> sections, Dictionary<string, int> index)
    {
        var result = new List<AnalyzerDetailSection>(sections.Count);
        foreach (AnalyzerDetailSection section in sections)
        {
            if (section.CompactTables == null || section.CompactTables.Count == 0)
            {
                result.Add(section);
                continue;
            }
            result.Add(section with { CompactTables = RewriteTables(section.CompactTables, index) });
        }
        return result;
    }

    private static IReadOnlyList<CompactTable> RewriteTables(IReadOnlyList<CompactTable> tables, Dictionary<string, int> index)
    {
        var result = new List<CompactTable>(tables.Count);
        foreach (CompactTable table in tables)
            result.Add(table with { Rows = RewriteRows(table.Rows, index) });
        return result;
    }

    private static IReadOnlyList<CompactRow> RewriteRows(IReadOnlyList<CompactRow> rows, Dictionary<string, int> index)
    {
        var result = new List<CompactRow>(rows.Count);
        foreach (CompactRow row in rows)
        {
            object?[] source = row.Values;
            var rewritten = new object?[source.Length];
            for (int i = 0; i < source.Length; i++)
                rewritten[i] = source[i] is string s && index.TryGetValue(s, out int idx) ? idx : source[i];
            result.Add(new CompactRow(rewritten));
        }
        return result;
    }

    // Two-pass profitability gate: a string is only worth pooling if replacing every occurrence
    // with its assigned index costs fewer bytes overall than leaving it as a literal, once the
    // one-time cost of listing it in the pool is accounted for. Candidates are ordered by
    // descending frequency so the most-repeated strings land on the cheapest (fewest-digit)
    // indices — e.g. a boolean-ish token repeated 250k+ times profits even at a single digit,
    // while a near-unique long string never clears the bar and stays a literal.
    private static IReadOnlyList<string> BuildPool(Dictionary<string, int> frequency, out Dictionary<string, int> index)
    {
        var candidates = new List<KeyValuePair<string, int>>();
        foreach (KeyValuePair<string, int> kv in frequency)
            if (kv.Value >= 2)
                candidates.Add(kv);
        candidates.Sort((a, b) =>
        {
            int byFrequency = b.Value.CompareTo(a.Value);
            return byFrequency != 0 ? byFrequency : string.CompareOrdinal(a.Key, b.Key);
        });

        var pool = new List<string>(candidates.Count);
        index = new Dictionary<string, int>(candidates.Count, StringComparer.Ordinal);
        for (int i = 0; i < candidates.Count; i++)
        {
            string value = candidates[i].Key;
            long occurrences = candidates[i].Value;
            int literalCost = value.Length + 2; // quotes; ignores rare escape-sequence expansion
            int indexCost = DecimalDigitCount(i); // conservative: real final index is <= i
            if (occurrences * (literalCost - indexCost) <= literalCost)
                continue; // doesn't pull its own weight in the pool

            index[value] = pool.Count;
            pool.Add(value);
        }
        return pool;
    }

    private static int DecimalDigitCount(int value)
    {
        int digits = 1;
        for (int n = value; n >= 10; n /= 10) digits++;
        return digits;
    }
}
