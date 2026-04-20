namespace DumpDetective.Reporting.Formatters;

internal static class TableWrapHelper
{
    public static IReadOnlyList<string> Wrap(string? value, int width)
    {
        string text = value ?? string.Empty;
        if (width <= 0)
        {
            return [text];
        }

        List<string> lines = [];
        foreach (string rawLine in text.Replace("\r\n", "\n").Split('\n'))
        {
            if (rawLine.Length == 0)
            {
                lines.Add(string.Empty);
                continue;
            }

            int index = 0;
            while (index < rawLine.Length)
            {
                int take = Math.Min(width, rawLine.Length - index);
                int breakIndex = index + take;

                if (index + take < rawLine.Length)
                {
                    int candidate = rawLine.LastIndexOf(' ', index + take - 1, take);
                    if (candidate > index)
                    {
                        breakIndex = candidate;
                    }
                }

                string chunk = rawLine[index..breakIndex].TrimEnd();
                lines.Add(chunk);
                index = breakIndex;
                while (index < rawLine.Length && rawLine[index] == ' ')
                {
                    index++;
                }
            }
        }

        return lines.Count == 0 ? [string.Empty] : lines;
    }
}
