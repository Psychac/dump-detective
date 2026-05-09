using System;
using System.IO;
using Xunit;

namespace DumpDetective.Tests
{
    public class ReportingVisualsTests
    {
        [Fact]
        public void ReportTemplateExists()
        {
            var baseDir = AppContext.BaseDirectory;
            var path = Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", "..", "src", "DumpDetective.Reporting", "Templates", "report-template.html"));
            Assert.True(File.Exists(path), $"Expected report template at {path}");
            var content = File.ReadAllText(path);
            Assert.Contains("<main", content);
        }
    }
}
