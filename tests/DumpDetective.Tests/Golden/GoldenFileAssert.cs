namespace DumpDetective.Tests.Golden;

internal static class GoldenFileAssert
{
    public static void AssertMatches(string expected, string actual)
    {
        string normalizedExpected = Normalize(expected);
        string normalizedActual = Normalize(actual);

        if (normalizedExpected == normalizedActual)
        {
            return;
        }

        string[] expectedLines = normalizedExpected.Split('\n');
        string[] actualLines = normalizedActual.Split('\n');
        int max = Math.Max(expectedLines.Length, actualLines.Length);

        for (int i = 0; i < max; i++)
        {
            string expectedLine = i < expectedLines.Length ? expectedLines[i] : "<missing>";
            string actualLine = i < actualLines.Length ? actualLines[i] : "<missing>";
            if (!string.Equals(expectedLine, actualLine, StringComparison.Ordinal))
            {
                throw new Xunit.Sdk.XunitException($"Golden mismatch at line {i + 1}.{Environment.NewLine}Expected: {expectedLine}{Environment.NewLine}Actual:   {actualLine}");
            }
        }

        throw new Xunit.Sdk.XunitException("Golden mismatch with no identifiable differing line.");
    }

    private static string Normalize(string value) => value.Replace("\r\n", "\n").TrimEnd();
}
