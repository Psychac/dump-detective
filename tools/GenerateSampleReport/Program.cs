using System.Text.Json;
using System.Text;

string repoRoot = Directory.GetCurrentDirectory();
// If launched from repo root, template paths are relative to repo root
string templatesDir = Path.Combine(repoRoot, "src", "DumpDetective.Reporting", "Templates");
string cssPath = Path.Combine(templatesDir, "report.css");
string templatePath = Path.Combine(templatesDir, "report.html");
string jsPath = Path.Combine(templatesDir, "report.js");

if (!File.Exists(templatePath))
{
    Console.Error.WriteLine($"Template not found: {templatePath}");
    return 1;
}

string template = File.ReadAllText(templatePath);
string css = File.Exists(cssPath) ? File.ReadAllText(cssPath) : "/* missing css */";
string js = File.Exists(jsPath) ? File.ReadAllText(jsPath) : "// missing js";

var sample = new
{
    schemaVersion = "2.1",
    dumpPath = "samples/Date__03_23_2026__Second_Chance_Exception.dmp",
    generatedAtUtc = DateTime.UtcNow,
    elapsedSeconds = 1.23,
    findings = new[] {
        new {
            analyzer = "LeakAnalyzer",
            category = "Memory",
            severity = "Critical",
            title = "Large string retention",
            evidence = "Top type: System.String (1.2 GB retained)",
            recommendation = "Investigate caching patterns and trim interned strings.",
            tags = new string[] { "memory", "string" },
            fingerprint = "fp-1"
        },
        new {
            analyzer = "ModuleAnalyzer",
            category = "Modules",
            severity = "Warning",
            title = "Many loaded modules",
            evidence = "Multiple copies of same assembly loaded in different contexts.",
            recommendation = "Ensure single load context for shared assemblies",
            tags = new string[] { "assembly" },
            fingerprint = "fp-2"
        }
    },
    executiveSummary = new {
        totalManagedBytes = 8200000000L,
        leakLikelihoodScore = 85,
        gcPressureScore = 72,
        threadContentionScore = 12,
        topRecommendations = new object[] { }
    },
    analyzerSections = new[] {
        new {
            analyzerName = "LeakAnalyzer",
            displayTitle = "Leak Analysis",
            sortOrder = 100,
            // Emit compactTables (preferred) instead of legacy table blocks
            compactTables = new[] {
                new {
                    title = "Top types by retained size",
                    headers = new[] { new { name = "Type", type = "string" }, new { name = "Instances", type = "number" }, new { name = "TotalSize", type = "bytes" } },
                    rows = new[] {
                        new { values = new object[] { "System.String", 3402, 1200000000L } },
                        new { values = new object[] { "MyApp.LargeBuffer", 128, 512000000L } }
                    }
                }
            },
            blocks = new object[] { new { type = "heading", text = "Top suspects", indentLevel = 0 } }
        }
    }
};

string json = JsonSerializer.Serialize(sample, new JsonSerializerOptions { WriteIndented = false });

string outHtml = template.Replace("{{CSS}}", css)
    .Replace("{{REPORT_JSON}}", json)
    .Replace("{{JS}}", "<script>" + js + "</script>")
    .Replace("{{PRE_RENDERED_FINDINGS}}", string.Empty)
    .Replace("{{PRE_RENDERED_ANALYZER_SECTIONS}}", string.Empty);

string outPath = Path.Combine(repoRoot, "sample-report.html");
File.WriteAllText(outPath, outHtml, Encoding.UTF8);
Console.WriteLine($"Wrote sample report to: {outPath}");
return 0;
