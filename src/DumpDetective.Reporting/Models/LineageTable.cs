using System.Text.Json.Serialization;
using DumpDetective.Sdk.Observations;

namespace DumpDetective.Reporting.Models;

/// <summary>
/// The observation ids cited by a report's findings, written once. <see cref="Ids"/> holds each distinct id; <see cref="Lists"/> holds
/// each distinct citation list as indexes into <see cref="Ids"/>. In <c>report.json</c> a finding's <c>derivedFrom</c> and the
/// <c>observations</c> of each support and counter statement are an index into <see cref="Lists"/>.
/// </summary>
[JsonConverter(typeof(DumpDetective.Reporting.Serialization.LineageTableJsonConverter))]
internal sealed record LineageTable(IReadOnlyList<ObservationId> Ids, IReadOnlyList<int[]> Lists);

/// <summary>Numbers the ids and citation lists of a document's findings, in the order a walk of the findings first meets them.</summary>
internal sealed class LineageIndex
{
    private readonly Dictionary<ObservationId, int> _idIndexes = new();
    private readonly List<ObservationId> _ids = new();
    private readonly List<int[]> _lists = new();
    private readonly Dictionary<IReadOnlyList<ObservationId>, int> _listsByReference = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<IReadOnlyList<ObservationId>, int> _listsByContent = new(ContentComparer.Instance);

    public LineageTable Table => new(_ids, _lists);

    public bool IsEmpty => _lists.Count == 0;

    public static LineageIndex Build(AnalysisReportDocument document)
    {
        var index = new LineageIndex();
        if (document.Domains is not null)
        {
            foreach (ReportDomainSection domain in document.Domains)
                index.AddFindings(domain.DomainInsights);
        }

        index.AddFindings(document.CrossDomainInsights);
        return index;
    }

    /// <summary>The position in <see cref="LineageTable.Lists"/> of a list that was met while building; throws for any other list.</summary>
    public int ListOf(IReadOnlyList<ObservationId> list) => _listsByReference[list];

    private void AddFindings(IReadOnlyList<FindingRecord>? findings)
    {
        if (findings is null)
            return;

        foreach (FindingRecord finding in findings)
        {
            if (finding.DerivedFrom is not null)
                AddList(finding.DerivedFrom);

            AddStatements(finding.Support);
            AddStatements(finding.Counter?.Statements);
        }
    }

    private void AddStatements(IReadOnlyList<DumpDetective.Sdk.Synthesis.EvidenceStatement>? statements)
    {
        if (statements is null)
            return;

        foreach (DumpDetective.Sdk.Synthesis.EvidenceStatement statement in statements)
            AddList(statement.Observations);
    }

    private void AddList(IReadOnlyList<ObservationId> list)
    {
        if (_listsByReference.ContainsKey(list))
            return;

        if (!_listsByContent.TryGetValue(list, out int listIndex))
        {
            var indexes = new int[list.Count];
            for (int i = 0; i < indexes.Length; i++)
                indexes[i] = IndexOfId(list[i]);

            listIndex = _lists.Count;
            _lists.Add(indexes);
            _listsByContent.Add(list, listIndex);
        }

        _listsByReference.Add(list, listIndex);
    }

    private int IndexOfId(ObservationId id)
    {
        if (_idIndexes.TryGetValue(id, out int index))
            return index;

        index = _ids.Count;
        _ids.Add(id);
        _idIndexes.Add(id, index);
        return index;
    }

    private sealed class ContentComparer : IEqualityComparer<IReadOnlyList<ObservationId>>
    {
        public static readonly ContentComparer Instance = new();

        public bool Equals(IReadOnlyList<ObservationId>? x, IReadOnlyList<ObservationId>? y)
        {
            if (x is null || y is null || x.Count != y.Count)
                return false;

            for (int i = 0; i < x.Count; i++)
            {
                if (x[i] != y[i])
                    return false;
            }

            return true;
        }

        public int GetHashCode(IReadOnlyList<ObservationId> list)
        {
            var hash = new HashCode();
            hash.Add(list.Count);
            for (int i = 0; i < list.Count; i++)
                hash.Add(list[i]);

            return hash.ToHashCode();
        }
    }
}
