namespace DumpDetective.Utilities
{
    internal class OutputWriter
    {
        private readonly TextWriter? _writer;
        private readonly bool _writeToConsoleWhenNoWriter;

        public OutputWriter(TextWriter? writer, bool writeToConsoleWhenNoWriter = true)
        {
            _writer = writer;
            _writeToConsoleWhenNoWriter = writeToConsoleWhenNoWriter;
        }

        public void WriteLine(string message)
        {
            _writer?.WriteLine(message);

            if (_writer == null && _writeToConsoleWhenNoWriter)
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
