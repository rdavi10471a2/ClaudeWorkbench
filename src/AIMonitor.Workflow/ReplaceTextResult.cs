namespace AIMonitor.Workflow;

public sealed class ReplaceTextResult
{
    public string WatchedFilePath { get; set; } = string.Empty;

    public string WorkingFilePath { get; set; } = string.Empty;

    public string RelativePath { get; set; } = string.Empty;

    public string OldWorkingHash { get; set; } = string.Empty;

    public string NewWorkingHash { get; set; } = string.Empty;

    public string LineEnding { get; set; } = string.Empty;

    public int ActualMatches { get; set; }

    public int TotalMatchCount { get; set; }

    public int ReplacementCount { get; set; }

    public int? ExpectedMatches { get; set; }

    public bool Changed { get; set; }

    public string Message { get; set; } = string.Empty;

    public int OperationCount { get; set; }

    public string ManifestJson { get; set; } = string.Empty;

    public EditSyntaxValidationResult? SyntaxValidation { get; set; }

    public EditOverlayValidationResult? OverlayValidation { get; set; }
}
