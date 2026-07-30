using System.Windows;

namespace ClaudeWorkbench.Launcher.Wpf;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Headless lifecycle check (no GUI), same contract as the WinForms launcher's Program.cs:
        //   --selftest <solution> [logPath]
        if (e.Args.Length >= 2 && string.Equals(e.Args[0], "--selftest", StringComparison.Ordinal))
        {
            int code = SelfTest.Run(e.Args[1], e.Args.Length >= 3 ? e.Args[2] : null).GetAwaiter().GetResult();
            Shutdown(code);
            return;
        }

        MainWindow window = new MainWindow();
        MainWindow = window;
        window.Show();
    }
}
