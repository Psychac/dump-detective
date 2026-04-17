using DumpDetective.Core.Utilities;
using System.IO;

namespace DumpDetective.Reporting.Output;

// TEMP-REFRACTOR-BRIDGE: Remove once OutputWriter is used consistently at reporting boundaries.
internal static class TextWriterExtensions
{
    public static void WriteHeader(this TextWriter writer, string title)
    {
        writer.WriteLine($"\n{title}");
        writer.WriteLine(StringConstants.Equals80);
    }

    public static void WriteSeparator(this TextWriter writer)
    {
        writer.WriteLine(StringConstants.Separator80);
    }
}