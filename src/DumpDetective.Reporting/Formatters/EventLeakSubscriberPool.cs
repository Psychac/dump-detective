using System.Linq;
using DumpDetective.Reporting.Models;

namespace DumpDetective.Reporting.Formatters;

/// <summary>
/// Deduplicates <c>EventLeakInstanceCard.SubscriberDetails</c> entries within each Event Leak
/// Analysis section (docs/refactor/report-payload-size-reduction-design.md, F4). A dump can
/// have thousands of instance cards that share the same handful of distinct subscriber
/// type/method pairs — measured 5,835 entries collapsing to 1,305 distinct values on a large
/// dump. Repeated entries are rewritten as an index into a new section-level
/// <c>SubscriberDetailPool</c>; entries that occur once stay inline (a lone reference costs
/// strictly more than the object it would replace).
/// </summary>
internal static class EventLeakSubscriberPool
{
    /// <summary>
    /// Rewrites domain sections in place (returning new immutable copies — inputs are never
    /// mutated, matching <see cref="ReportStringPool"/>'s contract that <c>Render()</c> stays
    /// safe to call more than once on the same document).
    /// </summary>
    public static IReadOnlyList<ReportDomainSection>? Apply(IReadOnlyList<ReportDomainSection>? domains)
    {
        if (domains == null) return domains;

        var result = new List<ReportDomainSection>(domains.Count);
        foreach (ReportDomainSection domain in domains)
        {
            IReadOnlyList<AnalyzerDetailSection> rewritten = RewriteSections(domain.Sections);
            result.Add(ReferenceEquals(rewritten, domain.Sections) ? domain : domain with { Sections = rewritten });
        }
        return result;
    }

    public static IReadOnlyList<AnalyzerDetailSection>? Apply(IReadOnlyList<AnalyzerDetailSection>? sections)
        => sections == null ? sections : RewriteSections(sections);

    private static IReadOnlyList<AnalyzerDetailSection> RewriteSections(IReadOnlyList<AnalyzerDetailSection> sections)
    {
        List<AnalyzerDetailSection>? result = null;
        for (int i = 0; i < sections.Count; i++)
        {
            AnalyzerDetailSection section = sections[i];
            if (section.EventLeakInstanceCards == null || section.EventLeakInstanceCards.Count == 0)
            {
                result?.Add(section);
                continue;
            }

            AnalyzerDetailSection rewritten = RewriteSection(section);
            if (result == null && !ReferenceEquals(rewritten, section))
            {
                result = new List<AnalyzerDetailSection>(sections.Count);
                for (int j = 0; j < i; j++) result.Add(sections[j]);
            }
            result?.Add(rewritten);
        }
        return result ?? sections;
    }

    private static AnalyzerDetailSection RewriteSection(AnalyzerDetailSection section)
    {
        var frequency = new Dictionary<SubscriberDetailEntry, int>();
        foreach (EventLeakInstanceCard card in section.EventLeakInstanceCards!)
            foreach (object item in card.SubscriberDetails ?? [])
                if (item is SubscriberDetailEntry entry)
                    frequency[entry] = frequency.TryGetValue(entry, out int count) ? count + 1 : 1;

        var index = new Dictionary<SubscriberDetailEntry, int>();
        var pool = new List<SubscriberDetailEntry>();
        foreach (KeyValuePair<SubscriberDetailEntry, int> kv in
            frequency.Where(kv => kv.Value >= 2).OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key.Type, StringComparer.Ordinal))
        {
            index[kv.Key] = pool.Count;
            pool.Add(kv.Key);
        }
        if (pool.Count == 0) return section;

        var newCards = new List<EventLeakInstanceCard>(section.EventLeakInstanceCards!.Count);
        foreach (EventLeakInstanceCard card in section.EventLeakInstanceCards!)
        {
            if (card.SubscriberDetails == null || card.SubscriberDetails.Count == 0)
            {
                newCards.Add(card);
                continue;
            }
            var newDetails = new List<object>(card.SubscriberDetails.Count);
            foreach (object item in card.SubscriberDetails)
                newDetails.Add(item is SubscriberDetailEntry entry && index.TryGetValue(entry, out int idx) ? idx : item);
            newCards.Add(card with { SubscriberDetails = newDetails });
        }
        return section with { EventLeakInstanceCards = newCards, SubscriberDetailPool = pool };
    }
}
