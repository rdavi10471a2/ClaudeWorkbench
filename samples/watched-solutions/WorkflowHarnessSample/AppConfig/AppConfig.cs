namespace WorkflowHarnessSample.Configuration;

public sealed class AppConfig
{
    public bool InitiaWorkflowTestPassed3 { get; set; } = true;

    public static AppConfig CreateDefault() =>
        new()
        {
            InitiaWorkflowTestPassed3 = true
        };
}
