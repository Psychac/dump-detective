using System.Text.Json;
using System.Text.Json.Serialization;
using DumpDetective.Sdk.Artifacts;
using DumpDetective.Sdk.Observations;
using DumpDetective.Sdk.Synthesis;
using DumpDetective.Sdk.Temporal;
using Xunit.Sdk;

namespace DumpDetective.Tests.Golden;

/// <summary>
/// Pins the structured claim a rule emits (assertion, support, counter, falsifiedBy) for fixed observation
/// scenarios, so a change to what a rule says cannot land without a visible <see cref="ISynthesisRule.Version"/> bump.
/// Observation ids are random per run, so they are recorded as scenario-relative positions.
/// </summary>
/// <remarks>
/// The golden files live in the source tree (<c>tests/DumpDetective.Tests/Golden/RuleShapes</c>) and are read from
/// there, not from the build output. Set <c>DD_BLESS_RULE_SHAPES=1</c> to (re)write a golden file; blessing is
/// refused when the shape changed but the rule's version did not.
/// </remarks>
internal static class RuleShapeGolden
{
    private const string BlessVariable = "DD_BLESS_RULE_SHAPES";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    public sealed record Scenario(string Name, IReadOnlyList<Observation> Observations);

    public static async Task AssertMatchesAsync(ISynthesisRule rule, IReadOnlyList<Scenario> scenarios)
    {
        string currentShape = await DescribeShapeAsync(rule, scenarios);
        string path = Path.Combine(FindGoldenDirectory(), $"{rule.RuleId}.golden");
        bool bless = Environment.GetEnvironmentVariable(BlessVariable) == "1";

        if (!File.Exists(path))
        {
            if (!bless)
                throw new XunitException($"No golden for rule '{rule.RuleId}'. Run with {BlessVariable}=1 to create {path}, then review and commit it.");

            File.WriteAllText(path, currentShape);
            return;
        }

        (int goldenVersion, string goldenShape) = Parse(File.ReadAllText(path));
        bool shapeChanged = Normalize(goldenShape) != Normalize(ShapeBody(currentShape));

        if (shapeChanged && rule.Version == goldenVersion)
            throw new XunitException($"Rule '{rule.RuleId}' emits a different claim shape but is still version {rule.Version}. Bump ISynthesisRule.Version, then re-bless with {BlessVariable}=1.");

        if (rule.Version < goldenVersion)
            throw new XunitException($"Rule '{rule.RuleId}' is version {rule.Version} but its golden records version {goldenVersion}. Versions only go up.");

        if (shapeChanged || rule.Version != goldenVersion)
        {
            if (!bless)
                throw new XunitException($"Rule '{rule.RuleId}' is version {rule.Version} but its golden records version {goldenVersion}. Review the shape and re-bless with {BlessVariable}=1.");

            File.WriteAllText(path, currentShape);
        }
    }

    public static Observation Observation(string type, string analyzerKey, Dictionary<string, Measure> measures) => new()
    {
        Id = ObservationId.NewId(),
        ObservationType = type,
        Subjects = [],
        When = new TemporalExtent { Kind = TemporalKind.Point, Start = new TimeAnchor { Confidence = AnchorConfidence.Unknown } },
        Measures = measures,
        Provenance = new Provenance
        {
            Artifact = new ArtifactId("rule-shape-golden"),
            AnalyzerKey = analyzerKey,
            CapabilitiesUsed = new HashSet<Capability>(),
            Fidelity = FidelityLevel.Full,
        },
        Confidence = 1.0,
    };

    private static async Task<string> DescribeShapeAsync(ISynthesisRule rule, IReadOnlyList<Scenario> scenarios)
    {
        var described = new List<object>(scenarios.Count);
        foreach (Scenario scenario in scenarios)
        {
            var positions = new Dictionary<ObservationId, string>();
            for (int i = 0; i < scenario.Observations.Count; i++)
                positions[scenario.Observations[i].Id] = $"observations[{i}]";

            IReadOnlyList<Claim> claims = await rule.SynthesizeAsync(new ObservationMatchSet(scenario.Observations), new SynthesisContext());
            var claimShapes = new List<object>(claims.Count);
            foreach (Claim claim in claims)
            {
                claimShapes.Add(new
                {
                    claim.Fingerprint,
                    claim.Severity,
                    claim.Assertion,
                    support = claim.Support.Select(s => new { observations = s.Observations.Select(id => positions[id]), s.Why }),
                    counter = claim.Counter is null ? null : new
                    {
                        statements = claim.Counter.Statements.Select(s => new { observations = s.Observations.Select(id => positions[id]), s.Why }),
                        claim.Counter.NoneFoundBecause,
                    },
                    claim.FalsifiedBy,
                });
            }

            described.Add(new { scenario.Name, claims = claimShapes });
        }

        string body = JsonSerializer.Serialize(described, JsonOptions);
        return $"rule: {rule.RuleId}\nversion: {rule.Version}\n{body}\n";
    }

    private static (int Version, string Shape) Parse(string golden)
    {
        string[] lines = golden.Replace("\r\n", "\n").Split('\n');
        int version = int.Parse(lines[1].Substring("version: ".Length));
        return (version, string.Join('\n', lines.Skip(2)));
    }

    private static string ShapeBody(string shape) => string.Join('\n', shape.Split('\n').Skip(2));

    private static string Normalize(string value) => value.Replace("\r\n", "\n").TrimEnd();

    private static string FindGoldenDirectory()
    {
        DirectoryInfo? current = new(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "DumpDetective.slnx")))
            {
                string directory = Path.Combine(current.FullName, "tests", "DumpDetective.Tests", "Golden", "RuleShapes");
                Directory.CreateDirectory(directory);
                return directory;
            }

            current = current.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate DumpDetective.slnx above the test output directory.");
    }
}
