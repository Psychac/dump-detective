using System.Text.Json;

namespace DumpDetective.Tests.Golden;

/// <summary>
/// Base class for golden/snapshot tests. Provides utilities for comparing actual output against stored baselines.
/// </summary>
public abstract class GoldenTestBase
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    protected static string GetGoldenDirectory() =>
        Path.Combine(AppContext.BaseDirectory, "Golden", "Baselines");

    protected static string GetGoldenFilePath(string testName, string extension = ".golden") =>
        Path.Combine(GetGoldenDirectory(), $"{testName}{extension}");

    protected static void ApproveGoldenOutput(string actualOutput, string goldenFileName)
    {
        var goldenPath = GetGoldenFilePath(goldenFileName);
        var goldenDir = Path.GetDirectoryName(goldenPath);

        if (goldenDir != null && !Directory.Exists(goldenDir))
            Directory.CreateDirectory(goldenDir);

        if (!File.Exists(goldenPath))
        {
            File.WriteAllText(goldenPath, actualOutput);
            throw new InvalidOperationException(
                $"Golden file created at {goldenPath}. Review and commit this baseline.");
        }

        var expectedOutput = File.ReadAllText(goldenPath);
        if (actualOutput != expectedOutput)
        {
            var diffPath = Path.Combine(goldenDir, $"{Path.GetFileNameWithoutExtension(goldenPath)}.diff");
            File.WriteAllText(diffPath, $"EXPECTED:\n{expectedOutput}\n\nACTUAL:\n{actualOutput}");
            throw new InvalidOperationException(
                $"Golden output mismatch. Diff written to {diffPath}");
        }
    }

    protected static string SerializeToJson(object obj) =>
        JsonSerializer.Serialize(obj, JsonOptions);
}
