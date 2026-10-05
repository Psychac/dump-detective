namespace DumpDetective.Cli.Output;

/// <summary>
/// Companion files take the primary output's name so several reports in one folder never collide:
/// <c>Crash.html</c> pairs with <c>Crash.report.json</c> and <c>Crash.observations.json.gz</c>.
/// </summary>
internal static class ReportSiblingPath
{
    public static string For(string primaryOutputPath, string suffix)
    {
        string fullPath = Path.GetFullPath(primaryOutputPath);
        string directory = Path.GetDirectoryName(fullPath) ?? Directory.GetCurrentDirectory();
        return Path.Combine(directory, Path.GetFileNameWithoutExtension(fullPath) + suffix);
    }
}
