using System.Text.Json;

namespace AIMonitor.Workflow;

public sealed class WorkflowRunRecorder
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private const int RunLogEntryRetentionLimit = 500;
    private readonly string runLogPath;
    private readonly string telemetryPath;
    private int sequence;

    public WorkflowRunRecorder(string historyRoot, string runId)
    {
        Directory.CreateDirectory(historyRoot);
        runLogPath = Path.Combine(historyRoot, "_runs.json");
        string telemetryRoot = Path.Combine(historyRoot, "Telemetry");
        Directory.CreateDirectory(telemetryRoot);
        telemetryPath = Path.Combine(telemetryRoot, $"telemetry-{Sanitize(runId)}.log");
    }

    public string RunLogPath => runLogPath;

    public string TelemetryPath => telemetryPath;

    public void Stage(string runId, string stage, IReadOnlyDictionary<string, string>? fields = null)
    {
        sequence++;
        Dictionary<string, object?> payload = new()
        {
            ["timestampLocal"] = DateTimeOffset.Now.ToString("O"),
            ["seq"] = sequence,
            ["entryType"] = "stage",
            ["stage"] = stage,
            ["runId"] = runId
        };

        if (fields is not null)
        {
            foreach ((string key, string value) in fields)
            {
                payload[key] = value;
            }
        }

        List<Dictionary<string, object?>> entries = LoadRunEntries();
        entries.Add(payload);
        TrimListToLast(entries, RunLogEntryRetentionLimit);
        WriteAllTextAtomically(runLogPath, JsonSerializer.Serialize(entries, JsonOptions));

        List<string> telemetryLines =
        [
            $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff} [{runId}] {stage}"
        ];
        if (fields is not null)
        {
            telemetryLines.AddRange(fields.Select(pair => $"  {pair.Key}: {pair.Value}"));
        }

        File.AppendAllLines(telemetryPath, telemetryLines);
    }

    private List<Dictionary<string, object?>> LoadRunEntries()
    {
        if (!File.Exists(runLogPath))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<List<Dictionary<string, object?>>>(File.ReadAllText(runLogPath), JsonOptions) ?? [];
        }
        catch
        {
            File.Move(runLogPath, $"{runLogPath}.corrupt_{DateTimeOffset.Now:yyyyMMdd_HHmmss}", overwrite: true);
            return [];
        }
    }

    private static void TrimListToLast<T>(List<T> items, int keepCount)
    {
        if (items.Count > keepCount)
        {
            items.RemoveRange(0, items.Count - keepCount);
        }
    }

    private static void WriteAllTextAtomically(string path, string contents)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? ".");
        string tempPath = $"{path}.tmp_{Environment.ProcessId}_{Guid.NewGuid():N}";
        File.WriteAllText(tempPath, contents);
        File.Move(tempPath, path, overwrite: true);
    }

    private static string Sanitize(string value)
    {
        char[] invalid = Path.GetInvalidFileNameChars();
        return new string(value.Select(character => invalid.Contains(character) ? '_' : character).ToArray());
    }
}
