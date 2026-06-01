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
            var path = Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", "..", "..", "src", "DumpDetective.Reporting", "Templates", "report-template.html"));
            Assert.True(File.Exists(path), $"Expected report template at {path}");
            var content = File.ReadAllText(path);
            Assert.Contains("<main", content);
        }

        [Fact]
        public void HeaderRenderer_ShouldExposeReadingModeToggleId()
        {
            var baseDir = AppContext.BaseDirectory;
            var path = Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", "..", "..", "src", "DumpDetective.Reporting", "Templates", "report.renderers.header.js"));
            Assert.True(File.Exists(path), $"Expected header renderer at {path}");
            var content = File.ReadAllText(path);
            Assert.Contains("reading-mode-toggle", content);
            Assert.Contains("trend-story-card", content);
            Assert.Contains("What changed first", content);
            Assert.Contains("header-body", content);
        }

        [Fact]
        public void PanelsRenderer_ShouldExposeTicketTemplateMenuId()
        {
            var baseDir = AppContext.BaseDirectory;
            var path = Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", "..", "..", "src", "DumpDetective.Reporting", "Templates", "report.renderers.panels.js"));
            Assert.True(File.Exists(path), $"Expected panels renderer at {path}");
            var content = File.ReadAllText(path);
            Assert.Contains("ticket-template-menu", content);
            Assert.Contains("Copy ADO", content);
            Assert.Contains("Copy Jira", content);
            Assert.Contains("Copy GitHub", content);
            Assert.Contains("action-triage-lanes", content);
        }

        [Fact]
        public void SectionsRenderer_ShouldExposeLazyHydrationMarkers()
        {
            var baseDir = AppContext.BaseDirectory;
            var path = Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", "..", "..", "src", "DumpDetective.Reporting", "Templates", "report.renderers.sections.js"));
            Assert.True(File.Exists(path), $"Expected sections renderer at {path}");
            var content = File.ReadAllText(path);
            Assert.Contains("dataset.lazyHydrate", content);
            Assert.Contains("table-lazy-hint", content);
            Assert.Contains("dataset.responsiveStack", content);
        }

        [Fact]
        public void UtilitiesCss_ShouldExposeResponsiveStackAndClampStyles()
        {
            var baseDir = AppContext.BaseDirectory;
            var path = Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", "..", "..", "src", "DumpDetective.Reporting", "Templates", "report.utilities.css"));
            Assert.True(File.Exists(path), $"Expected utilities css at {path}");
            var content = File.ReadAllText(path);
            Assert.Contains("table[data-responsive-stack=\"1\"]", content);
            Assert.Contains("table-cell-clamp__text", content);
            Assert.Contains("table-cell-clamp__toggle", content);
        }

        [Fact]
        public void UiRenderer_ShouldExposeReadingModeAndDynamicSectionMarkers()
        {
            var baseDir = AppContext.BaseDirectory;
            var path = Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", "..", "..", "src", "DumpDetective.Reporting", "Templates", "report.ui.js"));
            Assert.True(File.Exists(path), $"Expected UI renderer at {path}");
            var content = File.ReadAllText(path);
            Assert.Contains("dumpdetective:reading-mode", content);
            Assert.Contains(".report-domain__details", content);
            Assert.Contains(".analyzer-section > details:not(.provenance)", content);
            Assert.Contains("dumpdetective:sections-rendered", content);
            Assert.Contains("dumpdetective:domain-sections-appended", content);
        }

        [Fact]
        public void UiIntegrityModule_ShouldExposeAnchorIntegrityContract()
        {
            var baseDir = AppContext.BaseDirectory;
            var path = Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", "..", "..", "src", "DumpDetective.Reporting", "Templates", "report.ui.integrity.js"));
            Assert.True(File.Exists(path), $"Expected UI integrity module at {path}");
            var content = File.ReadAllText(path);
            Assert.Contains("render-integrity-report", content);
            Assert.Contains("duplicateIdCount", content);
            Assert.Contains("brokenAnchorCount", content);
            Assert.Contains("data-broken-anchor", content);
        }

        [Fact]
        public void UiTocModule_ShouldExposeFocusedFilterContract()
        {
            var baseDir = AppContext.BaseDirectory;
            var path = Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", "..", "..", "src", "DumpDetective.Reporting", "Templates", "report.ui.toc.js"));
            Assert.True(File.Exists(path), $"Expected UI TOC module at {path}");
            var content = File.ReadAllText(path);
            Assert.Contains("export function filterTocList", content);
            Assert.Contains("item.hidden", content);
        }
    }
}
