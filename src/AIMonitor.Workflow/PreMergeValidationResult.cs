namespace AIMonitor.Workflow;

public sealed class PreMergeValidationResult
{
    public string Status { get; set; } = string.Empty;

    public bool IsError { get; set; }

    public int DiagnosticCount { get; set; }

    public string[] Diagnostics { get; set; } = [];

    public string ValidationWorkspacePath { get; set; } = string.Empty;

    public string Message { get; set; } = string.Empty;
}
