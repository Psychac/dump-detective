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
            Console.WriteLine(message);
            _fileWriter?.WriteLine(message);
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
