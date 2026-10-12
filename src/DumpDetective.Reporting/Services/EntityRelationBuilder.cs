using DumpDetective.Reporting.Models;
using DumpDetective.Sdk.Identity;
using DumpDetective.Sdk.Observations;

namespace DumpDetective.Reporting.Services;

/// <summary>
/// Pairs of entities that appear together (docs/ReportStructure/m2b-relations-plan.md, steps 1-2). Two sources:
/// <list type="bullet">
/// <item>two entity cells in one row of a table, read off rows <see cref="EntityCellPass"/> has already rewritten so the list cannot
/// disagree with the links a reader sees (D5);</item>
/// <item>two subjects of one observation, named by its fact kind, which is exact where a row only says the two were shown together.</item>
/// </list>
/// A pair seen both ways keeps only its fact source (D2).
/// </summary>
internal static class EntityRelationBuilder
{
    public const string FactSourcePrefix = "fact: ";

    // A fact whose two subjects have different roles (a publisher and its subscriber) keeps them: pairs are stored lower id first, so the
    // source name says when the first subject was the higher id. The client reads both back to say which side the open page is on.
    public const string ReversedSuffix = " (reversed)";

    private static readonly HashSet<string> DirectedFactKinds = new(StringComparer.Ordinal) { "event.subscription", "type.dominance" };

    public static EntityRelations? FromTables(IReadOnlyList<ReportDomainSection>? domains)
    {
        if (domains is null)
            return null;

        var pairs = new PairSet();
        foreach (ReportDomainSection domain in domains)
        {
            foreach (AnalyzerDetailSection section in domain.Sections)
            {
                if (section.CompactTables is null)
                    continue;

                foreach (CompactTable table in section.CompactTables)
                    CollectTable(section.AnalyzerName, table, pairs);
            }
        }

        return pairs.ToRelations();
    }

    public static EntityRelations? FromFacts(IReadOnlyList<Observation> observations, IReadOnlyList<EntityRecord> entities)
    {
        var entityIdByKey = new Dictionary<(EntityKind Kind, string JoinKey), int>(entities.Count);
        foreach (EntityRecord entity in entities)
            entityIdByKey[(entity.Kind, entity.JoinKey)] = entity.Id;

        var pairs = new PairSet();
        var idsInFact = new List<int>(2);
        for (int i = 0; i < observations.Count; i++)
        {
            IReadOnlyList<EntityRef> subjects = observations[i].Subjects;
            if (subjects.Count < 2)
                continue;

            idsInFact.Clear();
            foreach (EntityRef subject in subjects)
            {
                if (entityIdByKey.TryGetValue((subject.Kind, subject.JoinKey), out int id) && !idsInFact.Contains(id))
                    idsInFact.Add(id);
            }

            if (DirectedFactKinds.Contains(observations[i].ObservationType) && idsInFact.Count == 2)
            {
                string source = FactSourcePrefix + observations[i].ObservationType + (idsInFact[0] > idsInFact[1] ? ReversedSuffix : "");
                pairs.Add(idsInFact[0], idsInFact[1], source);
                continue;
            }

            AddAllPairs(idsInFact, FactSourcePrefix + observations[i].ObservationType, pairs);
        }

        return pairs.ToRelations();
    }

    /// <summary>
    /// Fact pairs win over row pairs for the same two entities. Only the fact sources of <paramref name="existing"/> are kept, so
    /// merging a document that was already merged gives the same result.
    /// </summary>
    public static EntityRelations? Merge(EntityRelations? existing, EntityRelations? fromTables)
    {
        var merged = new PairSet();
        var factPairs = new HashSet<(int First, int Second)>();
        if (existing is not null)
        {
            foreach (EntityRelation pair in existing.Pairs)
            {
                foreach (int sourceIndex in pair.SourceIndexes)
                {
                    string source = existing.Sources[sourceIndex];
                    if (!source.StartsWith(FactSourcePrefix, StringComparison.Ordinal))
                        continue;

                    merged.Add(pair.First, pair.Second, source);
                    factPairs.Add((pair.First, pair.Second));
                }
            }
        }

        if (fromTables is not null)
        {
            foreach (EntityRelation pair in fromTables.Pairs)
            {
                if (factPairs.Contains((pair.First, pair.Second)))
                    continue;

                foreach (int sourceIndex in pair.SourceIndexes)
                    merged.Add(pair.First, pair.Second, fromTables.Sources[sourceIndex]);
            }
        }

        return merged.ToRelations();
    }

    private static void CollectTable(string analyzer, CompactTable table, PairSet pairs)
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
        var idsInRow = new List<int>(entityColumns.Count);
        foreach (CompactRow row in table.Rows)
        {
            idsInRow.Clear();
            foreach (int column in entityColumns)
            {
                if (column < row.Values.Length && row.Values[column] is EntityCell cell)
                    idsInRow.Add(cell.Id);
            }

            AddAllPairs(idsInRow, sourceName, pairs);
        }
    }

    private static void AddAllPairs(List<int> ids, string source, PairSet pairs)
    {
        for (int i = 0; i < ids.Count; i++)
        {
            for (int j = i + 1; j < ids.Count; j++)
            {
                if (ids[i] != ids[j])
                    pairs.Add(ids[i], ids[j], source);
            }
        }
    }

    // A source is only listed once a pair uses it, so a table that relates nothing never appears. Sources are sorted by name on the
    // way out, so two runs over the same input write the same list whatever order the observations arrived in.
    private sealed class PairSet
    {
        private readonly Dictionary<(int First, int Second), HashSet<string>> _sourcesByPair = [];

        public void Add(int a, int b, string source)
        {
            var key = (Math.Min(a, b), Math.Max(a, b));
            if (!_sourcesByPair.TryGetValue(key, out HashSet<string>? seenIn))
                _sourcesByPair[key] = seenIn = new HashSet<string>(StringComparer.Ordinal);
            seenIn.Add(source);
        }

        public EntityRelations? ToRelations()
        {
            if (_sourcesByPair.Count == 0)
                return null;

            var allSources = new SortedSet<string>(StringComparer.Ordinal);
            foreach (HashSet<string> seenIn in _sourcesByPair.Values)
                allSources.UnionWith(seenIn);

            var sources = new List<string>(allSources);
            var indexByName = new Dictionary<string, int>(sources.Count, StringComparer.Ordinal);
            for (int i = 0; i < sources.Count; i++)
                indexByName[sources[i]] = i;

            var pairs = new List<EntityRelation>(_sourcesByPair.Count);
            foreach (KeyValuePair<(int First, int Second), HashSet<string>> pair in _sourcesByPair)
            {
                var indexes = new List<int>(pair.Value.Count);
                foreach (string source in pair.Value)
                    indexes.Add(indexByName[source]);
                indexes.Sort();
                pairs.Add(new EntityRelation(pair.Key.First, pair.Key.Second, indexes));
            }

            pairs.Sort(static (a, b) => a.First != b.First ? a.First.CompareTo(b.First) : a.Second.CompareTo(b.Second));
            return new EntityRelations(sources, pairs);
        }
    }
}
