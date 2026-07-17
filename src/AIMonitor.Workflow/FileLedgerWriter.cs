namespace AIMonitor.Workflow;

public sealed class FileLedgerWriter
{
    public string AppendEntry(
        string historyRoot,
        string relativePath,
        string watchedFilePath,
        string workingFilePath,
        string proposedSnapshotPath,
        string? summary)
    {
        string ledgerDirectory = Path.Combine(historyRoot, "Ledgers");
        Directory.CreateDirectory(ledgerDirectory);
        string ledgerPath = Path.Combine(ledgerDirectory, $"{Sanitize(relativePath.Replace(Path.DirectorySeparatorChar, '_'))}.md");
        List<string> entry =
        [
            $"## {DateTimeOffset.Now:yyyy-MM-dd HH:mm}",
            string.Empty,
            $"File: `{relativePath}`",
            $"Original: `{watchedFilePath}`",
            $"Working: `{workingFilePath}`",
            $"Snapshot: `{proposedSnapshotPath}`",
            "ArchiveZip: `not archived`",
            "ArchiveEntry: `not archived`",
            string.Empty,
            string.IsNullOrWhiteSpace(summary)
                ? "Compare snapshot created. Add a concise summary when useful."
                : summary.Trim(),
            string.Empty
        ];

        File.AppendAllText(ledgerPath, string.Join(Environment.NewLine, entry));
        return ledgerPath;
    }

    private static string Sanitize(string value)
    {
        char[] invalid = Path.GetInvalidFileNameChars();
        string clean = new(value.Select(character => invalid.Contains(character) ? '_' : character).ToArray());
        return string.IsNullOrWhiteSpace(clean) ? "file" : clean;
    }
}
