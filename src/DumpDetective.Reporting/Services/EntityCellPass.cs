using System.Globalization;
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

        var idsByKind = new Dictionary<EntityKind, Dictionary<string, int>>
        {
            [EntityKind.Type] = IdsByJoinKey(entities, EntityKind.Type),
            [EntityKind.Thread] = IdsByJoinKey(entities, EntityKind.Thread),
        };
        var domains = new List<ReportDomainSection>(document.Domains.Count);
        foreach (ReportDomainSection domain in document.Domains)
            domains.Add(domain with { Sections = RewriteSections(domain.Sections, idsByKind) });

        return document with { Domains = domains };
    }

    private static Dictionary<string, int> IdsByJoinKey(IReadOnlyList<EntityRecord> entities, EntityKind kind)
    {
        var ids = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int i = 0; i < entities.Count; i++)
        {
            if (entities[i].Kind == kind)
                ids[entities[i].JoinKey] = entities[i].Id;
        }

        return ids;
    }

    private static IReadOnlyList<AnalyzerDetailSection> RewriteSections(IReadOnlyList<AnalyzerDetailSection> sections, Dictionary<EntityKind, Dictionary<string, int>> idsByKind)
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

            rewritten.Add(hasEntityColumn ? section with { CompactTables = RewriteTables(section.CompactTables!, idsByKind) } : section);
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

    private static IReadOnlyList<CompactTable> RewriteTables(IReadOnlyList<CompactTable> tables, Dictionary<EntityKind, Dictionary<string, int>> idsByKind)
    {
        var rewritten = new List<CompactTable>(tables.Count);
        foreach (CompactTable table in tables)
            rewritten.Add(HasEntityColumn(table) ? table with { Rows = RewriteRows(table, idsByKind) } : table);

        return rewritten;
    }

    private static IReadOnlyList<CompactRow> RewriteRows(CompactTable table, Dictionary<EntityKind, Dictionary<string, int>> idsByKind)
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
                if (column >= cells.Length || cells[column] is null)
                    continue;

                EntityKind kind = EntityColumn.KindOf(table.Headers[column]);
                cells[column] = kind == EntityKind.Thread
                    ? ThreadCell(cells[column]!, idsByKind[EntityKind.Thread])
                    : cells[column] is string shownName ? (object?)ToTypeCell(shownName, idsByKind[EntityKind.Type]) ?? shownName : cells[column];
            }

            rows.Add(new CompactRow(cells));
        }

        return rows;
    }

    // Thread ids are numbers in the table (builders hand over a double) and the entity's join key is the OS id as plain digits.
    // A thread with no entity becomes its digits as text: in an entity column a bare number means "entity id", so a leftover
    // number would be read as the wrong entity.
    private static object ThreadCell(object cell, Dictionary<string, int> threadIds)
    {
        string? joinKey = cell switch
        {
            uint or int or long or ulong or ushort => Convert.ToString(cell, CultureInfo.InvariantCulture),
            double number when number >= 0 && number == Math.Floor(number) => ((ulong)number).ToString(CultureInfo.InvariantCulture),
            string text => text,
            _ => null,
        };
        if (joinKey is null)
            return cell;

        return threadIds.TryGetValue(joinKey, out int id) ? new EntityCell(id, null) : joinKey;
    }

    private static EntityCell? ToTypeCell(string shownName, Dictionary<string, int> typeIds)
    {
        string entityName = EntityCanonicalizer.CanonicalizeTypeName(shownName).CanonicalName;
        if (!typeIds.TryGetValue(entityName, out int id))
            return null;

        return new EntityCell(id, string.Equals(entityName, shownName, StringComparison.Ordinal) ? null : shownName);
    }
}
