namespace DumpDetective.Reporting.Models;

/// <summary>
/// What a renderer needs to show a session's coverage, as wording rather than records: one headline and the groups behind it.
/// Generated once by <c>CoverageProse</c> so HTML, Markdown and text say the same thing; the records themselves stay in
/// <c>coverage</c> for tools.
/// </summary>
internal sealed record CoverageView(string Headline, IReadOnlyList<CoverageGroupView> Groups);

/// <param name="Kind">One of <c>limited</c>, <c>failed</c>, <c>unavailable</c>, <c>not-supplied</c>, <c>complete</c>; renderers key styling and order on it.</param>
internal sealed record CoverageGroupView(string Kind, string Title, IReadOnlyList<CoverageLineView> Lines);

/// <param name="Claims">Fingerprints of the claims this line limits; the HTML links each to its card.</param>
internal sealed record CoverageLineView(string Text, IReadOnlyList<string> Claims);
