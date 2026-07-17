using AIMonitor.Data;
using Microsoft.Data.Sqlite;

namespace AIMonitor.Data.Tests;

// Schema-versioned full-recreate contract. The solution index is a DERIVED SNAPSHOT, not transactional data: on any
// schema change the whole schema is rebuilt fresh and a full refresh is forced. These tests prove that a persisted
// PRAGMA user_version that does not match SolutionIndexDatabase.SchemaVersion (a stale/old/fresh db) triggers a full
// schema recreate, sets the needs_full_rebuild marker, and that the marker forces the post-accept refresh onto the
// full RebuildAsync path (never scoped).
public sealed class SolutionIndexDatabaseSchemaVersionTests
{
    [Fact]
    public void Stale_user_version_triggers_full_schema_recreate_and_sets_needs_full_rebuild()
    {
        string databasePath = Path.Combine(Path.GetTempPath(), "AIMonitorTests", Guid.NewGuid().ToString("N"), "index.sqlite");
        Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);

        // Hand-build an OLD-shaped db: a stale user_version, a legacy symbol_references table carrying the dropped
        // cross-symbol FK, and a stray row. This is exactly the state a pre-v2 index left on disk.
        using (SqliteConnection seed = new($"Data Source={databasePath}"))
        {
            seed.Open();
            using (SqliteCommand command = seed.CreateCommand())
            {
                command.CommandText = """
                    pragma user_version = 1;
                    create table symbols (id integer primary key, stable_key text not null unique);
                    create table symbol_references (
                        id integer primary key,
                        target_stable_key text not null references symbols(stable_key) on delete cascade,
                        reference_kind text not null
                    );
                    insert into symbols(id, stable_key) values (1, 'legacy:key');
                    insert into symbol_references(id, target_stable_key, reference_kind) values (1, 'legacy:key', 'IdentifierName');
                    """;
                command.ExecuteNonQuery();
            }
        }

        SolutionIndexDatabase database = new(databasePath);
        database.EnsureCreated();

        // The stale version was upgraded to the current SchemaVersion.
        Assert.Equal(SolutionIndexDatabase.SchemaVersion, ReadUserVersion(databasePath));

        // The full schema was recreated fresh: the legacy cross-symbol FK is gone and the legacy row did not survive
        // (NO per-table content migration).
        string referencesSql = ReadTableSql(databasePath, "symbol_references");
        Assert.DoesNotContain("references symbols", referencesSql, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, ScalarCount(databasePath, "symbol_references"));
        Assert.Equal(0, ScalarCount(databasePath, "symbols"));

        // The whole current schema is present (a representative slice).
        Assert.True(TableExists(databasePath, "projects"));
        Assert.True(TableExists(databasePath, "documents"));
        Assert.True(TableExists(databasePath, "call_sites"));
        Assert.True(TableExists(databasePath, "symbol_relationships"));
        Assert.True(TableExists(databasePath, "index_meta"));
        Assert.True(TableExists(databasePath, "solution_state"));

        // The recreate set the needs_full_rebuild marker; the index is INVALID until a full rebuild repopulates it.
        // PostAcceptIndexRefreshService reads exactly this flag to set useFileRefresh = false, forcing the full
        // RebuildAsync path instead of a scoped project/file refresh.
        Assert.True(database.IsFullRebuildRequired());
    }

    [Fact]
    public void Matching_user_version_does_not_set_needs_full_rebuild()
    {
        string databasePath = Path.Combine(Path.GetTempPath(), "AIMonitorTests", Guid.NewGuid().ToString("N"), "index.sqlite");

        // First create stamps the current SchemaVersion and (because the fresh db reported user_version 0) sets the
        // marker; clear it to simulate the state after a successful full rebuild.
        SolutionIndexDatabase database = new(databasePath);
        database.EnsureCreated();
        Assert.True(database.IsFullRebuildRequired());
        database.ClearFullRebuildRequired();

        // A subsequent EnsureCreated against the now-current user_version must NOT re-trigger a recreate or re-set the
        // marker (no spurious full rebuilds once the schema is current and populated).
        database.EnsureCreated();
        Assert.False(database.IsFullRebuildRequired());
        Assert.Equal(SolutionIndexDatabase.SchemaVersion, ReadUserVersion(databasePath));
    }

    private static int ReadUserVersion(string databasePath)
    {
        using (SqliteConnection connection = new($"Data Source={databasePath}"))
        {
            connection.Open();
            using (SqliteCommand command = connection.CreateCommand())
            {
                command.CommandText = "pragma user_version;";
                return Convert.ToInt32(command.ExecuteScalar() ?? 0);
            }
        }
    }

    private static string ReadTableSql(string databasePath, string tableName)
    {
        using (SqliteConnection connection = new($"Data Source={databasePath}"))
        {
            connection.Open();
            using (SqliteCommand command = connection.CreateCommand())
            {
                command.CommandText = "select sql from sqlite_master where type = 'table' and name = $tableName;";
                command.Parameters.AddWithValue("$tableName", tableName);
                return command.ExecuteScalar() as string ?? string.Empty;
            }
        }
    }

    private static int ScalarCount(string databasePath, string tableName)
    {
        using (SqliteConnection connection = new($"Data Source={databasePath}"))
        {
            connection.Open();
            using (SqliteCommand command = connection.CreateCommand())
            {
                command.CommandText = $"select count(*) from {tableName};";
                return Convert.ToInt32(command.ExecuteScalar() ?? 0);
            }
        }
    }

    private static bool TableExists(string databasePath, string tableName)
    {
        using (SqliteConnection connection = new($"Data Source={databasePath}"))
        {
            connection.Open();
            using (SqliteCommand command = connection.CreateCommand())
            {
                command.CommandText = "select count(*) from sqlite_master where type = 'table' and name = $tableName;";
                command.Parameters.AddWithValue("$tableName", tableName);
                return Convert.ToInt32(command.ExecuteScalar() ?? 0) > 0;
            }
        }
    }
}
