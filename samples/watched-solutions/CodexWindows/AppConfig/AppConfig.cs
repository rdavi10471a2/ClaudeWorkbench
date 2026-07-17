namespace CodexWindows.Configuration;

public sealed class AppConfig
{
    public bool InitiaWorkflowTestPassed3 { get; set; } = true;

    public string WindowTitle { get; set; } = "Codex Windows Sample";

    public static AppConfig CreateDefault() =>
        new()
        {
            InitiaWorkflowTestPassed3 = true,
            WindowTitle = "Codex Windows Sample"
        };
}

