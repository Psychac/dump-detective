namespace DumpDetective.Utilities
{
    internal class OutputWriter
    {
        private readonly StreamWriter? _fileWriter;

        public OutputWriter(StreamWriter? fileWriter)
        {
            _fileWriter = fileWriter;
        }

        public void WriteLine(string message)
        {
            // Write to file only - console is reserved for progress/diagnostics
            _fileWriter?.WriteLine(message);

            // If no file writer, write to console (when user doesn't specify output file)
            if (_fileWriter == null)
            {
                Console.WriteLine(message);
            }
        }

        public void WriteHeader(string title)
        {
            WriteLine($"\n{title}");
            WriteLine(StringConstants.Equals80);
        }

        public void WriteSeparator()
        {
            WriteLine(StringConstants.Separator80);
        }
    }
}
