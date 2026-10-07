using DumpDetective.Reporting.Models;
using DumpDetective.Sdk.Identity;

namespace DumpDetective.Reporting.Services;

/// <summary>
/// Replaces the type names in entity-typed compact-table columns with <see cref="EntityCell"/>s that point into the
/// document's entity table. Runs only when <c>report.json</c> or the HTML is rendered, so Markdown and text keep seeing names.
/// </summary>
internal static class EntityCellPass
{
    public static AnalysisReportDocument Apply(AnalysisReportDocument document)
    {
        if (document.Entities is not { Count: > 0 } entities || document.Domains is null)
            return document;

        Dictionary<string, int> typeIds = TypeIdsByJoinKey(entities);
        var domains = new List<ReportDomainSection>(document.Domains.Count);
        foreach (ReportDomainSection domain in document.Domains)
            domains.Add(domain with { Sections = RewriteSections(domain.Sections, typeIds) });

        return document with { Domains = domains };
    }

    private static Dictionary<string, int> TypeIdsByJoinKey(IReadOnlyList<EntityRecord> entities)
    {
        var typeIds = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int i = 0; i < entities.Count; i++)
        {
            if (entities[i].Kind == EntityKind.Type)
                typeIds[entities[i].JoinKey] = entities[i].Id;
        }

        return typeIds;
    }

    private static IReadOnlyList<AnalyzerDetailSection> RewriteSections(IReadOnlyList<AnalyzerDetailSection> sections, Dictionary<string, int> typeIds)
    {
        var rewritten = new List<AnalyzerDetailSection>(sections.Count);
        foreach (AnalyzerDetailSection section in sections)
        {
            bool hasEntityColumn = false;
            if (section.CompactTables is not null)
            {
                foreach (CompactTable table in section.CompactTables)
                    hasEntityColumn |= HasEntityColumn(table);
            }

            rewritten.Add(hasEntityColumn ? section with { CompactTables = RewriteTables(section.CompactTables!, typeIds) } : section);
        }

        return rewritten;
    }

    private static bool HasEntityColumn(CompactTable table)
    {
        foreach (CompactHeader header in table.Headers)
        {
            if (EntityColumn.IsEntityColumn(header))
                return true;
        }

        return false;
    }

    private static IReadOnlyList<CompactTable> RewriteTables(IReadOnlyList<CompactTable> tables, Dictionary<string, int> typeIds)
    {
        var rewritten = new List<CompactTable>(tables.Count);
        foreach (CompactTable table in tables)
            rewritten.Add(HasEntityColumn(table) ? table with { Rows = RewriteRows(table, typeIds) } : table);

        return rewritten;
    }

    private static IReadOnlyList<CompactRow> RewriteRows(CompactTable table, Dictionary<string, int> typeIds)
    {
        var entityColumns = new List<int>();
        for (int column = 0; column < table.Headers.Count; column++)
        {
            if (EntityColumn.IsEntityColumn(table.Headers[column]))
                entityColumns.Add(column);
        }

        var rows = new List<CompactRow>(table.Rows.Count);
        foreach (CompactRow row in table.Rows)
        {
            object?[] cells = (object?[])row.Values.Clone();
            foreach (int column in entityColumns)
            {
                if (column < cells.Length && cells[column] is string shownName)
                    cells[column] = ToEntityCell(shownName, typeIds) ?? (object)shownName;
            }

            rows.Add(new CompactRow(cells));
        }

        return rows;
    }

    private static EntityCell? ToEntityCell(string shownName, Dictionary<string, int> typeIds)
    {
        string entityName = EntityCanonicalizer.CanonicalizeTypeName(shownName).CanonicalName;
        if (!typeIds.TryGetValue(entityName, out int id))
            return null;

        return new EntityCell(id, string.Equals(entityName, shownName, StringComparison.Ordinal) ? null : shownName);
    }
}
