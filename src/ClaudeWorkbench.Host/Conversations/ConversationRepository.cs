using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace ClaudeWorkbench.Host.Conversations;

// Data access for the per-workspace conversations.sqlite. Pure persistence: it owns no session/lifecycle
// policy (that lives in the Host service that calls it). "Active" is never stored here — it is
// computed by the caller as "the row whose SessionId is the live one".
public sealed class ConversationRepository
{
    private readonly ConversationDatabase database;

    public ConversationRepository(ConversationDatabase database)
    {
        this.database = database;
        database.EnsureCreated();
    }

    public string DatabasePath => database.DatabasePath;

    // Insert or update a thread's metadata row (does NOT touch provenance edit refs — see AddEditRefs).
    public void Upsert(ConversationRecord thread)
    {
        using SqliteConnection connection = database.OpenConnection();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            insert into conversations
                (conversation_id, name, description, user_note, session_id, cwd, status, created_at_utc, updated_at_utc, transcript_file)
            values
                ($conversationId, $name, $description, $userNote, $sessionId, $cwd, $status, $createdAt, $updatedAt, $transcriptFile)
            on conflict(conversation_id) do update set
                name = excluded.name,
                description = excluded.description,
                user_note = excluded.user_note,
                session_id = excluded.session_id,
                cwd = excluded.cwd,
                status = excluded.status,
                updated_at_utc = excluded.updated_at_utc,
                transcript_file = excluded.transcript_file;
            """;
        command.Parameters.AddWithValue("$conversationId", thread.ConversationId);
        command.Parameters.AddWithValue("$name", thread.Name);
        command.Parameters.AddWithValue("$description", (object?)thread.Description ?? DBNull.Value);
        command.Parameters.AddWithValue("$userNote", (object?)thread.UserNote ?? DBNull.Value);
        command.Parameters.AddWithValue("$sessionId", (object?)thread.SessionId ?? DBNull.Value);
        command.Parameters.AddWithValue("$cwd", thread.Cwd);
        command.Parameters.AddWithValue("$status", thread.Status);
        command.Parameters.AddWithValue("$createdAt", FormatUtc(thread.CreatedAtUtc));
        command.Parameters.AddWithValue("$updatedAt", FormatUtc(thread.UpdatedAtUtc));
        command.Parameters.AddWithValue("$transcriptFile", (object?)thread.TranscriptFile ?? DBNull.Value);
        command.ExecuteNonQuery();
    }

    public ConversationRecord? Get(string conversationId)
    {
        using SqliteConnection connection = database.OpenConnection();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = SelectColumns + " where conversation_id = $conversationId;";
        command.Parameters.AddWithValue("$conversationId", conversationId);
        using SqliteDataReader reader = command.ExecuteReader();
        if (!reader.Read())
        {
            return null;
        }

        ConversationRecord thread = ReadRow(reader);
        reader.Close();
        return thread with { AcceptedEditRefs = LoadEditRefs(connection, conversationId) };
    }

    public ConversationRecord? FindBySessionId(string sessionId)
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

        ConversationRecord thread = ReadRow(reader);
        reader.Close();
        return thread with { AcceptedEditRefs = LoadEditRefs(connection, thread.ConversationId) };
    }

    // All threads, newest-updated first, each with its provenance edit refs attached.
    public IReadOnlyList<ConversationRecord> List()
    {
        using SqliteConnection connection = database.OpenConnection();
        List<ConversationRecord> threads = [];
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
            .Select(thread => thread with { AcceptedEditRefs = LoadEditRefs(connection, thread.ConversationId) })
            .ToArray();
    }

    // The most-recent unadopted (no session yet) thread still on a default name — the throwaway a
    // fresh start can reuse instead of creating another empty "conversation-*". Null if none. A
    // user-named stub is skipped (only default-named ones are reusable).
    public ConversationRecord? LatestUnadoptedDefault()
    {
        using SqliteConnection connection = database.OpenConnection();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = SelectColumns +
            " where session_id is null and name like 'conversation-%' order by updated_at_utc desc;";
        using SqliteDataReader reader = command.ExecuteReader();
        while (reader.Read())
        {
            ConversationRecord candidate = ReadRow(reader);
            if (IsDefaultName(candidate.Name))
            {
                reader.Close();
                return candidate with { AcceptedEditRefs = LoadEditRefs(connection, candidate.ConversationId) };
            }
        }

        return null;
    }

    public void SetStatus(string conversationId, string status)
    {
        if (!ConversationStatus.IsValid(status))
        {
            throw new ArgumentException("Unknown thread status: " + status, nameof(status));
        }

        UpdateColumn(conversationId, "status", status);
    }

    public void Rename(string conversationId, string name) => UpdateColumn(conversationId, "name", name);

    public void SetDescription(string conversationId, string? description) =>
        UpdateColumn(conversationId, "description", (object?)description ?? DBNull.Value);

    public void SetUserNote(string conversationId, string? userNote) =>
        UpdateColumn(conversationId, "user_note", (object?)userNote ?? DBNull.Value);

    public void AssignSession(string conversationId, string sessionId) =>
        UpdateColumn(conversationId, "session_id", sessionId);

    public void SetTranscriptFile(string conversationId, string? transcriptFile) =>
        UpdateColumn(conversationId, "transcript_file", (object?)transcriptFile ?? DBNull.Value);

    // Link a thread to the accepted staged-edit records it produced. Idempotent per (thread, record).
    public void AddEditRefs(string conversationId, IEnumerable<string> stagedRecordIds)
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
                insert into conversation_edits (conversation_id, staged_record_id, recorded_at_utc)
                values ($conversationId, $recordId, $recordedAt)
                on conflict(conversation_id, staged_record_id) do nothing;
                """;
            command.Parameters.AddWithValue("$conversationId", conversationId);
            command.Parameters.AddWithValue("$recordId", id);
            command.Parameters.AddWithValue("$recordedAt", recordedAt);
            command.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    // Hard delete: remove the thread row (cascade removes thread_edits). Returns true if a row went.
    // The caller is responsible for deleting the ~/.claude transcript JSONL to reclaim disk — this
    // layer owns only the index.
    public bool Delete(string conversationId)
    {
        using SqliteConnection connection = database.OpenConnection();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "delete from conversations where conversation_id = $conversationId;";
        command.Parameters.AddWithValue("$conversationId", conversationId);
        return command.ExecuteNonQuery() > 0;
    }

    // The default display name: conversation-YYYY-MM-DD-N. N is one more than the HIGHEST suffix already
    // used by a conversation-YYYY-MM-DD-* thread today — NOT a running count. Using max+1 (not count+1)
    // keeps the name unique even after threads are deleted: a plain count would reuse a freed number and
    // collide with a surviving thread of the same name. Deterministic; no agent-generated names.
    public string NextDefaultName(DateTime nowUtc)
    {
        string day = nowUtc.ToUniversalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        string prefix = $"conversation-{day}-";
        using SqliteConnection connection = database.OpenConnection();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "select name from conversations where name like $prefix || '%';";
        command.Parameters.AddWithValue("$prefix", prefix);
        long maxSuffix = 0;
        using (SqliteDataReader reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                string suffix = reader.GetString(0)[prefix.Length..];
                if (long.TryParse(suffix, NumberStyles.None, CultureInfo.InvariantCulture, out long value) && value > maxSuffix)
                {
                    maxSuffix = value;
                }
            }
        }

        return $"{prefix}{maxSuffix + 1}";
    }

    // A thread is "still unnamed" when its name is a machine default from NextDefaultName
    // (conversation-YYYY-MM-DD-N) — i.e. the operator never gave it a name. New Thread uses this to
    // decide whether to offer renaming the conversation being left behind.
    private static readonly Regex DefaultNamePattern =
        new(@"^conversation-\d{4}-\d{2}-\d{2}-\d+$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static bool IsDefaultName(string? name) =>
        !string.IsNullOrWhiteSpace(name) && DefaultNamePattern.IsMatch(name);

    private void UpdateColumn(string conversationId, string column, object value)
    {
        using SqliteConnection connection = database.OpenConnection();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = $"update conversations set {column} = $value, updated_at_utc = $updatedAt where conversation_id = $conversationId;";
        command.Parameters.AddWithValue("$value", value);
        command.Parameters.AddWithValue("$updatedAt", FormatUtc(DateTime.UtcNow));
        command.Parameters.AddWithValue("$conversationId", conversationId);
        command.ExecuteNonQuery();
    }

    private static IReadOnlyList<string> LoadEditRefs(SqliteConnection connection, string conversationId)
    {
        List<string> refs = [];
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "select staged_record_id from conversation_edits where conversation_id = $conversationId order by recorded_at_utc;";
        command.Parameters.AddWithValue("$conversationId", conversationId);
        using SqliteDataReader reader = command.ExecuteReader();
        while (reader.Read())
        {
            refs.Add(reader.GetString(0));
        }

        return refs;
    }

    private const string SelectColumns =
        "select conversation_id, name, description, user_note, session_id, cwd, status, created_at_utc, updated_at_utc, transcript_file from conversations";

    private static ConversationRecord ReadRow(SqliteDataReader reader)
    {
        return new ConversationRecord(
            reader.GetString(0),
            reader.GetString(1),
            reader.IsDBNull(2) ? null : reader.GetString(2),
            reader.IsDBNull(3) ? null : reader.GetString(3),
            reader.IsDBNull(4) ? null : reader.GetString(4),
            reader.GetString(5),
            reader.GetString(6),
            ParseUtc(reader.GetString(7)),
            ParseUtc(reader.GetString(8)),
            [],
            reader.IsDBNull(9) ? null : reader.GetString(9));
    }

    // Timestamps are stored as invariant round-trip ("o") UTC strings so date math (the per-day
    // default-name count) and parse-back are both driver-independent.
    private static string FormatUtc(DateTime value) =>
        value.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture);

    private static DateTime ParseUtc(string value) =>
        DateTime.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind).ToUniversalTime();
}
