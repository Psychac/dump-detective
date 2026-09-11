// Phase 0 item 5 (docs/refactor/modularity/phase-0-foundation.md): characterization test coverage
// gap analysis. Generates docs/refactor/modularity/characterization-coverage.md from source.
// Run: dotnet run --project tools/CharacterizationCoverageAudit
//
// Scope: the 36 IFindingGenerator implementations in src/DumpDetective.Reporting/FindingGenerators/
// — this is where "distinct severity tiers and decision branches" (the doc's own phrasing) live,
// since severity is decided here, not in the analyzers themselves (analyzers emit raw
// AnalyzerDomainResult facts; generators turn them into FindingSeverity-tagged InsightFindings).
//
// Two scripted signals per generator, both syntax-only (Roslyn, no semantic model):
//   1. Decision branches: count of `new InsightFinding(` construction sites in the generator's
//      Generate() call graph — each is a distinct finding kind/branch the logic can reach.
//   2. Severities: distinct FindingSeverity.X tokens the generator can produce, vs. distinct
//      FindingSeverity.X tokens actually asserted in test methods that reference this generator by
//      name (method-level attribution via Roslyn, not whole-file, since several generators share
//      one test file e.g. InfrastructureFindingGeneratorTests.cs).
//
// This is a starting gap signal, not a certificate of correctness — same standard as this
// project's other syntax-only audits (contract-surface-inventory.md, capability-mapping.md):
// approximate, scripted, meant to be corrected per-generator as tests are actually written.

using System.Text;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

string repoRoot = FindRepoRoot(AppContext.BaseDirectory);
string generatorsDir = Path.Combine(repoRoot, "src", "DumpDetective.Reporting", "FindingGenerators");
string testsDir = Path.Combine(repoRoot, "tests", "DumpDetective.Tests");
string outputPath = Path.Combine(repoRoot, "docs", "refactor", "modularity", "characterization-coverage.md");

var generators = new List<GeneratorInfo>();

foreach (string file in Directory.GetFiles(generatorsDir, "*.cs", SearchOption.TopDirectoryOnly)
    .OrderBy(f => f, StringComparer.Ordinal))
{
    string text = File.ReadAllText(file);
    var tree = CSharpSyntaxTree.ParseText(text, path: file);
    var root = tree.GetCompilationUnitRoot();

    ClassDeclarationSyntax? classDecl = root.DescendantNodes()
        .OfType<ClassDeclarationSyntax>()
        .FirstOrDefault(c => c.BaseList?.Types.Any(t => GetSimpleName(t.Type) == "IFindingGenerator") == true);
    if (classDecl is null)
    {
        continue;
    }

    string className = classDecl.Identifier.Text;
    string classBody = classDecl.ToFullString();

    int callSites = Regex.Matches(classBody, @"\bnew InsightFinding\(").Count;
    var severities = new SortedSet<string>(
        Regex.Matches(classBody, @"FindingSeverity\.(Info|Warning|Critical)")
            .Select(m => m.Groups[1].Value),
        StringComparer.Ordinal);

    generators.Add(new GeneratorInfo(className, file, callSites, severities));
}

var testMethodsByGenerator = new Dictionary<string, List<TestMethodHit>>(StringComparer.Ordinal);
foreach (GeneratorInfo g in generators)
{
    testMethodsByGenerator[g.ClassName] = new List<TestMethodHit>();
}

foreach (string file in Directory.GetFiles(testsDir, "*.cs", SearchOption.AllDirectories))
{
    string text = File.ReadAllText(file);
    if (!generators.Any(g => text.Contains(g.ClassName, StringComparison.Ordinal)))
    {
        continue;
    }

    var tree = CSharpSyntaxTree.ParseText(text, path: file);
    var root = tree.GetCompilationUnitRoot();

    foreach (ClassDeclarationSyntax testClass in root.DescendantNodes().OfType<ClassDeclarationSyntax>())
    {
        // Common xUnit pattern: `private readonly XxxFindingGenerator _gen = new();` declared once,
        // referenced only by variable name inside each [Fact] — a plain class-name text search over
        // the method body alone misses every one of these. Map field-name -> generator class name
        // from the field's declared type (or its `new Xxx(...)` initializer, for `var`/inferred
        // fields) so method-level attribution also catches fixture-field usage.
        var fieldToGenerator = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (FieldDeclarationSyntax field in testClass.Members.OfType<FieldDeclarationSyntax>())
        {
            string declaredTypeName = GetSimpleName(field.Declaration.Type);
            foreach (VariableDeclaratorSyntax v in field.Declaration.Variables)
            {
                string? matchedGenerator = generators.FirstOrDefault(g => g.ClassName == declaredTypeName)?.ClassName;
                if (matchedGenerator is null && v.Initializer?.Value is ObjectCreationExpressionSyntax oce)
                {
                    string createdTypeName = GetSimpleName(oce.Type);
                    matchedGenerator = generators.FirstOrDefault(g => g.ClassName == createdTypeName)?.ClassName;
                }
                if (matchedGenerator is not null)
                {
                    fieldToGenerator[v.Identifier.Text] = matchedGenerator;
                }
            }
        }

        foreach (MethodDeclarationSyntax method in testClass.Members.OfType<MethodDeclarationSyntax>())
        {
            bool isTest = method.AttributeLists
                .SelectMany(al => al.Attributes)
                .Any(a => GetSimpleAttributeName(a.Name) is "Fact" or "Theory");
            if (!isTest)
            {
                continue;
            }

            string methodBody = method.ToFullString();
            var severitiesHere = new SortedSet<string>(
                Regex.Matches(methodBody, @"FindingSeverity\.(Info|Warning|Critical)")
                    .Select(m => m.Groups[1].Value),
                StringComparer.Ordinal);

            var attributedGenerators = new HashSet<string>(StringComparer.Ordinal);
            foreach (GeneratorInfo g in generators)
            {
                if (methodBody.Contains(g.ClassName, StringComparison.Ordinal))
                {
                    attributedGenerators.Add(g.ClassName);
                }
            }
            foreach ((string fieldName, string generatorName) in fieldToGenerator)
            {
                if (Regex.IsMatch(methodBody, $@"\b{Regex.Escape(fieldName)}\b"))
                {
                    attributedGenerators.Add(generatorName);
                }
            }

            foreach (string generatorName in attributedGenerators)
            {
                testMethodsByGenerator[generatorName].Add(new TestMethodHit(file, method.Identifier.Text, severitiesHere));
            }
        }
    }
}

var rows = new List<CoverageRow>();
foreach (GeneratorInfo g in generators)
{
    List<TestMethodHit> hits = testMethodsByGenerator[g.ClassName];
    var testedSeverities = new SortedSet<string>(StringComparer.Ordinal);
    foreach (TestMethodHit h in hits)
    {
        foreach (string s in h.Severities)
        {
            testedSeverities.Add(s);
        }
    }
    var missing = new SortedSet<string>(g.Severities.Except(testedSeverities), StringComparer.Ordinal);
    var testFiles = hits.Select(h => Path.GetFileName(h.File)).Distinct().OrderBy(f => f, StringComparer.Ordinal).ToList();

    rows.Add(new CoverageRow(g.ClassName, g.CallSites, g.Severities, hits.Count, testFiles, missing));
}

WriteReport(outputPath, rows);
Console.WriteLine($"Wrote {rows.Count} generator rows to {outputPath}");

static string GetSimpleAttributeName(NameSyntax name) => name switch
{
    QualifiedNameSyntax q => q.Right.Identifier.Text,
    IdentifierNameSyntax id => id.Identifier.Text,
    GenericNameSyntax g => g.Identifier.Text,
    _ => name.ToString(),
};

static string GetSimpleName(TypeSyntax type) => type switch
{
    QualifiedNameSyntax q => q.Right.Identifier.Text,
    IdentifierNameSyntax id => id.Identifier.Text,
    GenericNameSyntax g => g.Identifier.Text,
    _ => type.ToString(),
};

static string FindRepoRoot(string startDir)
{
    var dir = new DirectoryInfo(startDir);
    while (dir is not null)
    {
        if (Directory.Exists(Path.Combine(dir.FullName, "src", "DumpDetective.Core")))
        {
            return dir.FullName;
        }
        dir = dir.Parent;
    }
    throw new InvalidOperationException($"Could not locate repo root (src/DumpDetective.Core) above '{startDir}'.");
}

static void WriteReport(string outputPath, List<CoverageRow> rows)
{
    var sb = new StringBuilder();
    sb.AppendLine("# Characterization test coverage");
    sb.AppendLine();
    sb.AppendLine("Generated by `tools/CharacterizationCoverageAudit` — do not hand-edit. Re-run:");
    sb.AppendLine("`dotnet run --project tools/CharacterizationCoverageAudit`.");
    sb.AppendLine();
    sb.AppendLine(
        "Phase 0 item 5 (see [phase-0-foundation.md](phase-0-foundation.md)): gap analysis, not a " +
        "certificate of correctness. Scope is the 36 `IFindingGenerator` implementations in " +
        "`src/DumpDetective.Reporting/FindingGenerators/` — severity is decided there, not in the " +
        "analyzers. Two scripted signals: **decision branches** (count of `new InsightFinding(` " +
        "construction sites reachable in the generator, a proxy for distinct finding kinds) and " +
        "**severities** (distinct `FindingSeverity` values the generator can produce vs. values " +
        "actually asserted in `[Fact]`/`[Theory]` methods that reference the generator by name — " +
        "method-level attribution via Roslyn, since several generators share one test file, e.g. " +
        "`InfrastructureFindingGeneratorTests.cs`).");
    sb.AppendLine();

    int zeroTests = rows.Count(r => r.TestMethodCount == 0);
    int missingSeverity = rows.Count(r => r.MissingSeverities.Count > 0);
    int branchesExceedTests = rows.Count(r => r.TestMethodCount < r.CallSites);
    sb.AppendLine($"**{rows.Count} generators audited — {zeroTests} have zero attributable test " +
                   $"methods, {missingSeverity} produce a severity no attributed test asserts, " +
                   $"{branchesExceedTests} have fewer test methods than finding-construction call sites.**");
    sb.AppendLine();

    sb.AppendLine("| Generator | Decision branches (`new InsightFinding(` sites) | Severities producible | Test methods found | Test file(s) | Severities never asserted |");
    sb.AppendLine("|---|---|---|---|---|---|");
    foreach (CoverageRow r in rows.OrderByDescending(r => r.MissingSeverities.Count)
        .ThenByDescending(r => r.CallSites - r.TestMethodCount)
        .ThenBy(r => r.GeneratorName, StringComparer.Ordinal))
    {
        string severities = r.SourceSeverities.Count == 0 ? "—" : string.Join(", ", r.SourceSeverities.Select(s => $"`{s}`"));
        string testFiles = r.TestFiles.Count == 0 ? "**none**" : string.Join("<br>", r.TestFiles.Select(f => $"`{f}`"));
        string missing = r.MissingSeverities.Count == 0 ? "—" : string.Join(", ", r.MissingSeverities.Select(s => $"**`{s}`**"));
        sb.AppendLine($"| `{r.GeneratorName}` | {r.CallSites} | {severities} | {r.TestMethodCount} | {testFiles} | {missing} |");
    }
    sb.AppendLine();

    Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
    File.WriteAllText(outputPath, sb.ToString());
}

internal sealed record GeneratorInfo(string ClassName, string File, int CallSites, SortedSet<string> Severities);
internal sealed record TestMethodHit(string File, string MethodName, SortedSet<string> Severities);
internal sealed record CoverageRow(
    string GeneratorName,
    int CallSites,
    SortedSet<string> SourceSeverities,
    int TestMethodCount,
    List<string> TestFiles,
    SortedSet<string> MissingSeverities);
