using Microsoft.Data.Sqlite;

namespace AIMonitor.Data;

// Read-only SQLite verification probe over the monitor-owned solution index.
//
// These queries are deliberately product-grade, not test-only: they are (1) the authoritative cross-project
// dependency lookup the scoped-refresh path needs (the inbound-reference closure), and (2) the assertion surface
// the MCP-surface test suite uses to verify index contents (reference kinds present, cross-project edges, per-project
// counts) after a workflow run. Keeping them in AIMonitor.Data means any host (MCP, tests, diagnostics) can reuse them.
public sealed class SolutionIndexProbe
{
    private readonly SolutionIndexDatabase database;

    public SolutionIndexProbe(SolutionIndexDatabase database)
    {
        this.database = database;
    }

    // Count of reference rows per reference_kind (e.g. "IdentifierName", "InvocationExpression",
    // "razor:IdentifierName", "razor-generated:...", "inherits_from"). Proves Razor/source-gen extraction actually ran.
    public IReadOnlyList<ReferenceKindCount> GetReferenceKindCounts()
    {
        List<ReferenceKindCount> rows = new();
        using (SqliteConnection connection = database.OpenConnection())
        {
            using (SqliteCommand command = connection.CreateCommand())
            {
                command.CommandText = """
                    select reference_kind, count(*) as n
                    from symbol_references
                    group by reference_kind
                    order by n desc;
                    """;
                using (SqliteDataReader reader = command.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        rows.Add(new ReferenceKindCount(reader.GetString(0), reader.GetInt32(1)));
                    }
                }
            }
        }

        return rows;
    }

    // True when at least one reference row exists whose reference_kind starts with the given prefix
    // (e.g. "razor" for any Razor reference, "razor-generated:" for the generated-source mapped references).
    public bool HasReferenceKindPrefix(string prefix)
    {
        using (SqliteConnection connection = database.OpenConnection())
        {
            using (SqliteCommand command = connection.CreateCommand())
            {
                command.CommandText = "select exists(select 1 from symbol_references where reference_kind like $p);";
                command.Parameters.AddWithValue("$p", prefix + "%");
                return Convert.ToInt64(command.ExecuteScalar()) != 0L;
            }
        }
    }

    // Number of reference rows that cross a project boundary: the referencing project differs from the project that
    // declares the target symbol. This is exactly the population endangered by a project-scoped delete + ON DELETE CASCADE.
    public int GetCrossProjectReferenceCount()
    {
        using (SqliteConnection connection = database.OpenConnection())
        {
            using (SqliteCommand command = connection.CreateCommand())
            {
                command.CommandText = """
                    select count(*)
                    from symbol_references r
                    join symbols s on s.stable_key = r.target_stable_key
                    where r.project_id <> s.project_id;
                    """;
                return Convert.ToInt32(command.ExecuteScalar());
            }
        }
    }

    // The inbound-reference closure for a project: the distinct OTHER projects that reference (via references,
    // call sites, or relationships) a symbol declared in the given project. A project-scoped refresh of `projectPath`
    // must also refresh these (or fall back to a full rebuild), or their inbound rows are cascade-deleted and lost.
    public IReadOnlyList<string> GetInboundDependentProjectPaths(string projectPath)
    {
        List<string> projects = new();
        using (SqliteConnection connection = database.OpenConnection())
        {
            using (SqliteCommand command = connection.CreateCommand())
            {
                command.CommandText = """
                    select distinct rp.project_path
                    from (
                        select r.project_id, r.target_stable_key as key
                        from symbol_references r
                        union all
                        select c.project_id, c.target_stable_key as key
                        from call_sites c
                        union all
                        select x.project_id, x.target_stable_key as key
                        from symbol_relationships x
                        union all
                        select x.project_id, x.source_stable_key as key
                        from symbol_relationships x
                    ) edges
                    join symbols s on s.stable_key = edges.key
                    join projects sp on sp.id = s.project_id
                    join projects rp on rp.id = edges.project_id
                    where sp.project_path = $p
                      and edges.project_id <> s.project_id;
                    """;
                command.Parameters.AddWithValue("$p", projectPath);
                using (SqliteDataReader reader = command.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        projects.Add(reader.GetString(0));
                    }
                }
            }
        }

        return projects;
    }

    // Row counts per table, for sanity assertions in the suite (symbols/references/call_sites/relationships/documents/projects).
    public SolutionIndexCounts GetCounts()
    {
        using (SqliteConnection connection = database.OpenConnection())
        {
            return new SolutionIndexCounts(
                ScalarCount(connection, "projects"),
                ScalarCount(connection, "documents"),
                ScalarCount(connection, "symbols"),
                ScalarCount(connection, "symbol_references"),
                ScalarCount(connection, "call_sites"),
                ScalarCount(connection, "symbol_relationships"));
        }
    }

    private static int ScalarCount(SqliteConnection connection, string table)
    {
        using (SqliteCommand command = connection.CreateCommand())
        {
            command.CommandText = $"select count(*) from {table};";
            return Convert.ToInt32(command.ExecuteScalar());
        }
    }
}

public sealed record ReferenceKindCount(string Kind, int Count);

public sealed record SolutionIndexCounts(
    int Projects,
    int Documents,
    int Symbols,
    int References,
    int CallSites,
    int Relationships);
