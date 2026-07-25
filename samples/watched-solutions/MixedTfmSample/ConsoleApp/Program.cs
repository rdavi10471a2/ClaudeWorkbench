using Shared;

namespace ConsoleApp;

internal static class Program
{
    private static void Main()
    {
        Console.WriteLine(SharedGreeter.Greet("console (net8.0)"));
    }
}
