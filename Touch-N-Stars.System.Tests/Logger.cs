namespace NINA.Core.Utility;

// Only the NINA logging dependency is replaced in this isolated test assembly.
internal static class Logger
{
    public static void Warning(string message) => System.Console.Error.WriteLine(message);
    public static void Debug(string message) => System.Console.Error.WriteLine(message);
    public static void Error(string message) => System.Console.Error.WriteLine(message);
}
