using System.Text;
using System.Reflection;
using System.Text;

internal static class Program
{
    private static int Main()
    {
        // use reflection to construct internal model types from the Reporting assembly
        Assembly repAsm = Assembly.Load("DumpDetective.Reporting");
        Type docType = repAsm.GetType("DumpDetective.Reporting.Models.AnalysisReportDocument", true);
        Type findingType = repAsm.GetType("DumpDetective.Reporting.Models.FindingRecord", true);

        var fixtures = BuildFixtures(findingType);

        var outBase = Path.Combine(Directory.GetCurrentDirectory(), "..", "..", "tests", "DumpDetective.Tests", "Golden", "Baselines");
        Directory.CreateDirectory(outBase);

        // formatter types (internal) via reflection
        Type textFmtType = repAsm.GetType("DumpDetective.Reporting.Formatters.TextCanonicalReportFormatter", true);
        Type mdFmtType = repAsm.GetType("DumpDetective.Reporting.Formatters.MarkdownCanonicalReportFormatter", true);
        Type jsonFmtType = repAsm.GetType("DumpDetective.Reporting.Formatters.JsonCanonicalReportFormatter", true);

        object textFmt = Activator.CreateInstance(textFmtType, nonPublic: true)!;
        object mdFmt = Activator.CreateInstance(mdFmtType, nonPublic: true)!;
        object jsonFmt = Activator.CreateInstance(jsonFmtType, nonPublic: true)!;

        MethodInfo renderText = textFmtType.GetMethod("Render", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!;
        MethodInfo renderMd = mdFmtType.GetMethod("Render", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!;
        MethodInfo renderJson = jsonFmtType.GetMethod("Render", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!;

        foreach (var kv in fixtures)
        {
            string name = kv.Key;
            object doc = kv.Value;

            string text = (string)renderText.Invoke(textFmt, new object[] { doc })!;
            string md = (string)renderMd.Invoke(mdFmt, new object[] { doc })!;
            string js = (string)renderJson.Invoke(jsonFmt, new object[] { doc })!;

            var textDir = Path.Combine(outBase, "Text"); Directory.CreateDirectory(textDir);
            File.WriteAllText(Path.Combine(textDir, name + ".text.golden"), text, Encoding.UTF8);

            var mdDir = Path.Combine(outBase, "Markdown"); Directory.CreateDirectory(mdDir);
            File.WriteAllText(Path.Combine(mdDir, name + ".markdown.golden"), md, Encoding.UTF8);

            var jsonDir = Path.Combine(outBase, "Json"); Directory.CreateDirectory(jsonDir);
            File.WriteAllText(Path.Combine(jsonDir, name + ".json.golden"), js, Encoding.UTF8);
        }

        Console.WriteLine("Wrote golden baselines to: " + Path.GetFullPath(outBase));
        return 0;
    }

    private static Dictionary<string, object> BuildFixtures(Type findingType)
    {
        var fixtures = new Dictionary<string, object>(StringComparer.Ordinal);

        fixtures["BaselineSmall"] = MakeDoc(
            dumpPath: "C:/fixtures/BaselineSmall.dmp",
            elapsed: 12.3,
            findings: new[] {
                MakeFinding(findingType, "MemoryLeakAnalyzer", "Leak", "Warning", "Leak pressure", "Detected duplicate strings.\n- Analyzer: MemoryLeakAnalyzer\n- Value: System.String duplicated", "Pool repeated string payloads.", new[] { "baseline-small" }, "baseline-small")
            }
        );

        fixtures["DuplicateHeavy"] = MakeDoc(
            dumpPath: "C:/fixtures/DuplicateHeavy.dmp",
            elapsed: 8.1,
            findings: new[] {
                MakeFinding(findingType, "MemoryLeakAnalyzer", "Leak", "Critical", "Duplicate-heavy merged section", "Merged duplicate leak evidence from multiple analyzers.\n- EvidenceA: A repeated payload instance\n- EvidenceB: Another repeated payload instance", "Deduplicate payload cache keys. Review object retention roots.", new[] { "dup-heavy" }, "dup-heavy")
            }
        );

        fixtures["LongNames"] = MakeDoc(
            dumpPath: "C:/fixtures/LongNames.dmp",
            elapsed: 4.2,
            findings: new[] {
                MakeFinding(findingType, "MemoryAnalyzer", "Memory", "Warning", "Long member/type names", "Long identifiers are preserved end-to-end.\n- Type: VeryLongTypeName_ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789ABCDEFGHIJKLMNOP\n- Member: VeryLongMemberName_ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789ABCDEFGHIJKLMN", "Keep full value visibility; do not truncate.", new[] { "long-names" }, "long-names")
            }
        );

        fixtures["RichEvidence"] = MakeDoc(
            dumpPath: "C:/fixtures/RichEvidence.dmp",
            elapsed: 9.8,
            findings: new[] {
                MakeFinding(findingType, "CrashAnalyzer", "Crash", "Warning", "Rich evidence sample", "Includes multiple evidence and remediation records.\n- Thread: 42\n- Exception: System.NullReferenceException\n- StackTop: Service.ProcessRequest", "Guard null dereferences. Add targeted telemetry around request processing.", new[] { "rich-evidence" }, "rich-evidence")
            }
        );

        fixtures["MixedSeverity"] = MakeDoc(
            dumpPath: "C:/fixtures/MixedSeverity.dmp",
            elapsed: 6.6,
            findings: new[] {
                MakeFinding(findingType, "LeakAnalyzer", "Leak", "Critical", "Critical leak", "Critical item", "Handle now", new[] { "sev-critical" }, "sev-critical"),
                MakeFinding(findingType, "LeakAnalyzer", "Leak", "Warning", "Warning leak", "Warning item", "Plan remediation", new[] { "sev-warning" }, "sev-warning"),
                MakeFinding(findingType, "LeakAnalyzer", "Info", "Info", "Info signal", "Informational item", "Observe", new[] { "sev-info" }, "sev-info")
            }
        );

        return fixtures;
    }

    private static object MakeDoc(string dumpPath, double elapsed, object[] findings)
    {
        Assembly repAsm = Assembly.Load("DumpDetective.Reporting");
        Type docType = repAsm.GetType("DumpDetective.Reporting.Models.AnalysisReportDocument", true);
        object doc = Activator.CreateInstance(docType, nonPublic: true)!;
        // set properties
        docType.GetProperty("DumpPath", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!.SetValue(doc, dumpPath);
        docType.GetProperty("GeneratedAtUtc", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!.SetValue(doc, new DateTime(2026,1,1,12,0,0,DateTimeKind.Utc));
        docType.GetProperty("ElapsedSeconds", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!.SetValue(doc, elapsed);

        // create List<FindingRecord>
        Type findingType = repAsm.GetType("DumpDetective.Reporting.Models.FindingRecord", true);
        Type listType = typeof(List<>).MakeGenericType(findingType);
        object list = Activator.CreateInstance(listType)!;
        MethodInfo add = listType.GetMethod("Add")!;
        foreach (var f in findings) add.Invoke(list, new[] { f });

        docType.GetProperty("Findings", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!.SetValue(doc, list);

        return doc;
    }

    private static object MakeFinding(Type findingType, string analyzer, string category, string severity, string title, string evidence, string recommendation, string[] tags, string fingerprint)
    {
        // try to find ctor with matching parameter count
        var ctors = findingType.GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        foreach (var c in ctors)
        {
            var ps = c.GetParameters();
            if (ps.Length >= 8)
            {
                object?[] args = new object?[] { analyzer, category, severity, title, evidence, recommendation, tags.ToList(), fingerprint };
                return c.Invoke(args)!;
            }
        }
        // fallback - create via Activator and set props
        object inst = Activator.CreateInstance(findingType, nonPublic: true)!;
        findingType.GetProperty("Analyzer", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!.SetValue(inst, analyzer);
        findingType.GetProperty("Category", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!.SetValue(inst, category);
        findingType.GetProperty("Severity", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!.SetValue(inst, severity);
        findingType.GetProperty("Title", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!.SetValue(inst, title);
        findingType.GetProperty("Evidence", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!.SetValue(inst, evidence);
        findingType.GetProperty("Recommendation", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!.SetValue(inst, recommendation);
        findingType.GetProperty("Tags", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!.SetValue(inst, tags.ToList());
        findingType.GetProperty("Fingerprint", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!.SetValue(inst, fingerprint);
        return inst;
    }

    
}