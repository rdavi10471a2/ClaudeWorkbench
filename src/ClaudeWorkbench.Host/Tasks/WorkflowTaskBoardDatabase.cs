using Microsoft.Data.Sqlite;

namespace ClaudeWorkbench.Host.Tasks;

public sealed class WorkflowTaskBoardDatabase
{
    public const int SchemaVersion = 3;

    private readonly string databasePath;

    public WorkflowTaskBoardDatabase(string databasePath)
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
                    "Workflow task board database schema version "
                    + persistedVersion
                    + " is newer than this application supports. Expected "
                    + SchemaVersion
                    + ".");
            }

            using (SqliteTransaction transaction = connection.BeginTransaction())
            {
                CreateSchema(connection, transaction);
                MigrateSchema(connection, transaction);
                SeedLookups(connection, transaction);
                transaction.Commit();
            }

            if (persistedVersion < SchemaVersion)
            {
                SetUserVersion(connection, SchemaVersion);
            }
        }
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

    private static void CreateSchema(SqliteConnection connection, SqliteTransaction transaction)
    {
        Execute(connection, transaction, """
            create table if not exists workflow_task_states (
                code text primary key,
                name text not null,
                sort_order integer not null,
                is_terminal integer not null default 0
            );
            """);

        Execute(connection, transaction, """
            create table if not exists workflow_task_event_types (
                code text primary key,
                name text not null,
                sort_order integer not null
            );
            """);

        Execute(connection, transaction, """
            create table if not exists workflow_task_sequences (
                name text primary key,
                current_value integer not null
            );
            """);

        Execute(connection, transaction, """
            create table if not exists workflow_tasks (
                id text primary key,
                task_number integer not null default 0,
                name text not null,
                description text null,
                short_name text null,
                slug text null,
                state_code text not null references workflow_task_states(code),
                notes_markdown_path text null,
                agent_notes_markdown_path text null,
                is_archived integer not null default 0,
                created_at datetime not null,
                updated_at datetime not null,
                activated_at datetime null,
                completed_at datetime null,
                archived_at datetime null
            );
            """);

        Execute(connection, transaction, """
            create table if not exists workflow_task_files (
                id text primary key,
                task_id text not null references workflow_tasks(id) on delete cascade,
                relative_path text not null,
                intent text null,
                file_role text null,
                unique(task_id, relative_path)
            );
            """);

        Execute(connection, transaction, """
            create table if not exists workflow_task_events (
                id text primary key,
                task_id text not null references workflow_tasks(id) on delete cascade,
                event_type_code text not null references workflow_task_event_types(code),
                message text null,
                payload_json text null,
                created_at datetime not null
            );
            """);

        Execute(connection, transaction, """
            create table if not exists workflow_archived_discussions (
                id text primary key,
                name text not null,
                markdown_path text not null,
                thread_id text null,
                turn_mode text not null,
                trigger text not null,
                created_at datetime not null
            );
            """);

        Execute(connection, transaction, "create unique index if not exists ux_workflow_tasks_single_active on workflow_tasks(state_code) where state_code = 'Active';");
        Execute(connection, transaction, "create index if not exists idx_workflow_tasks_state on workflow_tasks(state_code);");
        Execute(connection, transaction, "create index if not exists idx_workflow_task_files_task on workflow_task_files(task_id);");
        Execute(connection, transaction, "create index if not exists idx_workflow_task_events_task on workflow_task_events(task_id, created_at);");
        Execute(connection, transaction, "create index if not exists idx_workflow_archived_discussions_created on workflow_archived_discussions(created_at desc);");
    }

    private static void MigrateSchema(SqliteConnection connection, SqliteTransaction transaction)
    {
        AddColumnIfMissing(connection, transaction, "workflow_tasks", "short_name", "text null");
        AddColumnIfMissing(connection, transaction, "workflow_tasks", "slug", "text null");
        AddColumnIfMissing(connection, transaction, "workflow_tasks", "task_number", "integer not null default 0");
        AddColumnIfMissing(connection, transaction, "workflow_tasks", "description", "text null");
        AddColumnIfMissing(connection, transaction, "workflow_tasks", "agent_notes_markdown_path", "text null");
        AddColumnIfMissing(connection, transaction, "workflow_tasks", "is_archived", "integer not null default 0");
        AddColumnIfMissing(connection, transaction, "workflow_tasks", "archived_at", "datetime null");
        Execute(connection, transaction, """
            update workflow_tasks
            set description = name
            where description is null or trim(description) = '';
            """);
        SeedTaskSequence(connection, transaction);
        AssignMissingTaskNumbers(connection, transaction);
    }

    private static void SeedLookups(SqliteConnection connection, SqliteTransaction transaction)
    {
        UpsertState(connection, transaction, "Proposed", "New Task", 10, false);
        UpsertState(connection, transaction, "Ready", "Ready", 20, false);
        UpsertState(connection, transaction, "Active", "Active", 30, false);
        UpsertState(connection, transaction, "Review", "Review", 40, false);
        UpsertState(connection, transaction, "Done", "Done", 50, true);
        UpsertState(connection, transaction, "Blocked", "Blocked", 60, true);

        UpsertEventType(connection, transaction, "Created", "Created", 10);
        UpsertEventType(connection, transaction, "StateChanged", "State Changed", 20);
        UpsertEventType(connection, transaction, "NotesUpdated", "Notes Updated", 30);
        UpsertEventType(connection, transaction, "FileAdded", "File Added", 40);
        UpsertEventType(connection, transaction, "Comment", "Comment", 50);
        UpsertEventType(connection, transaction, "DetailsUpdated", "Details Updated", 60);
        UpsertEventType(connection, transaction, "Archived", "Archived", 70);
        UpsertEventType(connection, transaction, "Restored", "Restored", 80);
        UpsertEventType(connection, transaction, "AgentNotesUpdated", "Agent Notes Updated", 90);
    }

    private static void UpsertState(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string code,
        string name,
        int sortOrder,
        bool isTerminal)
    {
        using (SqliteCommand command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                insert into workflow_task_states(code, name, sort_order, is_terminal)
                values ($code, $name, $sortOrder, $isTerminal)
                on conflict(code) do update set
                    name = excluded.name,
                    sort_order = excluded.sort_order,
                    is_terminal = excluded.is_terminal;
                """;
            command.Parameters.AddWithValue("$code", code);
            command.Parameters.AddWithValue("$name", name);
            command.Parameters.AddWithValue("$sortOrder", sortOrder);
            command.Parameters.AddWithValue("$isTerminal", isTerminal ? 1 : 0);
            command.ExecuteNonQuery();
        }
    }

    private static void UpsertEventType(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string code,
        string name,
        int sortOrder)
    {
        using (SqliteCommand command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                insert into workflow_task_event_types(code, name, sort_order)
                values ($code, $name, $sortOrder)
                on conflict(code) do update set
                    name = excluded.name,
                    sort_order = excluded.sort_order;
                """;
            command.Parameters.AddWithValue("$code", code);
            command.Parameters.AddWithValue("$name", name);
            command.Parameters.AddWithValue("$sortOrder", sortOrder);
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

    private static void SeedTaskSequence(SqliteConnection connection, SqliteTransaction transaction)
    {
        Execute(connection, transaction, """
            insert into workflow_task_sequences(name, current_value)
            values ('task', 0)
            on conflict(name) do nothing;
            """);
    }

    private static void AssignMissingTaskNumbers(SqliteConnection connection, SqliteTransaction transaction)
    {
        int nextNumber = ReadSequenceValue(connection, transaction);
        List<string> taskIds = [];
        using (SqliteCommand select = connection.CreateCommand())
        {
            select.Transaction = transaction;
            select.CommandText = "select id from workflow_tasks where task_number <= 0 order by created_at, id;";
            using (SqliteDataReader reader = select.ExecuteReader())
            {
                while (reader.Read())
                {
                    taskIds.Add(reader.GetString(0));
                }
            }
        }

        foreach (string taskId in taskIds)
        {
            nextNumber++;
            using (SqliteCommand update = connection.CreateCommand())
            {
                update.Transaction = transaction;
                update.CommandText = "update workflow_tasks set task_number = $taskNumber where id = $id;";
                update.Parameters.AddWithValue("$taskNumber", nextNumber);
                update.Parameters.AddWithValue("$id", taskId);
                update.ExecuteNonQuery();
            }
        }

        WriteSequenceValue(connection, transaction, nextNumber);
    }

    private static int ReadSequenceValue(SqliteConnection connection, SqliteTransaction transaction)
    {
        using (SqliteCommand command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "select current_value from workflow_task_sequences where name = 'task';";
            return Convert.ToInt32(command.ExecuteScalar() ?? 0);
        }
    }

    private static void WriteSequenceValue(SqliteConnection connection, SqliteTransaction transaction, int value)
    {
        using (SqliteCommand command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                insert into workflow_task_sequences(name, current_value)
                values ('task', $value)
                on conflict(name) do update set current_value = excluded.current_value;
                """;
            command.Parameters.AddWithValue("$value", value);
            command.ExecuteNonQuery();
        }
    }

    private static void AddColumnIfMissing(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string tableName,
        string columnName,
        string columnDefinition)
    {
        if (ColumnExists(connection, transaction, tableName, columnName))
        {
            return;
        }

        Execute(connection, transaction, "alter table " + tableName + " add column " + columnName + " " + columnDefinition + ";");
    }

    private static bool ColumnExists(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string tableName,
        string columnName)
    {
        using (SqliteCommand command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "pragma table_info(" + tableName + ");";
            using (SqliteDataReader reader = command.ExecuteReader())
            {
                while (reader.Read())
                {
                    if (reader.GetString(1).Equals(columnName, StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                }
            }
        }

        return false;
    }
}
