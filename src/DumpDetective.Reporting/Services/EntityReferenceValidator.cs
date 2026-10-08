using DumpDetective.Reporting.Models;
using DumpDetective.Sdk.Identity;

namespace DumpDetective.Reporting.Services;

/// <summary>
/// Checks, as the report is written, that every reference to the entity table resolves: table cells, the per-entity summary and its
/// claim links (docs/ReportStructure/ReportSystemVision.md § 9.5, "a broken anchor is a build failure"). Returns what is broken
/// instead of throwing, so one bad link never costs the reader the whole report.
/// </summary>
internal static class EntityReferenceValidator
{
    public static IReadOnlyList<string> Validate(AnalysisReportDocument document)
    {
        var problems = new List<string>();
        if (document.Entities is not { } entities)
            return problems;

        CheckEntityIds(entities, problems);
        CheckEntityAddresses(entities, problems);
        if (document.EntitySummary is { } summary)
            CheckSummary(summary, entities, document.Findings, problems);
        CheckTableCells(EntityCellPass.Apply(document), entities, problems);
        return problems;
    }

    private static void CheckEntityIds(IReadOnlyList<EntityRecord> entities, List<string> problems)
    {
        for (int i = 0; i < entities.Count; i++)
        {
            if (entities[i].Id != i)
                problems.Add($"Entity table row {i} has id {entities[i].Id}; ids must equal the row index.");
        }
    }

    // The page address is #entity/<kind>/<joinKey>, so two entities sharing one would open each other's page, and an empty
    // key cannot be addressed at all (the client's address pattern needs at least one character after the kind).
    private static void CheckEntityAddresses(IReadOnlyList<EntityRecord> entities, List<string> problems)
    {
        var firstIdByAddress = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (EntityRecord entity in entities)
        {
            if (entity.JoinKey.Length == 0)
                continue;

            string address = Address(entity);
            if (firstIdByAddress.TryGetValue(address, out int firstId))
                problems.Add($"Entities {firstId} and {entity.Id} share the address '{address}'; opening it would show only one of them.");
            else
                firstIdByAddress[address] = entity.Id;
        }
    }

    private static void CheckSummary(
        EntitySummary summary, IReadOnlyList<EntityRecord> entities, IReadOnlyList<FindingRecord> findings, List<string> problems)
    {
        var findingIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (FindingRecord finding in findings)
            findingIds.Add(finding.Id);

        foreach (string claimId in summary.ClaimIds)
        {
            if (!findingIds.Contains(claimId))
                problems.Add($"Entity summary lists claim '{claimId}', which is not a finding in this report.");
        }

        foreach (EntityFacts row in summary.Entities)
        {
            if (row.EntityId < 0 || row.EntityId >= entities.Count)
            {
                problems.Add($"Entity summary row names entity {row.EntityId}, which is not in the entity table ({entities.Count} rows).");
                continue;
            }

            foreach (int claimIndex in row.ClaimIndexes)
            {
                if (claimIndex < 0 || claimIndex >= summary.ClaimIds.Count)
                    problems.Add($"Entity {Describe(entities[row.EntityId])} links claim index {claimIndex}; only {summary.ClaimIds.Count} claims are listed.");
            }

            foreach (FactGroup group in row.Groups)
            {
                if (group.FactKind < 0 || group.FactKind >= summary.FactKinds.Count)
                    problems.Add($"Entity {Describe(entities[row.EntityId])} has a fact group with kind index {group.FactKind}; only {summary.FactKinds.Count} kinds are listed.");
                foreach (MeasureStat stat in group.Stats)
                {
                    if (stat.Measure < 0 || stat.Measure >= summary.Measures.Count)
                        problems.Add($"Entity {Describe(entities[row.EntityId])} has a measure index {stat.Measure}; only {summary.Measures.Count} measures are listed.");
                }
            }
        }
    }

    private static void CheckTableCells(AnalysisReportDocument rewritten, IReadOnlyList<EntityRecord> entities, List<string> problems)
    {
        if (rewritten.Domains is null)
            return;

        foreach (ReportDomainSection domain in rewritten.Domains)
        {
            foreach (AnalyzerDetailSection section in domain.Sections)
            {
                if (section.CompactTables is null)
                    continue;

                foreach (CompactTable table in section.CompactTables)
                    CheckTable(section.AnalyzerName, table, entities, problems);
            }
        }
    }

    private static void CheckTable(string analyzer, CompactTable table, IReadOnlyList<EntityRecord> entities, List<string> problems)
    {
        for (int column = 0; column < table.Headers.Count; column++)
        {
            if (!EntityColumn.IsEntityColumn(table.Headers[column]))
                continue;

            EntityKind expected = EntityColumn.KindOf(table.Headers[column]);
            for (int row = 0; row < table.Rows.Count; row++)
            {
                object? cell = column < table.Rows[row].Values.Length ? table.Rows[row].Values[column] : null;
                string where = $"{analyzer} / '{table.Title}' / column '{table.Headers[column].Name}' / row {row}";
                switch (cell)
                {
                    case EntityCell reference when reference.Id < 0 || reference.Id >= entities.Count:
                        problems.Add($"{where}: points at entity {reference.Id}, which is not in the entity table ({entities.Count} rows).");
                        break;
                    case EntityCell reference when entities[reference.Id].JoinKey.Length == 0:
                        problems.Add($"{where}: links to {Describe(entities[reference.Id])}, which has no name to address; the cell should stay text.");
                        break;
                    case EntityCell reference when entities[reference.Id].Kind != expected:
                        problems.Add($"{where}: points at {Describe(entities[reference.Id])}, but the column holds {expected} entities.");
                        break;
                    case null or string:
                        break;
                    case EntityCell:
                        break;
                    default:
                        problems.Add($"{where}: holds a bare {cell.GetType().Name}, which a reader would take for an entity id.");
                        break;
                }
            }
        }
    }

    private static string Address(EntityRecord entity) => $"#entity/{entity.Kind.ToString().ToLowerInvariant()}/{entity.JoinKey}";

    private static string Describe(EntityRecord entity) => $"{entity.Kind} '{entity.JoinKey}' (id {entity.Id})";
}
