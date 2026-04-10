using System.Text;

namespace DumpDetective.Models
{
    internal static class FindingFingerprint
    {
        public static string Build(InsightFinding finding)
        {
            return Build(finding.Analyzer, finding.Category, finding.Title, finding.Tags);
        }

        public static string Build(string analyzer, string category, string title, IReadOnlyList<string> tags)
        {
            var normalizedTags = tags
                .Where(t => !string.IsNullOrWhiteSpace(t))
                .Select(t => t.Trim().ToLowerInvariant())
                .OrderBy(t => t, StringComparer.Ordinal);

            var titleNormalized = NormalizeToken(title);
            var analyzerNormalized = NormalizeToken(analyzer);
            var categoryNormalized = NormalizeToken(category);
            string tagPart = string.Join("|", normalizedTags);

            return $"{analyzerNormalized}::{categoryNormalized}::{titleNormalized}::{tagPart}";
        }

        private static string NormalizeToken(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return string.Empty;

            var sb = new StringBuilder(value.Length);
            foreach (char c in value.Trim().ToLowerInvariant())
            {
                if (char.IsLetterOrDigit(c) || c == ' ' || c == '_' || c == '-')
                {
                    sb.Append(c);
                }
            }

            return sb.ToString().Replace("  ", " ", StringComparison.Ordinal);
        }
    }
}
