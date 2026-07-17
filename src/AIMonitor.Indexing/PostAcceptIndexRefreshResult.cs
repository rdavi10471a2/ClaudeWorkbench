namespace AIMonitor.Indexing;

public sealed class PostAcceptIndexRefreshResult
{
    public string Status { get; set; } = string.Empty;

    public string RefreshMode { get; set; } = string.Empty;

    public bool IsError { get; set; }

    public string DatabasePath { get; set; } = string.Empty;

    public int ProjectCount { get; set; }

    public int DocumentCount { get; set; }

    public int DiagnosticCount { get; set; }

    public long DurationMs { get; set; }

    public string Message { get; set; } = string.Empty;
}
