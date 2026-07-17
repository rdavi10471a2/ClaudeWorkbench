namespace AIMonitor.Runtime;

public sealed class DiffLaunchResult
{
    public bool Launched { get; set; }

    public string Tool { get; set; } = string.Empty;

    public string ToolPath { get; set; } = string.Empty;

    public int ProcessId { get; set; }

    public string Message { get; set; } = string.Empty;
}
