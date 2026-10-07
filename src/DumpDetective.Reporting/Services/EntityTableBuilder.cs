using DumpDetective.Reporting.Models;
using DumpDetective.Sdk.Identity;
using DumpDetective.Sdk.Observations;

namespace DumpDetective.Reporting.Services;

internal static class EntityTableBuilder
{
    public static IReadOnlyList<EntityRecord> Build(IReadOnlyList<Observation> observations, IReadOnlyList<FindingRecord> findings)
    {
        var lowestFidelityByEntity = new Dictionary<(EntityKind Kind, string JoinKey), MatchFidelity>();

        for (int i = 0; i < observations.Count; i++)
        {
            IReadOnlyList<EntityRef> subjects = observations[i].Subjects;
            for (int j = 0; j < subjects.Count; j++)
                Collect(subjects[j], lowestFidelityByEntity);
        }

        for (int i = 0; i < findings.Count; i++)
        {
            EntityRef? claimSubject = findings[i].Assertion?.Subject;
            if (claimSubject is not null)
                Collect(claimSubject, lowestFidelityByEntity);
        }

        var entityKeys = new List<(EntityKind Kind, string JoinKey)>(lowestFidelityByEntity.Keys);
        entityKeys.Sort(CompareKindThenJoinKey);

        var table = new List<EntityRecord>(entityKeys.Count);
        for (int id = 0; id < entityKeys.Count; id++)
        {
            (EntityKind kind, string joinKey) = entityKeys[id];
            table.Add(new EntityRecord(id, kind, joinKey, lowestFidelityByEntity[entityKeys[id]]));
        }

        return table;
    }

    // Two sources can resolve the same entity at different fidelity; keeping the lowest never overstates how
    // far the identity can be trusted, and unlike "first seen" it does not depend on analyzer completion order.
    private static void Collect(EntityRef entity, Dictionary<(EntityKind Kind, string JoinKey), MatchFidelity> lowestFidelityByEntity)
    {
        var key = (entity.Kind, entity.JoinKey);
        if (!lowestFidelityByEntity.TryGetValue(key, out MatchFidelity known) || entity.Fidelity < known)
            lowestFidelityByEntity[key] = entity.Fidelity;
    }

    private static int CompareKindThenJoinKey((EntityKind Kind, string JoinKey) left, (EntityKind Kind, string JoinKey) right)
    {
        int byKind = left.Kind.CompareTo(right.Kind);
        return byKind != 0 ? byKind : string.CompareOrdinal(left.JoinKey, right.JoinKey);
    }
}
