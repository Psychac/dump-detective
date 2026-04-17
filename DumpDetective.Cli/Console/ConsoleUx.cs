namespace DumpDetective.Cli.Console;

internal static class ConsoleUx
{
    public static void Info(string message) => System.Console.WriteLine($"[INFO] {message}");

    public static void Error(string message) => System.Console.Error.WriteLine($"[ERROR] {message}");

    public static void Success(string message) => System.Console.WriteLine($"[OK] {message}");
}
