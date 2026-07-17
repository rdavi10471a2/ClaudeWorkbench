namespace AIMonitor.Workflow;

public sealed class TextSpanResult
{
    public string WatchedFilePath { get; set; } = string.Empty;

    public string WorkingFilePath { get; set; } = string.Empty;

    public string Text { get; set; } = string.Empty;

    public int OccurrenceCount { get; set; }

    public int OccurrenceIndex { get; set; }

    public int StartLine { get; set; }

    public int StartColumn { get; set; }

    public int EndLine { get; set; }

    public int EndColumn { get; set; }

    public string TextHash { get; set; } = string.Empty;
}
