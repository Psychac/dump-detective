using System.Reflection;

namespace DumpDetective.Reporting.Formatters;

/// <summary>
/// Loads text resources embedded in this assembly.
/// Used by <see cref="HtmlReportRenderer"/> to read report.html / report.css / report.js.
/// Resources are expected under the <c>DumpDetective.Reporting.Templates.</c> prefix.
/// </summary>
internal static class EmbeddedResourceLoader
{
    internal static string LoadText(string resourceName)
    {
        Assembly asm = typeof(EmbeddedResourceLoader).Assembly;
        string fullName = $"DumpDetective.Reporting.Templates.{resourceName}";
        using Stream stream = asm.GetManifestResourceStream(fullName)
            ?? throw new InvalidOperationException($"Embedded resource not found: {fullName}. Ensure the file is included with <EmbeddedResource> in DumpDetective.Reporting.csproj.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
