using DumpDetective.Reporting.Models;
using DumpDetective.Sdk.Identity;
using DumpDetective.Sdk.Observations;

namespace DumpDetective.Reporting.Services;

internal static class EntitySummaryBuilder
{
    public static EntitySummary Build(IReadOnlyList<Observation> observations, IReadOnlyList<EntityRecord> entities)
    {
        var entityIdByKey = new Dictionary<(EntityKind Kind, string JoinKey), int>(entities.Count);
        for (int i = 0; i < entities.Count; i++)
            entityIdByKey[(entities[i].Kind, entities[i].JoinKey)] = entities[i].Id;

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
                if (!entityIdByKey.TryGetValue((subjects[s].Kind, subjects[s].JoinKey), out int entityId))
                    continue;

                if (!accumulatorsByEntity.TryGetValue(entityId, out Dictionary<string, FactAccumulator>? byFactKind))
                    accumulatorsByEntity[entityId] = byFactKind = new Dictionary<string, FactAccumulator>(StringComparer.Ordinal);
                if (!byFactKind.TryGetValue(factKind, out FactAccumulator? accumulator))
                    byFactKind[factKind] = accumulator = new FactAccumulator();

                accumulator.Add(observation.Measures, measureKeys);
            }
        }

        return Index(accumulatorsByEntity, analyzerByFactKind, measureKeys);
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
        HashSet<(string Name, string Unit)> measureKeys)
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

        var entityIds = new List<int>(accumulatorsByEntity.Keys);
        entityIds.Sort();
        var entities = new List<EntityFacts>(entityIds.Count);
        foreach (int entityId in entityIds)
        {
            Dictionary<string, FactAccumulator> byFactKind = accumulatorsByEntity[entityId];
            var groups = new List<FactGroup>(byFactKind.Count);
            foreach (KeyValuePair<string, FactAccumulator> pair in byFactKind)
                groups.Add(pair.Value.ToGroup(factKindIndex[pair.Key], measureIndex));
            groups.Sort(static (a, b) => a.FactKind.CompareTo(b.FactKind));
            entities.Add(new EntityFacts(entityId, groups));
        }

        return new EntitySummary(factKinds, measures, entities);
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
