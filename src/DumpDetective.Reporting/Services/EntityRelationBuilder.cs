using DumpDetective.Reporting.Models;

namespace DumpDetective.Reporting.Services;

/// <summary>
/// Reads relations off tables that have already been rewritten by <see cref="EntityCellPass"/>: two entity cells in one row are
/// related by the table's own definition. It runs on the rewritten rows so the list cannot disagree with the links a reader sees
/// (m2b-relations-plan.md, D2 and D5).
/// </summary>
internal static class EntityRelationBuilder
{
    public static EntityRelations? Build(IReadOnlyList<ReportDomainSection>? domains)
    {
        if (domains is null)
            return null;

        var sourceIndexByName = new Dictionary<string, int>(StringComparer.Ordinal);
        var sources = new List<string>();
        var sourcesByPair = new Dictionary<(int First, int Second), SortedSet<int>>();

        foreach (ReportDomainSection domain in domains)
        {
            foreach (AnalyzerDetailSection section in domain.Sections)
            {
                if (section.CompactTables is null)
                    continue;

                foreach (CompactTable table in section.CompactTables)
                    CollectTable(section.AnalyzerName, table, sourceIndexByName, sources, sourcesByPair);
            }
        }

        if (sourcesByPair.Count == 0)
            return null;

        var pairs = new List<EntityRelation>(sourcesByPair.Count);
        foreach (KeyValuePair<(int First, int Second), SortedSet<int>> pair in sourcesByPair)
            pairs.Add(new EntityRelation(pair.Key.First, pair.Key.Second, [.. pair.Value]));

        pairs.Sort(static (a, b) => a.First != b.First ? a.First.CompareTo(b.First) : a.Second.CompareTo(b.Second));
        return new EntityRelations(sources, pairs);
    }

    private static void CollectTable(
        string analyzer, CompactTable table, Dictionary<string, int> sourceIndexByName, List<string> sources,
        Dictionary<(int First, int Second), SortedSet<int>> sourcesByPair)
    {
        var entityColumns = new List<int>();
        for (int column = 0; column < table.Headers.Count; column++)
        {
            if (EntityColumn.IsEntityColumn(table.Headers[column]))
                entityColumns.Add(column);
        }

        if (entityColumns.Count < 2)
            return;

        string sourceName = $"{analyzer} / {table.Title}";
        int sourceIndex = -1;
        var idsInRow = new List<int>(entityColumns.Count);
        foreach (CompactRow row in table.Rows)
        {
            idsInRow.Clear();
            foreach (int column in entityColumns)
            {
                if (column < row.Values.Length && row.Values[column] is EntityCell cell)
                    idsInRow.Add(cell.Id);
            }

            for (int i = 0; i < idsInRow.Count; i++)
            {
                for (int j = i + 1; j < idsInRow.Count; j++)
                {
                    if (idsInRow[i] == idsInRow[j])
                        continue;

                    if (sourceIndex < 0)
                        sourceIndex = SourceIndex(sourceName, sourceIndexByName, sources);

                    var key = (Math.Min(idsInRow[i], idsInRow[j]), Math.Max(idsInRow[i], idsInRow[j]));
                    if (!sourcesByPair.TryGetValue(key, out SortedSet<int>? seenIn))
                        sourcesByPair[key] = seenIn = [];
                    seenIn.Add(sourceIndex);
                }
            }
        }
    }

    // A table that relates nothing (no row has two entity cells) is never listed as a source.
    private static int SourceIndex(string name, Dictionary<string, int> sourceIndexByName, List<string> sources)
    {
        if (sourceIndexByName.TryGetValue(name, out int index))
            return index;

        index = sources.Count;
        sources.Add(name);
        sourceIndexByName[name] = index;
        return index;
    }
}
