namespace ClaudeWorkbench.Launcher;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        // Headless lifecycle check (no GUI): --selftest <solution> <logPath>
        if (args.Length >= 2 && string.Equals(args[0], "--selftest", StringComparison.Ordinal))
        {
            return SelfTest.Run(args[1], args.Length >= 3 ? args[2] : null).GetAwaiter().GetResult();
        }

        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm());
        return 0;
    }
}
