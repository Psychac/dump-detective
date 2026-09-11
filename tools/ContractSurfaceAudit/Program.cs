// Phase 0 item 1 (docs/refactor/modularity/phase-0-foundation.md): contract surface audit.
// Generates docs/refactor/modularity/contract-surface-inventory.md from source, on demand.
// Run: dotnet run --project tools/ContractSurfaceAudit
//
// Syntax-only (no MSBuild/semantic model): member/type touches are found by regex over each
// analyzer class's own text, matched against a known member/type vocabulary gathered from the
// actual interface declarations and from `internal` type declarations in the assemblies that
// grant DumpDetective.Analysis InternalsVisibleTo. This is the same grep-derived-not-exhaustive
// standard the project already applies to this kind of audit (see modularity-plan.md's own
// "grep-derived, not exhaustively verified line-by-line" 35-analyzer tier split) — good enough to
// scope Phase 1's SDK surface, corrected per-analyzer as each one is actually migrated.

using System.Text;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

string repoRoot = FindRepoRoot(AppContext.BaseDirectory);
string analyzersDir = Path.Combine(repoRoot, "src", "DumpDetective.Analysis", "Analyzers");
string coreDir = Path.Combine(repoRoot, "src", "DumpDetective.Core");
string platformDir = Path.Combine(repoRoot, "src", "DumpDetective.Platform");
string outputPath = Path.Combine(repoRoot, "docs", "refactor", "modularity", "contract-surface-inventory.md");

var internalTypesByProject = new (string Project, string Dir)[]
{
    ("DumpDetective.Core", coreDir),
    ("DumpDetective.Platform", platformDir),
};

var internalTypeOwners = new Dictionary<string, string>(StringComparer.Ordinal);
foreach ((string project, string dir) in internalTypesByProject)
{
    foreach (string name in CollectInternalTypeNames(dir))
    {
        internalTypeOwners.TryAdd(name, project);
    }
}

string[] cacheMemberNames =
[
    "ObjectScanCount", "CacheHits", "CacheMisses", "SizeTier",
    "GetStaticRootedAddresses", "GetPinnedRootedAddresses", "GetStaticFieldsByRootAddress",
    "TryResolveStackFrameOwner", "GetOrBuildTypeStatistics", "GetSampleInstanceAddress",
    "GetOrBuildValidRoots", "GetOrBuildRootTriples", "GetOrCountThreadStackRoots",
    "MethodTableHasOutgoingRefs", "EnumerateIndexedEntriesAsTuples", "TryGetObjectMetadata",
    "TryGetReverseIndexProvider", "TryGetForwardIndexProvider", "TryGetReachableAddressProvider",
    "TryGetDominatorTreeProvider", "TryGetThreadRetentionProvider", "TryGetGlobalSizeBuckets",
    "TryGetDistinctMethodTables",
];

string[] ignoredContextMemberNames = ["GetType", "Equals", "ToString", "GetHashCode"];

var rows = new List<AnalyzerRow>();

foreach (string file in Directory.GetFiles(analyzersDir, "*.cs", SearchOption.TopDirectoryOnly)
    .OrderBy(f => f, StringComparer.Ordinal))
{
    string text = File.ReadAllText(file);
    var tree = CSharpSyntaxTree.ParseText(text, path: file);
    var root = tree.GetCompilationUnitRoot();

    // "IAnalyzer" directly, or a marker sub-interface like IDeferredAnalyzer : IAnalyzer
    // (LeakCandidateAnalyzer) — syntax-only, so this is a fixed allowlist, not transitive
    // interface resolution.
    ClassDeclarationSyntax? classDecl = root.DescendantNodes()
        .OfType<ClassDeclarationSyntax>()
        .FirstOrDefault(c => c.BaseList?.Types.Any(t => GetSimpleName(t.Type) is "IAnalyzer" or "IDeferredAnalyzer") == true);
    if (classDecl is null)
    {
        continue;
    }

    string className = classDecl.Identifier.Text;
    bool isSdkAnalyzer = root.Usings.Any(u => u.Name?.ToString() == "DumpDetective.Sdk.Analysis");
    string variant = isSdkAnalyzer ? "SDK (`Sdk.Analysis.IAnalyzer`)" : "Legacy (`Core.Abstractions.IAnalyzer`)";

    List<string> ctorParamTypes = classDecl.Members
        .OfType<ConstructorDeclarationSyntax>()
        .SelectMany(c => c.ParameterList.Parameters)
        .Select(p => p.Type?.ToString() ?? "?")
        .Distinct(StringComparer.Ordinal)
        .OrderBy(s => s, StringComparer.Ordinal)
        .ToList();

    string classBodyText = classDecl.ToFullString();

    List<string> contextMembers = Regex.Matches(classBodyText, @"\bcontext\.([A-Za-z_][A-Za-z0-9_]*)")
        .Select(m => m.Groups[1].Value)
        .Where(n => !ignoredContextMemberNames.Contains(n))
        .Distinct(StringComparer.Ordinal)
        .OrderBy(s => s, StringComparer.Ordinal)
        .ToList();

    List<string> cacheMembers = cacheMemberNames
        .Where(m => Regex.IsMatch(classBodyText, $@"\b{Regex.Escape(m)}\b"))
        .OrderBy(s => s, StringComparer.Ordinal)
        .ToList();

    List<string> internalTypesTouched = internalTypeOwners.Keys
        .Where(n => Regex.IsMatch(classBodyText, $@"\b{Regex.Escape(n)}\b"))
        .OrderBy(s => s, StringComparer.Ordinal)
        .ToList();

    rows.Add(new AnalyzerRow(
        className,
        Path.GetFileName(file),
        variant,
        ctorParamTypes,
        contextMembers,
        cacheMembers,
        internalTypesTouched));
}

WriteReport(outputPath, rows, internalTypeOwners);
Console.WriteLine($"Wrote {rows.Count} analyzer rows to {outputPath}");

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

static IEnumerable<string> CollectInternalTypeNames(string projectDir)
{
    var regex = new Regex(
        @"internal(?:\s+(?:sealed|abstract|static|partial|readonly))*\s+(?:class|interface|struct|enum|record(?:\s+(?:class|struct))?)\s+([A-Za-z_][A-Za-z0-9_]*)",
        RegexOptions.Compiled);

    foreach (string file in Directory.EnumerateFiles(projectDir, "*.cs", SearchOption.AllDirectories))
    {
        if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal) ||
            file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
        {
            continue;
        }

        string text = File.ReadAllText(file);
        foreach (Match match in regex.Matches(text))
        {
            yield return match.Groups[1].Value;
        }
    }
}

static void WriteReport(string outputPath, List<AnalyzerRow> rows, Dictionary<string, string> internalTypeOwners)
{
    var sb = new StringBuilder();
    sb.AppendLine("# Contract surface inventory");
    sb.AppendLine();
    sb.AppendLine("Generated by `tools/ContractSurfaceAudit` — **do not hand-edit**. Re-run");
    sb.AppendLine("`dotnet run --project tools/ContractSurfaceAudit` to refresh after analyzer changes.");
    sb.AppendLine();
    sb.AppendLine(
        "Phase 0 item 1 (see [phase-0-foundation.md](phase-0-foundation.md)): for every " +
        "`IAnalyzer` implementation, what it touches outside its own file — constructor " +
        "parameters, `AnalysisContext` members read, `IHeapAnalysisCache` members called, and " +
        "`internal` types reached only because `DumpDetective.Core`/`DumpDetective.Platform` " +
        "grant `DumpDetective.Analysis` `InternalsVisibleTo`. This is exactly the surface Phase " +
        "1's SDK must expose — nothing more.");
    sb.AppendLine();
    sb.AppendLine(
        "Detection is syntax-only regex over each analyzer class's own text (no MSBuild/semantic " +
        "model), matched against the real member lists of `IHeapAnalysisCache` and the real " +
        "`internal` type declarations below. False positives are possible where an unrelated " +
        "identifier collides with one of these names; false negatives are possible where a " +
        "member is reached through an intermediate local/field alias rather than `context.` " +
        "directly. Treat this as a starting inventory to verify per-analyzer during migration, " +
        "the same standard the project already applies to this kind of audit " +
        "([modularity-plan.md § 10 point 8](../modularity-plan.md#10-external-review-2026-09-08--where-this-can-be-questioned)).");
    sb.AppendLine();

    int sdkCount = rows.Count(r => r.Variant.StartsWith("SDK", StringComparison.Ordinal));
    sb.AppendLine($"**{rows.Count} `IAnalyzer` implementations found — {sdkCount} already retyped onto " +
                   $"the SDK, {rows.Count - sdkCount} still on `Core.Abstractions.IAnalyzer`.**");
    sb.AppendLine();

    sb.AppendLine("## Internal types considered");
    sb.AppendLine();
    sb.AppendLine(
        "`internal` types declared in projects that grant `DumpDetective.Analysis` " +
        "`InternalsVisibleTo` (per each project's `.csproj`), i.e. types an analyzer can only " +
        "reach because of that grant, not because the type is `public`.");
    sb.AppendLine();
    sb.AppendLine("| Type | Declaring project |");
    sb.AppendLine("|---|---|");
    foreach ((string name, string project) in internalTypeOwners.OrderBy(kv => kv.Key, StringComparer.Ordinal))
    {
        sb.AppendLine($"| `{name}` | `{project}` |");
    }
    sb.AppendLine();

    sb.AppendLine("## Per-analyzer contract surface");
    sb.AppendLine();
    sb.AppendLine("| Analyzer | Variant | Constructor params | `AnalysisContext` members read | `IHeapAnalysisCache` members touched | Internal types touched (cross-assembly) |");
    sb.AppendLine("|---|---|---|---|---|---|");
    foreach (AnalyzerRow row in rows)
    {
        sb.AppendLine(
            $"| `{row.ClassName}` | {row.Variant} | {FormatList(row.ConstructorParamTypes)} | " +
            $"{FormatList(row.ContextMembers)} | {FormatList(row.CacheMembers)} | " +
            $"{FormatList(row.InternalTypesTouched)} |");
    }
    sb.AppendLine();

    Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
    File.WriteAllText(outputPath, sb.ToString());
}

static string FormatList(IReadOnlyCollection<string> values)
{
    if (values.Count == 0)
    {
        return "—";
    }
    return string.Join("<br>", values.Select(v => $"`{v}`"));
}

internal sealed record AnalyzerRow(
    string ClassName,
    string FileName,
    string Variant,
    List<string> ConstructorParamTypes,
    List<string> ContextMembers,
    List<string> CacheMembers,
    List<string> InternalTypesTouched);
