using Microsoft.Data.Sqlite;

namespace AIMonitor.Data;

public sealed class SolutionIndexDatabase
{
    // The solution index is a DERIVED SNAPSHOT, not transactional data. On any schema change we do NOT migrate
    // contents: we rebuild the whole schema fresh and force a full refresh. SchemaVersion is persisted via SQLite
    // PRAGMA user_version; bump it whenever the index schema changes in a way that invalidates existing rows.
    //
    // History:
    //   v1 (and any earlier/unversioned db): symbol_references/call_sites/symbol_relationships *_stable_key columns
    //       carried a `references symbols(stable_key) on delete cascade` cross-symbol FK. That FK made a full
    //       RebuildAsync fail with SQLite error 19 on real multi-project solutions (a project's references are
    //       inserted before later projects' target symbols exist) and made a scoped refresh cascade-delete other
    //       projects' inbound rows.
    //   v2: the cross-symbol FK is gone; those columns are plain `text not null`. project_id FKs are retained.
    public const int SchemaVersion = 2;

    // index_meta key set on a schema-versioned full recreate (a stale/old/fresh db whose persisted user_version did
    // not match SchemaVersion). While set, the index is INVALID: the post-accept refresh must take the full
    // RebuildAsync path (never a scoped refresh) so the freshly recreated empty tables are fully repopulated, and the
    // status surface reports the index as stale / rebuild-required. A successful full rebuild clears it.
    public const string NeedsFullRebuildKey = "needs_full_rebuild";

    private readonly string databasePath;

    public SolutionIndexDatabase(string databasePath)
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

            // Schema-versioned full recreate: if the persisted schema version does not match the current SchemaVersion
            // (this includes a brand-new db reporting user_version 0, and any older versioned db), the existing index is
            // INVALID. Drop ALL index tables and recreate the full schema from scratch (empty) — NO per-table content
            // migration — then stamp the new user_version and set the persistent needs_full_rebuild marker so a full
            // rebuild repopulates the empty tables before any scoped refresh trusts them.
            if (persistedVersion != SchemaVersion)
            {
                using (SqliteTransaction recreateTransaction = connection.BeginTransaction())
                {
                    DropAllIndexTables(connection, recreateTransaction);
                    CreateSchema(connection, recreateTransaction);
                    SetMeta(connection, recreateTransaction, NeedsFullRebuildKey, "1");
                    recreateTransaction.Commit();
                }

                SetUserVersion(connection, SchemaVersion);
                return;
            }

            // Version matches: ensure the schema objects exist (first-time create on a db that already carries the
            // current user_version, or a no-op when everything is present). create table if not exists is idempotent.
            using (SqliteTransaction transaction = connection.BeginTransaction())
            {
                CreateSchema(connection, transaction);
                transaction.Commit();
            }
        }
    }

    // True when a schema-versioned full recreate emptied the index tables and a full rebuild has not yet run to
    // repopulate them. The post-accept refresh and the status surface consult this to force the full RebuildAsync path
    // and report the index as stale / rebuild-required.
    public bool IsFullRebuildRequired()
    {
        EnsureCreated();
        using (SqliteConnection connection = OpenConnection())
        {
            using (SqliteCommand command = connection.CreateCommand())
            {
                command.CommandText = "select value from index_meta where key = $key;";
                command.Parameters.AddWithValue("$key", NeedsFullRebuildKey);
                object? value = command.ExecuteScalar();
                return value is string text && text == "1";
            }
        }
    }

    // Cleared by a successful full rebuild (SolutionIndexStore.SaveSnapshot) once the recreated tables are repopulated.
    public void ClearFullRebuildRequired()
    {
        using (SqliteConnection connection = OpenConnection())
        {
            using (SqliteCommand command = connection.CreateCommand())
            {
                command.CommandText = "delete from index_meta where key = $key;";
                command.Parameters.AddWithValue("$key", NeedsFullRebuildKey);
                command.ExecuteNonQuery();
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
            // user_version takes a literal, not a bound parameter; version is an internal int constant, not user input.
            command.CommandText = "pragma user_version = " + version + ";";
            command.ExecuteNonQuery();
        }
    }

    // Drops every index table this schema owns. Ordering is irrelevant: only project_id -> projects FKs remain, and a
    // plain DROP TABLE does not run row-level ON DELETE CASCADE, so the drop set can be processed in any order.
    private static void DropAllIndexTables(SqliteConnection connection, SqliteTransaction transaction)
    {
        string[] tables =
        [
            "diagnostics",
            "global_usings",
            "framework_references",
            "package_references",
            "project_references",
            "symbol_relationships",
            "call_sites",
            "symbol_references",
            "symbols",
            "documents",
            "projects",
            "solution_state",
            "index_meta"
        ];
        foreach (string table in tables)
        {
            Execute(connection, transaction, "drop table if exists " + table + ";");
        }
    }

    private static void CreateSchema(SqliteConnection connection, SqliteTransaction transaction)
    {
        Execute(connection, transaction, """
            create table if not exists solution_state (
                id integer primary key check (id = 1),
                input_path text not null,
                indexed_at_utc text not null,
                project_count integer not null,
                document_count integer not null,
                diagnostic_count integer not null
            );
            """);

        // Persistent key/value markers that must survive ClearCurrentState/SaveSnapshot row churn (e.g. the
        // needs_full_rebuild flag set by a schema-versioned recreate). Kept out of solution_state because that row is
        // deleted and reinserted on every snapshot save.
        Execute(connection, transaction, """
            create table if not exists index_meta (
                key text primary key,
                value text not null
            );
            """);

        Execute(connection, transaction, """
            create table if not exists projects (
                id integer primary key autoincrement,
                stable_key text not null unique,
                name text not null,
                project_path text not null unique,
                language text not null,
                target_framework text not null,
                target_frameworks text not null,
                output_type text not null,
                sdk text not null,
                assembly_name text not null,
                root_namespace text not null,
                nullable text not null,
                implicit_usings text not null,
                lang_version text not null,
                preprocessor_symbols text not null
            );
            """);

        Execute(connection, transaction, """
            create table if not exists documents (
                id integer primary key autoincrement,
                project_id integer not null references projects(id) on delete cascade,
                stable_key text not null unique,
                name text not null,
                file_path text not null,
                folders text not null,
                content_hash text not null default '',
                unique(project_id, file_path)
            );
            """);

        Execute(connection, transaction, """
            create table if not exists symbols (
                id integer primary key autoincrement,
                project_id integer not null references projects(id) on delete cascade,
                stable_key text not null unique,
                name text not null,
                kind text not null,
                namespace text not null,
                containing_type text not null,
                file_path text not null,
                start_line integer not null,
                end_line integer not null,
                signature text not null,
                accessibility text not null default '',
                is_static integer not null default 0,
                is_abstract integer not null default 0,
                is_sealed integer not null default 0,
                is_virtual integer not null default 0,
                is_override integer not null default 0,
                method_kind text not null default ''
            );
            """);

        // The *_stable_key columns are plain `text not null` (no cross-symbol FK). project_id keeps its
        // references projects(id) on delete cascade so a project delete still clears that project's own rows.
        Execute(connection, transaction, """
            create table if not exists symbol_references (
                id integer primary key autoincrement,
                project_id integer not null references projects(id) on delete cascade,
                target_stable_key text not null,
                file_path text not null,
                line integer not null,
                column integer not null,
                reference_kind text not null,
                snippet text not null
            );
            """);

        Execute(connection, transaction, """
            create table if not exists call_sites (
                id integer primary key autoincrement,
                project_id integer not null references projects(id) on delete cascade,
                caller_stable_key text not null,
                caller_name text not null,
                caller_kind text not null,
                target_stable_key text not null,
                file_path text not null,
                line integer not null,
                column integer not null,
                call_kind text not null,
                snippet text not null
            );
            """);

        Execute(connection, transaction, """
            create table if not exists symbol_relationships (
                id integer primary key autoincrement,
                project_id integer not null references projects(id) on delete cascade,
                source_stable_key text not null,
                source_name text not null,
                source_kind text not null,
                target_stable_key text not null,
                target_name text not null,
                target_kind text not null,
                relationship_kind text not null,
                file_path text not null,
                line integer not null,
                column integer not null,
                snippet text not null
            );
            """);

        Execute(connection, transaction, """
            create table if not exists project_references (
                id integer primary key autoincrement,
                project_id integer not null references projects(id) on delete cascade,
                include text not null,
                full_path text not null
            );
            """);

        Execute(connection, transaction, """
            create table if not exists package_references (
                id integer primary key autoincrement,
                project_id integer not null references projects(id) on delete cascade,
                include text not null,
                version text not null
            );
            """);

        Execute(connection, transaction, """
            create table if not exists framework_references (
                id integer primary key autoincrement,
                project_id integer not null references projects(id) on delete cascade,
                include text not null
            );
            """);

        Execute(connection, transaction, """
            create table if not exists global_usings (
                id integer primary key autoincrement,
                project_id integer not null references projects(id) on delete cascade,
                include text not null,
                is_static text not null,
                alias text not null
            );
            """);

        Execute(connection, transaction, """
            create table if not exists diagnostics (
                id integer primary key autoincrement,
                message text not null
            );
            """);

        Execute(connection, transaction, "create index if not exists idx_projects_path on projects(project_path);");
        Execute(connection, transaction, "create index if not exists idx_projects_stable_key on projects(stable_key);");
        Execute(connection, transaction, "create index if not exists idx_documents_project_id on documents(project_id);");
        Execute(connection, transaction, "create index if not exists idx_documents_file on documents(file_path);");
        Execute(connection, transaction, "create index if not exists idx_documents_stable_key on documents(stable_key);");
        Execute(connection, transaction, "create index if not exists idx_symbols_project_id on symbols(project_id);");
        Execute(connection, transaction, "create index if not exists idx_symbols_name on symbols(name);");
        Execute(connection, transaction, "create index if not exists idx_symbols_file on symbols(file_path);");
        Execute(connection, transaction, "create index if not exists idx_symbol_references_project_id on symbol_references(project_id);");
        Execute(connection, transaction, "create index if not exists idx_symbol_references_target on symbol_references(target_stable_key);");
        Execute(connection, transaction, "create index if not exists idx_symbol_references_file on symbol_references(file_path);");
        Execute(connection, transaction, "create index if not exists idx_call_sites_project_id on call_sites(project_id);");
        Execute(connection, transaction, "create index if not exists idx_call_sites_target on call_sites(target_stable_key);");
        Execute(connection, transaction, "create index if not exists idx_call_sites_caller on call_sites(caller_stable_key);");
        Execute(connection, transaction, "create index if not exists idx_symbol_relationships_project_id on symbol_relationships(project_id);");
        Execute(connection, transaction, "create index if not exists idx_symbol_relationships_target on symbol_relationships(target_stable_key);");
        Execute(connection, transaction, "create index if not exists idx_symbol_relationships_source on symbol_relationships(source_stable_key);");
        Execute(connection, transaction, "create index if not exists idx_project_references_project_id on project_references(project_id);");
        Execute(connection, transaction, "create index if not exists idx_project_references_full_path on project_references(full_path);");
        Execute(connection, transaction, "create index if not exists idx_package_references_project_id on package_references(project_id);");
        Execute(connection, transaction, "create index if not exists idx_package_references_include on package_references(include);");
        Execute(connection, transaction, "create index if not exists idx_framework_references_project_id on framework_references(project_id);");
        Execute(connection, transaction, "create index if not exists idx_framework_references_include on framework_references(include);");
        Execute(connection, transaction, "create index if not exists idx_global_usings_project_id on global_usings(project_id);");
        Execute(connection, transaction, "create index if not exists idx_global_usings_include on global_usings(include);");
    }

    private static void SetMeta(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string key,
        string value)
    {
        using (SqliteCommand command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                insert into index_meta(key, value)
                values ($key, $value)
                on conflict(key) do update set value = excluded.value;
                """;
            command.Parameters.AddWithValue("$key", key);
            command.Parameters.AddWithValue("$value", value);
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
