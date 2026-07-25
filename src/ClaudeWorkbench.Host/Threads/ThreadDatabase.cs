using Microsoft.Data.Sqlite;

namespace ClaudeWorkbench.Host.Threads;

// The dedicated per-workspace thread index: its OWN little SQLite database
// (runtime\<workspace>\threads.sqlite), SEPARATE from the solution index DB
// (AIMonitor.Data) and from the retired kanban board.sqlite. Thread metadata never touches the
// code index. Only the Microsoft.Data.Sqlite plumbing pattern is shared with the engine's DB.
public sealed class ThreadDatabase
{
    public const int SchemaVersion = 1;

    private readonly string databasePath;

    public ThreadDatabase(string databasePath)
    {
        this.databasePath = Path.GetFullPath(databasePath);
    }

    public string DatabasePath => databasePath;

    public SqliteConnection OpenConnection()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(databasePath) ?? ".");
        SqliteConnection connection = new($"Data Source={databasePath}");
        connection.Open();

        using (SqliteCommand pragma = connection.CreateCommand())
        {
            pragma.CommandText = "pragma journal_mode=wal; pragma foreign_keys=on;";
            pragma.ExecuteNonQuery();
        }

        return connection;
    }

    public void EnsureCreated()
    {
        using (SqliteConnection connection = OpenConnection())
        {
            int persistedVersion = ReadUserVersion(connection);
            if (persistedVersion > SchemaVersion)
            {
                throw new InvalidOperationException(
                    "Thread database schema version "
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
        Execute(connection, transaction, """
            create table if not exists threads (
                thread_id text primary key,
                name text not null,
                description text null,
                user_note text null,
                session_id text null,
                cwd text not null,
                status text not null default 'archived',
                kind text not null default 'discussion',
                created_at_utc text not null,
                updated_at_utc text not null
            );
            """);

        // Provenance: the accepted staged-edit records a thread produced. The bit a spec can't
        // give — the conversation AND exactly what landed.
        Execute(connection, transaction, """
            create table if not exists thread_edits (
                thread_id text not null references threads(thread_id) on delete cascade,
                staged_record_id text not null,
                recorded_at_utc text not null,
                primary key (thread_id, staged_record_id)
            );
            """);

        Execute(connection, transaction, "create index if not exists idx_threads_status on threads(status);");
        Execute(connection, transaction, "create index if not exists idx_threads_session on threads(session_id);");
        Execute(connection, transaction, "create index if not exists idx_threads_created on threads(created_at_utc desc);");
        Execute(connection, transaction, "create index if not exists idx_thread_edits_thread on thread_edits(thread_id);");
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
