using DumpDetective.Reporting.Models;
using DumpDetective.Sdk.Identity;
using DumpDetective.Sdk.Observations;

namespace DumpDetective.Reporting.Services;

internal static class EntitySummaryBuilder
{
    public static EntitySummary Build(
        IReadOnlyList<Observation> observations, IReadOnlyList<EntityRecord> entities, IReadOnlyList<FindingRecord>? claims = null)
    {
        var entityIdByKey = new Dictionary<(EntityKind Kind, string JoinKey), int>(entities.Count);
        for (int i = 0; i < entities.Count; i++)
            entityIdByKey[(entities[i].Kind, entities[i].JoinKey)] = entities[i].Id;

        (List<string> claimIds, Dictionary<ObservationId, List<int>> claimIndexesByObservation) = IndexCitingClaims(claims);
        var claimIndexesByEntity = new Dictionary<int, SortedSet<int>>();
        var accumulatorsByEntity = new Dictionary<int, Dictionary<string, FactAccumulator>>();
        var analyzerByFactKind = new Dictionary<string, string>(StringComparer.Ordinal);
        var measureKeys = new HashSet<(string Name, string Unit)>();

        for (int i = 0; i < observations.Count; i++)
        {
            Observation observation = observations[i];
            string factKind = observation.ObservationType;
            NoteAnalyzer(analyzerByFactKind, factKind, observation.Provenance.AnalyzerKey);

            IReadOnlyList<EntityRef> subjects = observation.Subjects;
            for (int s = 0; s < subjects.Count; s++)
            {
                // A fact that names one entity twice (a type holding objects of its own type) is still one fact for that entity.
                if (NamedEarlier(subjects, s))
                    continue;

                if (!entityIdByKey.TryGetValue((subjects[s].Kind, subjects[s].JoinKey), out int entityId))
                    continue;

                if (claimIndexesByObservation.TryGetValue(observation.Id, out List<int>? citingClaims))
                    AddClaims(claimIndexesByEntity, entityId, citingClaims);

                if (!accumulatorsByEntity.TryGetValue(entityId, out Dictionary<string, FactAccumulator>? byFactKind))
                    accumulatorsByEntity[entityId] = byFactKind = new Dictionary<string, FactAccumulator>(StringComparer.Ordinal);
                if (!byFactKind.TryGetValue(factKind, out FactAccumulator? accumulator))
                    byFactKind[factKind] = accumulator = new FactAccumulator();

                accumulator.Add(observation.Measures, measureKeys);
            }
        }

        AddClaimsBySubject(claims, claimIds, entityIdByKey, claimIndexesByEntity);
        List<string> linkedClaimIds = KeepLinkedClaims(claimIds, claimIndexesByEntity);
        return Index(accumulatorsByEntity, analyzerByFactKind, measureKeys, linkedClaimIds, claimIndexesByEntity);
    }

    private static bool NamedEarlier(IReadOnlyList<EntityRef> subjects, int index)
    {
        for (int earlier = 0; earlier < index; earlier++)
        {
            if (subjects[earlier].Kind == subjects[index].Kind && string.Equals(subjects[earlier].JoinKey, subjects[index].JoinKey, StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    // A claim that cites only process-wide numbers reaches no entity; listing its id would cost bytes and imply a link
    // that is not there. Ids were assigned in sorted order, so dropping the unlinked ones keeps the order.
    private static List<string> KeepLinkedClaims(List<string> claimIds, Dictionary<int, SortedSet<int>> claimIndexesByEntity)
    {
        var linked = new SortedSet<int>();
        foreach (SortedSet<int> indexes in claimIndexesByEntity.Values)
            linked.UnionWith(indexes);

        var newIndexByOldIndex = new Dictionary<int, int>(linked.Count);
        var linkedClaimIds = new List<string>(linked.Count);
        foreach (int oldIndex in linked)
        {
            newIndexByOldIndex[oldIndex] = linkedClaimIds.Count;
            linkedClaimIds.Add(claimIds[oldIndex]);
        }

        foreach (int entityId in new List<int>(claimIndexesByEntity.Keys))
        {
            var remapped = new SortedSet<int>();
            foreach (int oldIndex in claimIndexesByEntity[entityId])
                remapped.Add(newIndexByOldIndex[oldIndex]);
            claimIndexesByEntity[entityId] = remapped;
        }

        return linkedClaimIds;
    }

    // Only claims that cite observations (or name a subject) get an index; ids are sorted so identical input gives
    // identical output however the findings happened to be ordered.
    private static (List<string> ClaimIds, Dictionary<ObservationId, List<int>> ClaimIndexesByObservation) IndexCitingClaims(
        IReadOnlyList<FindingRecord>? claims)
    {
        var claimIds = new List<string>();
        var claimIndexesByObservation = new Dictionary<ObservationId, List<int>>();
        if (claims is null)
            return (claimIds, claimIndexesByObservation);

        var citing = new List<FindingRecord>();
        foreach (FindingRecord claim in claims)
        {
            if (claim.DerivedFrom is { Count: > 0 } || claim.Assertion?.Subject is not null)
                citing.Add(claim);
        }

        citing.Sort(static (a, b) => string.CompareOrdinal(a.Id, b.Id));
        for (int claimIndex = 0; claimIndex < citing.Count; claimIndex++)
        {
            claimIds.Add(citing[claimIndex].Id);
            if (citing[claimIndex].DerivedFrom is not { } derivedFrom)
                continue;

            foreach (ObservationId observationId in derivedFrom)
            {
                if (!claimIndexesByObservation.TryGetValue(observationId, out List<int>? indexes))
                    claimIndexesByObservation[observationId] = indexes = new List<int>(1);
                if (!indexes.Contains(claimIndex))
                    indexes.Add(claimIndex);
            }
        }

        return (claimIds, claimIndexesByObservation);
    }

    private static void AddClaims(Dictionary<int, SortedSet<int>> claimIndexesByEntity, int entityId, List<int> claimIndexes)
    {
        if (!claimIndexesByEntity.TryGetValue(entityId, out SortedSet<int>? known))
            claimIndexesByEntity[entityId] = known = new SortedSet<int>();
        foreach (int claimIndex in claimIndexes)
            known.Add(claimIndex);
    }

    // A claim can name an entity directly through its assertion subject without citing any observation about it.
    private static void AddClaimsBySubject(
        IReadOnlyList<FindingRecord>? claims, List<string> claimIds,
        Dictionary<(EntityKind Kind, string JoinKey), int> entityIdByKey, Dictionary<int, SortedSet<int>> claimIndexesByEntity)
    {
        if (claims is null)
            return;

        foreach (FindingRecord claim in claims)
        {
            if (claim.Assertion?.Subject is not { } subject
                || !entityIdByKey.TryGetValue((subject.Kind, subject.JoinKey), out int entityId))
                continue;

            AddClaims(claimIndexesByEntity, entityId, [claimIds.IndexOf(claim.Id)]);
        }
    }

    // The same kind is always emitted by one analyzer; if that ever stops being true, the lowest name wins so the choice
    // does not depend on the order analyzers finished in.
    private static void NoteAnalyzer(Dictionary<string, string> analyzerByFactKind, string factKind, string analyzer)
    {
        if (!analyzerByFactKind.TryGetValue(factKind, out string? known) || string.CompareOrdinal(analyzer, known) < 0)
            analyzerByFactKind[factKind] = analyzer;
    }

    private static EntitySummary Index(
        Dictionary<int, Dictionary<string, FactAccumulator>> accumulatorsByEntity,
        Dictionary<string, string> analyzerByFactKind,
        HashSet<(string Name, string Unit)> measureKeys,
        List<string> claimIds,
        Dictionary<int, SortedSet<int>> claimIndexesByEntity)
    {
        var usedFactKinds = new HashSet<string>(StringComparer.Ordinal);
        foreach (Dictionary<string, FactAccumulator> byFactKind in accumulatorsByEntity.Values)
        {
            foreach (string factKind in byFactKind.Keys)
                usedFactKinds.Add(factKind);
        }

        var factKindNames = new List<string>(usedFactKinds);
        factKindNames.Sort(StringComparer.Ordinal);
        var factKindIndex = new Dictionary<string, int>(factKindNames.Count, StringComparer.Ordinal);
        var factKinds = new List<FactKindRecord>(factKindNames.Count);
        for (int i = 0; i < factKindNames.Count; i++)
        {
            factKindIndex[factKindNames[i]] = i;
            factKinds.Add(new FactKindRecord(factKindNames[i], analyzerByFactKind[factKindNames[i]], EntityFactPanels.PanelFor(factKindNames[i])));
        }

        var measureList = new List<(string Name, string Unit)>(measureKeys);
        measureList.Sort(static (a, b) =>
        {
            int byName = string.CompareOrdinal(a.Name, b.Name);
            return byName != 0 ? byName : string.CompareOrdinal(a.Unit, b.Unit);
        });
        var measureIndex = new Dictionary<(string Name, string Unit), int>(measureList.Count);
        var measures = new List<MeasureRecord>(measureList.Count);
        for (int i = 0; i < measureList.Count; i++)
        {
            measureIndex[measureList[i]] = i;
            measures.Add(new MeasureRecord(measureList[i].Name, measureList[i].Unit));
        }

        var entityIdSet = new SortedSet<int>(accumulatorsByEntity.Keys);
        entityIdSet.UnionWith(claimIndexesByEntity.Keys);
        var entities = new List<EntityFacts>(entityIdSet.Count);
        foreach (int entityId in entityIdSet)
        {
            var groups = new List<FactGroup>();
            if (accumulatorsByEntity.TryGetValue(entityId, out Dictionary<string, FactAccumulator>? byFactKind))
            {
                foreach (KeyValuePair<string, FactAccumulator> pair in byFactKind)
                    groups.Add(pair.Value.ToGroup(factKindIndex[pair.Key], measureIndex));
                groups.Sort(static (a, b) => a.FactKind.CompareTo(b.FactKind));
            }

            int[] claimIndexes = claimIndexesByEntity.TryGetValue(entityId, out SortedSet<int>? entityClaims) ? [.. entityClaims] : [];
            entities.Add(new EntityFacts(entityId, groups, claimIndexes));
        }

        return new EntitySummary(factKinds, measures, entities, claimIds);
    }

    private sealed class FactAccumulator
    {
        private readonly Dictionary<(string Name, string Unit), MeasureAccumulator> _measures = new();
        private int _count;

        public void Add(IReadOnlyDictionary<string, Measure> measures, HashSet<(string Name, string Unit)> measureKeys)
        {
            _count++;
            foreach (KeyValuePair<string, Measure> pair in measures)
            {
                var key = (pair.Key, pair.Value.Unit.ToString());
                measureKeys.Add(key);
                if (!_measures.TryGetValue(key, out MeasureAccumulator? accumulator))
                    _measures[key] = accumulator = new MeasureAccumulator();
                accumulator.Add(pair.Value.Value);
            }
        }

        public FactGroup ToGroup(int factKindIndex, Dictionary<(string Name, string Unit), int> measureIndex)
        {
            var stats = new List<MeasureStat>(_measures.Count);
            foreach (KeyValuePair<(string Name, string Unit), MeasureAccumulator> pair in _measures)
            {
                MeasureAccumulator accumulator = pair.Value;
                bool canSum = accumulator.Count == 1 || EntityFactPanels.IsSummable(pair.Key.Name);
                stats.Add(new MeasureStat(measureIndex[pair.Key], canSum ? accumulator.Sum : null, accumulator.Min, accumulator.Max));
            }

            stats.Sort(static (a, b) => a.Measure.CompareTo(b.Measure));
            return new FactGroup(factKindIndex, _count, stats);
        }
    }

    private sealed class MeasureAccumulator
    {
        public int Count { get; private set; }
        public double Sum { get; private set; }
        public double Min { get; private set; } = double.PositiveInfinity;
        public double Max { get; private set; } = double.NegativeInfinity;

        public void Add(double value)
        {
            Count++;
            Sum += value;
            if (value < Min) Min = value;
            if (value > Max) Max = value;
        }
    }
}
