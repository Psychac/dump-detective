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
            [EntityKind.Module] = IdsByJoinKey(entities, EntityKind.Module),
            [EntityKind.Method] = IdsByJoinKey(entities, EntityKind.Method),
            [EntityKind.Object] = IdsByAddress(entities),
            [EntityKind.AppDomain] = IdsByJoinKey(entities, EntityKind.AppDomain),
        };
        var domains = new List<ReportDomainSection>(document.Domains.Count);
        foreach (ReportDomainSection domain in document.Domains)
            domains.Add(domain with { Sections = RewriteSections(domain.Sections, idsByKind) });

        return document with { Domains = domains, Relations = EntityRelationBuilder.Build(domains) };
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

    // An object's key is "<dump>:0x<address>" but tables show the address alone. One report is one dump, so the address identifies the
    // object; if two entities ever share an address (a report spanning several dumps) neither is linked, rather than guessing.
    private static Dictionary<string, int> IdsByAddress(IReadOnlyList<EntityRecord> entities)
    {
        var ids = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var ambiguous = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (EntityRecord entity in entities)
        {
            if (entity.Kind != EntityKind.Object)
                continue;

            string address = entity.JoinKey[(entity.JoinKey.LastIndexOf(':') + 1)..];
            if (!ids.TryAdd(address, entity.Id))
                ambiguous.Add(address);
        }

        foreach (string address in ambiguous)
            ids.Remove(address);

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

        int declaringTypeColumn = DeclaringTypeColumn(table);
        var rows = new List<CompactRow>(table.Rows.Count);
        foreach (CompactRow row in table.Rows)
        {
            object?[] cells = (object?[])row.Values.Clone();
            foreach (int column in entityColumns)
            {
                if (column >= cells.Length || cells[column] is null)
                    continue;

                EntityKind kind = EntityColumn.KindOf(table.Headers[column]);
                cells[column] = kind switch
                {
                    EntityKind.Thread => ThreadCell(cells[column]!, idsByKind[EntityKind.Thread]),
                    EntityKind.AppDomain => cells[column] is string { Length: > 0 } domainName && idsByKind[EntityKind.AppDomain].TryGetValue(domainName, out int domainId)
                        ? new EntityCell(domainId, null) : cells[column],
                    EntityKind.Object => cells[column] is string addressText && idsByKind[EntityKind.Object].TryGetValue(addressText, out int objectId)
                        ? new EntityCell(objectId, addressText) : cells[column],
                    EntityKind.Method => ToMethodCell(row.Values, column, declaringTypeColumn, idsByKind[EntityKind.Method]),
                    EntityKind.Module => cells[column] is string moduleText ? (object?)ToModuleCell(moduleText, idsByKind[EntityKind.Module]) ?? moduleText : cells[column],
                    _ => cells[column] is string shownName ? (object?)ToTypeCell(shownName, idsByKind[EntityKind.Type]) ?? shownName : cells[column],
                };
            }

            rows.Add(new CompactRow(cells));
        }

        return rows;
    }

    private static int DeclaringTypeColumn(CompactTable table)
    {
        for (int column = 0; column < table.Headers.Count; column++)
        {
            CompactHeader header = table.Headers[column];
            if (header.Type == EntityColumn.Type && header.Name == EntityColumn.MethodDeclaringTypeHeader)
                return column;
        }

        return -1;
    }

    // Reads the declaring type from the row as the table showed it, before that column is itself rewritten. The cell keeps the name
    // it showed (the method's own name), because the entity's key is the longer "<type>::<name>".
    private static object? ToMethodCell(object?[] shownRow, int methodColumn, int declaringTypeColumn, Dictionary<string, int> methodIds)
    {
        object? shownMethod = shownRow[methodColumn];
        if (shownMethod is not string { Length: > 0 } methodName || declaringTypeColumn < 0 || declaringTypeColumn >= shownRow.Length)
            return shownMethod;

        if (shownRow[declaringTypeColumn] is not string { Length: > 0 } declaringType)
            return shownMethod;

        return methodIds.TryGetValue($"{declaringType}::{methodName}", out int id) ? new EntityCell(id, methodName) : methodName;
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

    // A module is its file name. A cell that shows the name matches directly; one that shows the full path (the JIT table) matches on the
    // file name at its end and keeps the path as the exact text. A cell cut short for display ("...") matches nothing and stays text.
    private static EntityCell? ToModuleCell(string shown, Dictionary<string, int> moduleIds)
    {
        // The unnamed module is an entity with an empty file name, so an empty cell would otherwise match it and link to no address (D3).
        if (shown.Length == 0)
            return null;

        if (moduleIds.TryGetValue(shown, out int id))
            return new EntityCell(id, null);

        int separator = shown.LastIndexOfAny(['\\', '/']);
        if (separator < 0 || separator == shown.Length - 1)
            return null;

        return moduleIds.TryGetValue(shown[(separator + 1)..], out id) ? new EntityCell(id, shown) : null;
    }

    private static EntityCell? ToTypeCell(string shownName, Dictionary<string, int> typeIds)
    {
        string entityName = EntityCanonicalizer.CanonicalizeTypeName(shownName).CanonicalName;
        if (!typeIds.TryGetValue(entityName, out int id))
            return null;

        return new EntityCell(id, string.Equals(entityName, shownName, StringComparison.Ordinal) ? null : shownName);
    }
}
