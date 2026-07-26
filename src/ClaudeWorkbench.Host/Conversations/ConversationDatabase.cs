using Microsoft.Data.Sqlite;

namespace ClaudeWorkbench.Host.Conversations;

// The dedicated per-workspace conversation index: its OWN little SQLite database
// (runtime\<workspace>\data\conversations.sqlite), SEPARATE from the solution index DB
// (AIMonitor.Data) and from the retired kanban board.sqlite. Conversation metadata never touches the
// code index. Only the Microsoft.Data.Sqlite plumbing pattern is shared with the engine's DB.
public sealed class ConversationDatabase
{
    public const int SchemaVersion = 3;

    private readonly string databasePath;

    public ConversationDatabase(string databasePath)
    {
        this.databasePath = Path.GetFullPath(databasePath);
    }

    public string DatabasePath => databasePath;

    public SqliteConnection OpenConnection()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(databasePath) ?? ".");
        MigrateLegacyDatabaseFile();
        SqliteConnection connection = new($"Data Source={databasePath}");
        connection.Open();

        using (SqliteCommand pragma = connection.CreateCommand())
        {
            pragma.CommandText = "pragma journal_mode=wal; pragma foreign_keys=on;";
            pragma.ExecuteNonQuery();
        }

        return connection;
    }

    // Bring a pre-rename database file forward: the store used to be threads.sqlite. If ours
    // (conversations.sqlite) doesn't exist yet but the old file is sitting beside it, move it (and its
    // WAL/SHM sidecars) so existing conversations are preserved. The table/column rename then happens in
    // EnsureCreated. Best-effort and one-time — once moved, the old file is gone.
    private void MigrateLegacyDatabaseFile()
    {
        if (File.Exists(databasePath))
        {
            return;
        }

        string? dir = Path.GetDirectoryName(databasePath);
        if (dir is null)
        {
            return;
        }

        string legacy = Path.Combine(dir, "threads.sqlite");
        if (!File.Exists(legacy))
        {
            return;
        }

        foreach (string suffix in new[] { string.Empty, "-wal", "-shm" })
        {
            try
            {
                string from = legacy + suffix;
                string to = databasePath + suffix;
                if (File.Exists(from) && !File.Exists(to))
                {
                    File.Move(from, to);
                }
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    public void EnsureCreated()
    {
        using (SqliteConnection connection = OpenConnection())
        {
            int persistedVersion = ReadUserVersion(connection);
            if (persistedVersion > SchemaVersion)
            {
                throw new InvalidOperationException(
                    "Conversation database schema version "
                    + persistedVersion
                    + " is newer than this application supports. Expected "
                    + SchemaVersion
                    + ".");
            }

            using (SqliteTransaction transaction = connection.BeginTransaction())
            {
                CreateSchema(connection, transaction);
                transaction.Commit();
            }

            if (persistedVersion < SchemaVersion)
            {
                SetUserVersion(connection, SchemaVersion);
            }
        }
    }

    private static void CreateSchema(SqliteConnection connection, SqliteTransaction transaction)
    {
        // Current schema (fresh installs).
        Execute(connection, transaction, """
            create table if not exists conversations (
                conversation_id text primary key,
                name text not null,
                description text null,
                user_note text null,
                session_id text null,
                cwd text not null,
                status text not null default 'archived',
                created_at_utc text not null,
                updated_at_utc text not null,
                transcript_file text null
            );
            """);

        // Provenance: the accepted staged-edit records a conversation produced. The bit a spec can't
        // give — the conversation AND exactly what landed.
        Execute(connection, transaction, """
            create table if not exists conversation_edits (
                conversation_id text not null references conversations(conversation_id) on delete cascade,
                staged_record_id text not null,
                recorded_at_utc text not null,
                primary key (conversation_id, staged_record_id)
            );
            """);

        // Legacy migration: earlier schemas stored these as `threads` / `thread_edits` with a
        // `thread_id` key. If those tables are present, copy their rows into the new tables and drop
        // them. Copy-based (not ALTER … RENAME) to avoid FK-rename subtleties; data volumes are tiny.
        if (TableExists(connection, transaction, "threads"))
        {
            if (!ColumnExists(connection, transaction, "threads", "transcript_file"))
            {
                Execute(connection, transaction, "alter table threads add column transcript_file text null;");
            }

            Execute(connection, transaction, """
                insert or ignore into conversations
                    (conversation_id, name, description, user_note, session_id, cwd, status, created_at_utc, updated_at_utc, transcript_file)
                select thread_id, name, description, user_note, session_id, cwd, status, created_at_utc, updated_at_utc, transcript_file
                from threads;
                """);

            if (TableExists(connection, transaction, "thread_edits"))
            {
                Execute(connection, transaction, """
                    insert or ignore into conversation_edits (conversation_id, staged_record_id, recorded_at_utc)
                    select thread_id, staged_record_id, recorded_at_utc from thread_edits;
                    """);
                Execute(connection, transaction, "drop table thread_edits;");
            }

            Execute(connection, transaction, "drop table threads;");
        }

        Execute(connection, transaction, "create index if not exists idx_conversations_status on conversations(status);");
        Execute(connection, transaction, "create index if not exists idx_conversations_session on conversations(session_id);");
        Execute(connection, transaction, "create index if not exists idx_conversations_created on conversations(created_at_utc desc);");
        Execute(connection, transaction, "create index if not exists idx_conversation_edits_conversation on conversation_edits(conversation_id);");
    }

    private static bool TableExists(SqliteConnection connection, SqliteTransaction transaction, string table)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "select 1 from sqlite_master where type = 'table' and name = $name limit 1;";
        command.Parameters.AddWithValue("$name", table);
        using SqliteDataReader reader = command.ExecuteReader();
        return reader.Read();
    }

    private static bool ColumnExists(SqliteConnection connection, SqliteTransaction transaction, string table, string column)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"pragma table_info({table});";
        using SqliteDataReader reader = command.ExecuteReader();
        while (reader.Read())
        {
            // table_info columns: cid, name, type, notnull, dflt_value, pk — name is index 1.
            if (string.Equals(reader.GetString(1), column, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static int ReadUserVersion(SqliteConnection connection)
    {
        using (SqliteCommand command = connection.CreateCommand())
        {
            command.CommandText = "pragma user_version;";
            return Convert.ToInt32(command.ExecuteScalar() ?? 0);
        }
    }

    private static void SetUserVersion(SqliteConnection connection, int version)
    {
        using (SqliteCommand command = connection.CreateCommand())
        {
            command.CommandText = "pragma user_version = " + version + ";";
            command.ExecuteNonQuery();
        }
    }

    private static void Execute(SqliteConnection connection, SqliteTransaction transaction, string commandText)
    {
        using (SqliteCommand command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = commandText;
            command.ExecuteNonQuery();
        }
    }
}
