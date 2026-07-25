using System.Globalization;
using Microsoft.Data.Sqlite;

namespace ClaudeWorkbench.Host.Threads;

// Data access for the per-workspace threads.sqlite. Pure persistence: it owns no session/lifecycle
// policy (that lives in the Host service that calls it). "Active" is never stored here — it is
// computed by the caller as "the row whose SessionId is the live one".
public sealed class ThreadRepository
{
    private readonly ThreadDatabase database;

    public ThreadRepository(ThreadDatabase database)
    {
        this.database = database;
        database.EnsureCreated();
    }

    public string DatabasePath => database.DatabasePath;

    // Insert or update a thread's metadata row (does NOT touch provenance edit refs — see AddEditRefs).
    public void Upsert(ThreadRecord thread)
    {
        using SqliteConnection connection = database.OpenConnection();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            insert into threads
                (thread_id, name, description, user_note, session_id, cwd, status, kind, created_at_utc, updated_at_utc)
            values
                ($threadId, $name, $description, $userNote, $sessionId, $cwd, $status, $kind, $createdAt, $updatedAt)
            on conflict(thread_id) do update set
                name = excluded.name,
                description = excluded.description,
                user_note = excluded.user_note,
                session_id = excluded.session_id,
                cwd = excluded.cwd,
                status = excluded.status,
                kind = excluded.kind,
                updated_at_utc = excluded.updated_at_utc;
            """;
        command.Parameters.AddWithValue("$threadId", thread.ThreadId);
        command.Parameters.AddWithValue("$name", thread.Name);
        command.Parameters.AddWithValue("$description", (object?)thread.Description ?? DBNull.Value);
        command.Parameters.AddWithValue("$userNote", (object?)thread.UserNote ?? DBNull.Value);
        command.Parameters.AddWithValue("$sessionId", (object?)thread.SessionId ?? DBNull.Value);
        command.Parameters.AddWithValue("$cwd", thread.Cwd);
        command.Parameters.AddWithValue("$status", thread.Status);
        command.Parameters.AddWithValue("$kind", thread.Kind);
        command.Parameters.AddWithValue("$createdAt", FormatUtc(thread.CreatedAtUtc));
        command.Parameters.AddWithValue("$updatedAt", FormatUtc(thread.UpdatedAtUtc));
        command.ExecuteNonQuery();
    }

    public ThreadRecord? Get(string threadId)
    {
        using SqliteConnection connection = database.OpenConnection();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = SelectColumns + " where thread_id = $threadId;";
        command.Parameters.AddWithValue("$threadId", threadId);
        using SqliteDataReader reader = command.ExecuteReader();
        if (!reader.Read())
        {
            return null;
        }

        ThreadRecord thread = ReadRow(reader);
        reader.Close();
        return thread with { AcceptedEditRefs = LoadEditRefs(connection, threadId) };
    }

    public ThreadRecord? FindBySessionId(string sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return null;
        }

        using SqliteConnection connection = database.OpenConnection();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = SelectColumns + " where session_id = $sessionId order by updated_at_utc desc limit 1;";
        command.Parameters.AddWithValue("$sessionId", sessionId);
        using SqliteDataReader reader = command.ExecuteReader();
        if (!reader.Read())
        {
            return null;
        }

        ThreadRecord thread = ReadRow(reader);
        reader.Close();
        return thread with { AcceptedEditRefs = LoadEditRefs(connection, thread.ThreadId) };
    }

    // All threads, newest-updated first, each with its provenance edit refs attached.
    public IReadOnlyList<ThreadRecord> List()
    {
        using SqliteConnection connection = database.OpenConnection();
        List<ThreadRecord> threads = [];
        using (SqliteCommand command = connection.CreateCommand())
        {
            command.CommandText = SelectColumns + " order by updated_at_utc desc;";
            using SqliteDataReader reader = command.ExecuteReader();
            while (reader.Read())
            {
                threads.Add(ReadRow(reader));
            }
        }

        return threads
            .Select(thread => thread with { AcceptedEditRefs = LoadEditRefs(connection, thread.ThreadId) })
            .ToArray();
    }

    public void SetStatus(string threadId, string status)
    {
        if (!ThreadStatus.IsValid(status))
        {
            throw new ArgumentException("Unknown thread status: " + status, nameof(status));
        }

        UpdateColumn(threadId, "status", status);
    }

    public void SetKind(string threadId, string kind)
    {
        if (!ThreadKind.IsValid(kind))
        {
            throw new ArgumentException("Unknown thread kind: " + kind, nameof(kind));
        }

        UpdateColumn(threadId, "kind", kind);
    }

    public void Rename(string threadId, string name) => UpdateColumn(threadId, "name", name);

    public void SetDescription(string threadId, string? description) =>
        UpdateColumn(threadId, "description", (object?)description ?? DBNull.Value);

    public void SetUserNote(string threadId, string? userNote) =>
        UpdateColumn(threadId, "user_note", (object?)userNote ?? DBNull.Value);

    public void AssignSession(string threadId, string sessionId) =>
        UpdateColumn(threadId, "session_id", sessionId);

    // Link a thread to the accepted staged-edit records it produced. Idempotent per (thread, record).
    public void AddEditRefs(string threadId, IEnumerable<string> stagedRecordIds)
    {
        string[] ids = stagedRecordIds
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (ids.Length == 0)
        {
            return;
        }

        using SqliteConnection connection = database.OpenConnection();
        using SqliteTransaction transaction = connection.BeginTransaction();
        string recordedAt = FormatUtc(DateTime.UtcNow);
        foreach (string id in ids)
        {
            using SqliteCommand command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                insert into thread_edits (thread_id, staged_record_id, recorded_at_utc)
                values ($threadId, $recordId, $recordedAt)
                on conflict(thread_id, staged_record_id) do nothing;
                """;
            command.Parameters.AddWithValue("$threadId", threadId);
            command.Parameters.AddWithValue("$recordId", id);
            command.Parameters.AddWithValue("$recordedAt", recordedAt);
            command.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    // Hard delete: remove the thread row (cascade removes thread_edits). Returns true if a row went.
    // The caller is responsible for deleting the ~/.claude transcript JSONL to reclaim disk — this
    // layer owns only the index.
    public bool Delete(string threadId)
    {
        using SqliteConnection connection = database.OpenConnection();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "delete from threads where thread_id = $threadId;";
        command.Parameters.AddWithValue("$threadId", threadId);
        return command.ExecuteNonQuery() > 0;
    }

    // The default display name: discussion-YYYY-MM-DD-N, where N is that day's Nth thread for this
    // workspace (count of threads created today + 1). Deterministic; no agent-generated names.
    public string NextDefaultName(DateTime nowUtc)
    {
        string day = nowUtc.ToUniversalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        using SqliteConnection connection = database.OpenConnection();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "select count(*) from threads where substr(created_at_utc, 1, 10) = $day;";
        command.Parameters.AddWithValue("$day", day);
        long countToday = Convert.ToInt64(command.ExecuteScalar() ?? 0L);
        return $"discussion-{day}-{countToday + 1}";
    }

    private void UpdateColumn(string threadId, string column, object value)
    {
        using SqliteConnection connection = database.OpenConnection();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = $"update threads set {column} = $value, updated_at_utc = $updatedAt where thread_id = $threadId;";
        command.Parameters.AddWithValue("$value", value);
        command.Parameters.AddWithValue("$updatedAt", FormatUtc(DateTime.UtcNow));
        command.Parameters.AddWithValue("$threadId", threadId);
        command.ExecuteNonQuery();
    }

    private static IReadOnlyList<string> LoadEditRefs(SqliteConnection connection, string threadId)
    {
        List<string> refs = [];
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "select staged_record_id from thread_edits where thread_id = $threadId order by recorded_at_utc;";
        command.Parameters.AddWithValue("$threadId", threadId);
        using SqliteDataReader reader = command.ExecuteReader();
        while (reader.Read())
        {
            refs.Add(reader.GetString(0));
        }

        return refs;
    }

    private const string SelectColumns =
        "select thread_id, name, description, user_note, session_id, cwd, status, kind, created_at_utc, updated_at_utc from threads";

    private static ThreadRecord ReadRow(SqliteDataReader reader)
    {
        return new ThreadRecord(
            reader.GetString(0),
            reader.GetString(1),
            reader.IsDBNull(2) ? null : reader.GetString(2),
            reader.IsDBNull(3) ? null : reader.GetString(3),
            reader.IsDBNull(4) ? null : reader.GetString(4),
            reader.GetString(5),
            reader.GetString(6),
            reader.GetString(7),
            ParseUtc(reader.GetString(8)),
            ParseUtc(reader.GetString(9)),
            []);
    }

    // Timestamps are stored as invariant round-trip ("o") UTC strings so date math (the per-day
    // default-name count) and parse-back are both driver-independent.
    private static string FormatUtc(DateTime value) =>
        value.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture);

    private static DateTime ParseUtc(string value) =>
        DateTime.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind).ToUniversalTime();
}
