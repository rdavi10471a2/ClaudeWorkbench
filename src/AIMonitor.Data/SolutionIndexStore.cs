using AIMonitor.MSBuild;
using Microsoft.Data.Sqlite;

namespace AIMonitor.Data;

public sealed class SolutionIndexStore
{
    private readonly SolutionIndexDatabase database;

    public SolutionIndexStore(SolutionIndexDatabase database)
    {
        this.database = database;
    }

    public SolutionIndexSummary SaveSnapshot(
        MSBuildSolutionSnapshot snapshot,
        Action<string, long, IReadOnlyDictionary<string, string>>? timingSink = null)
    {
        database.EnsureCreated();
        SolutionIndexSummary previousSummary = GetSummary();
        if (snapshot.Projects.Count == 0 && previousSummary.ProjectCount > 0)
        {
            throw new InvalidOperationException("Refusing to replace an existing solution index with a degraded zero-project snapshot.");
        }

        using SqliteConnection connection = database.OpenConnection();
        using SqliteTransaction transaction = connection.BeginTransaction();

        Measure("index.sqlite.clear-current-state", timingSink, () => ClearCurrentState(connection, transaction));
        Measure("index.sqlite.save-solution-state", timingSink, () => SaveSolutionState(connection, transaction, snapshot));

        foreach (MSBuildProjectSnapshot project in snapshot.Projects)
        {
            long projectId = InsertProject(connection, transaction, project);
            Dictionary<string, string> projectProperties = CreateProjectTimingProperties(project);
            Measure("index.sqlite.insert-documents", timingSink, projectProperties, () => InsertDocuments(connection, transaction, projectId, project.Documents));
            Measure("index.sqlite.insert-symbols", timingSink, projectProperties, () => InsertSymbols(connection, transaction, projectId, project.Symbols));
            Measure("index.sqlite.insert-references", timingSink, projectProperties, () => InsertReferences(connection, transaction, projectId, project.References));
            Measure("index.sqlite.insert-call-sites", timingSink, projectProperties, () => InsertCallSites(connection, transaction, projectId, project.Symbols, project.References));
            Measure("index.sqlite.insert-relationships", timingSink, projectProperties, () => InsertRelationships(connection, transaction, projectId, project.Symbols, project.References));
            Measure("index.sqlite.insert-project-references", timingSink, projectProperties, () => InsertProjectReferences(connection, transaction, projectId, project.ProjectReferences));
            Measure("index.sqlite.insert-package-references", timingSink, projectProperties, () => InsertPackageReferences(connection, transaction, projectId, project.PackageReferences));
            Measure("index.sqlite.insert-framework-references", timingSink, projectProperties, () => InsertFrameworkReferences(connection, transaction, projectId, project.FrameworkReferences));
            Measure("index.sqlite.insert-global-usings", timingSink, projectProperties, () => InsertGlobalUsings(connection, transaction, projectId, project.GlobalUsings));
        }

        foreach (string diagnostic in snapshot.Diagnostics)
        {
            Execute(connection, transaction, """
                insert into diagnostics(message)
                values ($message);
                """,
                ("$message", diagnostic));
        }

        // A full rebuild repopulates every symbol-dependent table, so it clears the schema-upgrade rebuild marker as
        // part of the same transaction that writes the fresh rows.
        Execute(connection, transaction, "delete from index_meta where key = $key;", ("$key", SolutionIndexDatabase.NeedsFullRebuildKey));

        Measure("index.sqlite.commit", timingSink, () => transaction.Commit());
        return GetSummary();
    }

    public SolutionIndexSummary ReplaceProjectFiles(
        string inputPath,
        string projectPath,
        IReadOnlyList<string> filePaths,
        IReadOnlyList<MSBuildDocumentSnapshot> documents,
        IReadOnlyList<MSBuildSymbolSnapshot> symbols,
        IReadOnlyList<MSBuildReferenceSnapshot> references,
        Action<string, long, IReadOnlyDictionary<string, string>>? timingSink = null)
    {
        database.EnsureCreated();
        string normalizedProjectPath = Path.GetFullPath(projectPath);
        string[] normalizedFilePaths = filePaths
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (normalizedFilePaths.Length == 0)
        {
            throw new ArgumentException("At least one file path is required.", nameof(filePaths));
        }

        using (SqliteConnection connection = database.OpenConnection())
        {
            // No cross-symbol FK exists anymore (the *_stable_key columns are plain text after the schema upgrade), so
            // this single-project delete+reinsert cannot cascade-delete other projects' inbound rows. Insert order no
            // longer matters and no foreign-key toggling is required. The scoped->full inbound-dependent guard still
            // lives in PostAcceptIndexRefreshService for the stale-key case (a target symbol that genuinely moved).
            using (SqliteTransaction transaction = connection.BeginTransaction())
            {
                long projectId = Measure("index.sqlite.get-project-id", timingSink, () => GetProjectId(connection, transaction, normalizedProjectPath));
                Dictionary<string, string> fileProperties = new(StringComparer.Ordinal)
                {
                    ["projectPath"] = normalizedProjectPath,
                    ["fileCount"] = normalizedFilePaths.Length.ToString(),
                    ["documentCount"] = documents.Count.ToString(),
                    ["symbolCount"] = symbols.Count.ToString(),
                    ["referenceCount"] = references.Count.ToString()
                };
                Measure("index.sqlite.delete-project-rows", timingSink, fileProperties, () => DeleteProjectRows(connection, transaction, projectId));

                Measure("index.sqlite.insert-documents", timingSink, fileProperties, () => InsertDocuments(connection, transaction, projectId, documents));
                Measure("index.sqlite.insert-symbols", timingSink, fileProperties, () => InsertSymbols(connection, transaction, projectId, symbols));
                Measure("index.sqlite.insert-references", timingSink, fileProperties, () => InsertReferences(connection, transaction, projectId, references));
                Measure("index.sqlite.insert-call-sites", timingSink, fileProperties, () => InsertCallSites(connection, transaction, projectId, symbols, references));
                Measure("index.sqlite.insert-relationships", timingSink, fileProperties, () => InsertRelationships(connection, transaction, projectId, symbols, references));
                Measure("index.sqlite.save-current-solution-state", timingSink, fileProperties, () => SaveCurrentSolutionState(connection, transaction, inputPath));
                Measure("index.sqlite.commit", timingSink, fileProperties, () => transaction.Commit());
            }
        }

        return GetSummary();
    }

    private static void Measure(
        string phase,
        Action<string, long, IReadOnlyDictionary<string, string>>? timingSink,
        Action action)
    {
        Measure(phase, timingSink, new Dictionary<string, string>(), action);
    }

    private static void Measure(
        string phase,
        Action<string, long, IReadOnlyDictionary<string, string>>? timingSink,
        IReadOnlyDictionary<string, string> properties,
        Action action)
    {
        System.Diagnostics.Stopwatch stopwatch = System.Diagnostics.Stopwatch.StartNew();
        action();
        stopwatch.Stop();
        timingSink?.Invoke(phase, stopwatch.ElapsedMilliseconds, properties);
    }

    private static T Measure<T>(
        string phase,
        Action<string, long, IReadOnlyDictionary<string, string>>? timingSink,
        Func<T> action)
    {
        System.Diagnostics.Stopwatch stopwatch = System.Diagnostics.Stopwatch.StartNew();
        T result = action();
        stopwatch.Stop();
        timingSink?.Invoke(phase, stopwatch.ElapsedMilliseconds, new Dictionary<string, string>());
        return result;
    }

    private static Dictionary<string, string> CreateProjectTimingProperties(MSBuildProjectSnapshot project)
    {
        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["projectPath"] = project.ProjectPath,
            ["documentCount"] = project.Documents.Count.ToString(),
            ["symbolCount"] = project.Symbols.Count.ToString(),
            ["referenceCount"] = project.References.Count.ToString()
        };
    }

    public SolutionIndexSummary GetSummary()
    {
        database.EnsureCreated();
        using SqliteConnection connection = database.OpenConnection();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            select input_path, indexed_at_utc, project_count, document_count, diagnostic_count
            from solution_state
            where id = 1;
            """;

        using SqliteDataReader reader = command.ExecuteReader();
        if (!reader.Read())
        {
            return new SolutionIndexSummary(string.Empty, DateTimeOffset.MinValue, 0, 0, 0);
        }

        return new SolutionIndexSummary(
            reader.GetString(0),
            DateTimeOffset.Parse(reader.GetString(1)),
            reader.GetInt32(2),
            reader.GetInt32(3),
            reader.GetInt32(4));
    }

    public IReadOnlyList<IndexedDocumentRow> ListDocuments()
    {
        database.EnsureCreated();
        using SqliteConnection connection = database.OpenConnection();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            select projects.project_path, documents.stable_key, documents.name, documents.file_path,
                   documents.folders, documents.content_hash
            from documents
            inner join projects on projects.id = documents.project_id
            order by documents.file_path;
            """;

        List<IndexedDocumentRow> rows = [];
        using SqliteDataReader reader = command.ExecuteReader();
        while (reader.Read())
        {
            rows.Add(new IndexedDocumentRow(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.GetString(5)));
        }

        return rows;
    }

    public IReadOnlyList<IndexedSymbolRow> ListSymbols()
    {
        database.EnsureCreated();
        using SqliteConnection connection = database.OpenConnection();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            select projects.project_path, symbols.stable_key, symbols.name, symbols.kind,
                   symbols.namespace, symbols.containing_type, symbols.file_path,
                   symbols.start_line, symbols.end_line, symbols.signature,
                   symbols.accessibility, symbols.is_static, symbols.is_abstract,
                   symbols.is_sealed, symbols.is_virtual, symbols.is_override,
                   symbols.method_kind
            from symbols
            inner join projects on projects.id = symbols.project_id
            order by symbols.file_path, symbols.start_line, symbols.name;
            """;

        List<IndexedSymbolRow> rows = [];
        using SqliteDataReader reader = command.ExecuteReader();
        while (reader.Read())
        {
            rows.Add(new IndexedSymbolRow(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.GetString(5),
                reader.GetString(6),
                reader.GetInt32(7),
                reader.GetInt32(8),
                reader.GetString(9),
                reader.GetString(10),
                reader.GetInt32(11) != 0,
                reader.GetInt32(12) != 0,
                reader.GetInt32(13) != 0,
                reader.GetInt32(14) != 0,
                reader.GetInt32(15) != 0,
                reader.GetString(16)));
        }

        return rows;
    }

    public IReadOnlyList<IndexedReferenceRow> ListReferences(string? stableKey = null)
    {
        database.EnsureCreated();
        using SqliteConnection connection = database.OpenConnection();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = string.IsNullOrWhiteSpace(stableKey)
            ? """
              select projects.project_path, symbol_references.target_stable_key,
                     symbol_references.file_path, symbol_references.line, symbol_references.column,
                     symbol_references.reference_kind, symbol_references.snippet,
                     coalesce(target_symbols.name, '') as target_name,
                     coalesce(target_symbols.kind, '') as target_kind,
                     coalesce(caller_symbols.stable_key, '') as caller_stable_key,
                     coalesce(caller_symbols.name, '') as caller_name,
                     coalesce(caller_symbols.kind, '') as caller_kind,
                     coalesce(documents.content_hash, '') as file_content_hash
              from symbol_references
              inner join projects on projects.id = symbol_references.project_id
              left join symbols as target_symbols
                     on target_symbols.project_id = symbol_references.project_id
                    and target_symbols.stable_key = symbol_references.target_stable_key
              left join documents
                     on documents.project_id = symbol_references.project_id
                    and documents.file_path = symbol_references.file_path
              left join symbols as caller_symbols
                     on caller_symbols.id = (
                         select contained_symbols.id
                         from symbols as contained_symbols
                         where contained_symbols.project_id = symbol_references.project_id
                           and contained_symbols.file_path = symbol_references.file_path
                           and contained_symbols.start_line <= symbol_references.line
                           and contained_symbols.end_line >= symbol_references.line
                         order by contained_symbols.start_line desc, contained_symbols.end_line asc
                         limit 1
                     )
              order by symbol_references.file_path, symbol_references.line, symbol_references.column;
              """
            : """
              select projects.project_path, symbol_references.target_stable_key,
                     symbol_references.file_path, symbol_references.line, symbol_references.column,
                     symbol_references.reference_kind, symbol_references.snippet,
                     coalesce(target_symbols.name, '') as target_name,
                     coalesce(target_symbols.kind, '') as target_kind,
                     coalesce(caller_symbols.stable_key, '') as caller_stable_key,
                     coalesce(caller_symbols.name, '') as caller_name,
                     coalesce(caller_symbols.kind, '') as caller_kind,
                     coalesce(documents.content_hash, '') as file_content_hash
              from symbol_references
              inner join projects on projects.id = symbol_references.project_id
              left join symbols as target_symbols
                     on target_symbols.project_id = symbol_references.project_id
                    and target_symbols.stable_key = symbol_references.target_stable_key
              left join documents
                     on documents.project_id = symbol_references.project_id
                    and documents.file_path = symbol_references.file_path
              left join symbols as caller_symbols
                     on caller_symbols.id = (
                         select contained_symbols.id
                         from symbols as contained_symbols
                         where contained_symbols.project_id = symbol_references.project_id
                           and contained_symbols.file_path = symbol_references.file_path
                           and contained_symbols.start_line <= symbol_references.line
                           and contained_symbols.end_line >= symbol_references.line
                         order by contained_symbols.start_line desc, contained_symbols.end_line asc
                         limit 1
                     )
              where symbol_references.target_stable_key = $stableKey
              order by symbol_references.file_path, symbol_references.line, symbol_references.column;
              """;
        if (!string.IsNullOrWhiteSpace(stableKey))
        {
            command.Parameters.AddWithValue("$stableKey", stableKey);
        }

        List<IndexedReferenceRow> rows = [];
        using SqliteDataReader reader = command.ExecuteReader();
        while (reader.Read())
        {
            rows.Add(new IndexedReferenceRow(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetInt32(3),
                reader.GetInt32(4),
                reader.GetString(5),
                reader.GetString(6),
                reader.GetString(7),
                reader.GetString(8),
                reader.GetString(9),
                reader.GetString(10),
                reader.GetString(11),
                reader.GetString(12)));
        }

        return rows;
    }

    public IReadOnlyList<IndexedCallSiteRow> ListCallSites(string? stableKey = null)
    {
        database.EnsureCreated();
        using SqliteConnection connection = database.OpenConnection();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = string.IsNullOrWhiteSpace(stableKey)
            ? """
              select projects.project_path, call_sites.caller_stable_key, call_sites.caller_name,
                     call_sites.caller_kind, call_sites.target_stable_key, call_sites.file_path,
                     call_sites.line, call_sites.column, call_sites.call_kind, call_sites.snippet
              from call_sites
              inner join projects on projects.id = call_sites.project_id
              order by call_sites.file_path, call_sites.line, call_sites.column;
              """
            : """
              select projects.project_path, call_sites.caller_stable_key, call_sites.caller_name,
                     call_sites.caller_kind, call_sites.target_stable_key, call_sites.file_path,
                     call_sites.line, call_sites.column, call_sites.call_kind, call_sites.snippet
              from call_sites
              inner join projects on projects.id = call_sites.project_id
              where call_sites.target_stable_key = $stableKey
              order by call_sites.file_path, call_sites.line, call_sites.column;
              """;
        if (!string.IsNullOrWhiteSpace(stableKey))
        {
            command.Parameters.AddWithValue("$stableKey", stableKey);
        }

        List<IndexedCallSiteRow> rows = [];
        using SqliteDataReader reader = command.ExecuteReader();
        while (reader.Read())
        {
            rows.Add(new IndexedCallSiteRow(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.GetString(5),
                reader.GetInt32(6),
                reader.GetInt32(7),
                reader.GetString(8),
                reader.GetString(9)));
        }

        return rows;
    }

    public IReadOnlyList<IndexedRelationshipRow> ListRelationships(
        string? stableKey = null,
        string direction = "both",
        string? relationshipKind = null)
    {
        database.EnsureCreated();
        using SqliteConnection connection = database.OpenConnection();
        using SqliteCommand command = connection.CreateCommand();
        List<string> predicates = [];
        if (!string.IsNullOrWhiteSpace(stableKey))
        {
            string normalizedDirection = NormalizeRelationshipDirection(direction);
            if (normalizedDirection == "outgoing")
            {
                predicates.Add("symbol_relationships.source_stable_key = $stableKey");
            }
            else if (normalizedDirection == "incoming")
            {
                predicates.Add("symbol_relationships.target_stable_key = $stableKey");
            }
            else
            {
                predicates.Add("(symbol_relationships.source_stable_key = $stableKey or symbol_relationships.target_stable_key = $stableKey)");
            }
        }

        if (!string.IsNullOrWhiteSpace(relationshipKind))
        {
            predicates.Add("symbol_relationships.relationship_kind = $relationshipKind");
        }

        string whereClause = predicates.Count == 0 ? string.Empty : "where " + string.Join(" and ", predicates);
        command.CommandText = $"""
            select projects.project_path, symbol_relationships.source_stable_key, symbol_relationships.source_name,
                   symbol_relationships.source_kind, symbol_relationships.target_stable_key, symbol_relationships.target_name,
                   symbol_relationships.target_kind, symbol_relationships.relationship_kind, symbol_relationships.file_path,
                   symbol_relationships.line, symbol_relationships.column, symbol_relationships.snippet
            from symbol_relationships
            inner join projects on projects.id = symbol_relationships.project_id
            {whereClause}
            order by symbol_relationships.file_path, symbol_relationships.line, symbol_relationships.column, symbol_relationships.relationship_kind;
            """;
        if (!string.IsNullOrWhiteSpace(stableKey))
        {
            command.Parameters.AddWithValue("$stableKey", stableKey);
        }

        if (!string.IsNullOrWhiteSpace(relationshipKind))
        {
            command.Parameters.AddWithValue("$relationshipKind", relationshipKind);
        }

        List<IndexedRelationshipRow> rows = [];
        using SqliteDataReader reader = command.ExecuteReader();
        while (reader.Read())
        {
            rows.Add(new IndexedRelationshipRow(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.GetString(5),
                reader.GetString(6),
                reader.GetString(7),
                reader.GetString(8),
                reader.GetInt32(9),
                reader.GetInt32(10),
                reader.GetString(11)));
        }

        return rows;
    }

    public IReadOnlyList<IndexedProjectRow> ListProjects()
    {
        database.EnsureCreated();
        using SqliteConnection connection = database.OpenConnection();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            select stable_key, name, project_path, language, target_framework, target_frameworks, output_type,
                   sdk, assembly_name, root_namespace, nullable, implicit_usings, lang_version,
                   preprocessor_symbols
            from projects
            order by project_path;
            """;

        List<IndexedProjectRow> rows = [];
        using SqliteDataReader reader = command.ExecuteReader();
        while (reader.Read())
        {
            rows.Add(new IndexedProjectRow(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.GetString(5),
                reader.GetString(6),
                reader.GetString(7),
                reader.GetString(8),
                reader.GetString(9),
                reader.GetString(10),
                reader.GetString(11),
                reader.GetString(12),
                reader.GetString(13)));
        }

        return rows;
    }

    public IReadOnlyList<IndexedPackageReferenceRow> ListPackageReferences()
    {
        database.EnsureCreated();
        using SqliteConnection connection = database.OpenConnection();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            select projects.project_path, package_references.include, package_references.version
            from package_references
            inner join projects on projects.id = package_references.project_id
            order by projects.project_path, package_references.include;
            """;

        List<IndexedPackageReferenceRow> rows = [];
        using SqliteDataReader reader = command.ExecuteReader();
        while (reader.Read())
        {
            rows.Add(new IndexedPackageReferenceRow(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2)));
        }

        return rows;
    }

    private static void ClearCurrentState(SqliteConnection connection, SqliteTransaction transaction)
    {
        Execute(connection, transaction, "delete from diagnostics;");
        Execute(connection, transaction, "delete from global_usings;");
        Execute(connection, transaction, "delete from framework_references;");
        Execute(connection, transaction, "delete from package_references;");
        Execute(connection, transaction, "delete from project_references;");
        Execute(connection, transaction, "delete from symbol_relationships;");
        Execute(connection, transaction, "delete from call_sites;");
        Execute(connection, transaction, "delete from symbol_references;");
        Execute(connection, transaction, "delete from symbols;");
        Execute(connection, transaction, "delete from documents;");
        Execute(connection, transaction, "delete from projects;");
        Execute(connection, transaction, "delete from solution_state;");
    }

    private static void SaveSolutionState(
        SqliteConnection connection,
        SqliteTransaction transaction,
        MSBuildSolutionSnapshot snapshot)
    {
        Execute(connection, transaction, """
            insert into solution_state(id, input_path, indexed_at_utc, project_count, document_count, diagnostic_count)
            values (1, $inputPath, $indexedAtUtc, $projectCount, $documentCount, $diagnosticCount);
            """,
            ("$inputPath", snapshot.InputPath),
            ("$indexedAtUtc", DateTimeOffset.UtcNow.ToString("O")),
            ("$projectCount", snapshot.Projects.Count),
            ("$documentCount", snapshot.Projects.Sum(project => project.Documents.Count)),
            ("$diagnosticCount", snapshot.Diagnostics.Count));
    }

    private static void SaveCurrentSolutionState(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string inputPath)
    {
        int projectCount = Convert.ToInt32(ExecuteScalar(connection, transaction, "select count(*) from projects;") ?? 0);
        int documentCount = Convert.ToInt32(ExecuteScalar(connection, transaction, "select count(*) from documents;") ?? 0);
        int diagnosticCount = Convert.ToInt32(ExecuteScalar(connection, transaction, "select count(*) from diagnostics;") ?? 0);
        Execute(connection, transaction, "delete from solution_state;");
        Execute(connection, transaction, """
            insert into solution_state(id, input_path, indexed_at_utc, project_count, document_count, diagnostic_count)
            values (1, $inputPath, $indexedAtUtc, $projectCount, $documentCount, $diagnosticCount);
            """,
            ("$inputPath", Path.GetFullPath(inputPath)),
            ("$indexedAtUtc", DateTimeOffset.UtcNow.ToString("O")),
            ("$projectCount", projectCount),
            ("$documentCount", documentCount),
            ("$diagnosticCount", diagnosticCount));
    }

    private static long GetProjectId(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string projectPath)
    {
        object? result = ExecuteScalar(connection, transaction, """
            select id
            from projects
            where project_path = $projectPath;
            """,
            ("$projectPath", projectPath));
        if (result is null || result == DBNull.Value)
        {
            throw new InvalidOperationException("The project is not present in the existing solution index: " + projectPath);
        }

        return Convert.ToInt64(result);
    }

    private static void DeleteProjectRows(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long projectId)
    {
        Execute(connection, transaction, """
            delete from symbol_relationships
            where project_id = $projectId;
            """,
            ("$projectId", projectId));
        Execute(connection, transaction, """
            delete from call_sites
            where project_id = $projectId;
            """,
            ("$projectId", projectId));
        Execute(connection, transaction, """
            delete from symbol_references
            where project_id = $projectId;
            """,
            ("$projectId", projectId));
        Execute(connection, transaction, """
            delete from symbols
            where project_id = $projectId;
            """,
            ("$projectId", projectId));
        Execute(connection, transaction, """
            delete from documents
            where project_id = $projectId;
            """,
            ("$projectId", projectId));
    }

    private static long InsertProject(
        SqliteConnection connection,
        SqliteTransaction transaction,
        MSBuildProjectSnapshot project)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            insert into projects(stable_key, name, project_path, language, target_framework, target_frameworks,
                                 output_type, sdk, assembly_name, root_namespace, nullable,
                                 implicit_usings, lang_version, preprocessor_symbols)
            values ($stableKey, $name, $projectPath, $language, $targetFramework, $targetFrameworks,
                    $outputType, $sdk, $assemblyName, $rootNamespace, $nullable,
                    $implicitUsings, $langVersion, $preprocessorSymbols);

            select last_insert_rowid();
            """;
        command.Parameters.AddWithValue("$stableKey", project.StableProjectKey);
        command.Parameters.AddWithValue("$name", project.Name);
        command.Parameters.AddWithValue("$projectPath", project.ProjectPath);
        command.Parameters.AddWithValue("$language", project.Language);
        command.Parameters.AddWithValue("$targetFramework", project.TargetFramework);
        command.Parameters.AddWithValue("$targetFrameworks", project.TargetFrameworks);
        command.Parameters.AddWithValue("$outputType", project.OutputType);
        command.Parameters.AddWithValue("$sdk", project.Sdk);
        command.Parameters.AddWithValue("$assemblyName", project.AssemblyName);
        command.Parameters.AddWithValue("$rootNamespace", project.RootNamespace);
        command.Parameters.AddWithValue("$nullable", project.Nullable);
        command.Parameters.AddWithValue("$implicitUsings", project.ImplicitUsings);
        command.Parameters.AddWithValue("$langVersion", project.LangVersion);
        command.Parameters.AddWithValue("$preprocessorSymbols", string.Join(";", project.PreprocessorSymbols));

        object? result = command.ExecuteScalar();
        return Convert.ToInt64(result);
    }

    private static void InsertDocuments(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long projectId,
        IReadOnlyList<MSBuildDocumentSnapshot> documents)
    {
        foreach (MSBuildDocumentSnapshot document in documents)
        {
            Execute(connection, transaction, """
                insert into documents(project_id, stable_key, name, file_path, folders, content_hash)
                values ($projectId, $stableKey, $name, $filePath, $folders, $contentHash);
                """,
                ("$projectId", projectId),
                ("$stableKey", document.StableDocumentKey),
                ("$name", document.Name),
                ("$filePath", document.FilePath),
                ("$folders", string.Join("/", document.Folders)),
                ("$contentHash", document.ContentHash));
        }
    }

    private static void InsertSymbols(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long projectId,
        IReadOnlyList<MSBuildSymbolSnapshot> symbols)
    {
        foreach (MSBuildSymbolSnapshot symbol in symbols)
        {
            Execute(connection, transaction, """
                insert into symbols(project_id, stable_key, name, kind, namespace, containing_type,
                                    file_path, start_line, end_line, signature, accessibility,
                                    is_static, is_abstract, is_sealed, is_virtual, is_override,
                                    method_kind)
                values ($projectId, $stableKey, $name, $kind, $namespace, $containingType,
                        $filePath, $startLine, $endLine, $signature, $accessibility,
                        $isStatic, $isAbstract, $isSealed, $isVirtual, $isOverride,
                        $methodKind);
                """,
                ("$projectId", projectId),
                ("$stableKey", symbol.StableKey),
                ("$name", symbol.Name),
                ("$kind", symbol.Kind),
                ("$namespace", symbol.Namespace),
                ("$containingType", symbol.ContainingType),
                ("$filePath", symbol.FilePath),
                ("$startLine", symbol.StartLine),
                ("$endLine", symbol.EndLine),
                ("$signature", symbol.Signature),
                ("$accessibility", symbol.Accessibility),
                ("$isStatic", symbol.IsStatic ? 1 : 0),
                ("$isAbstract", symbol.IsAbstract ? 1 : 0),
                ("$isSealed", symbol.IsSealed ? 1 : 0),
                ("$isVirtual", symbol.IsVirtual ? 1 : 0),
                ("$isOverride", symbol.IsOverride ? 1 : 0),
                ("$methodKind", symbol.MethodKind));
        }
    }

    private static void InsertReferences(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long projectId,
        IReadOnlyList<MSBuildReferenceSnapshot> references)
    {
        foreach (MSBuildReferenceSnapshot reference in references)
        {
            Execute(connection, transaction, """
                insert into symbol_references(project_id, target_stable_key, file_path, line, column, reference_kind, snippet)
                values ($projectId, $targetStableKey, $filePath, $line, $column, $referenceKind, $snippet);
                """,
                ("$projectId", projectId),
                ("$targetStableKey", reference.TargetStableKey),
                ("$filePath", reference.FilePath),
                ("$line", reference.Line),
                ("$column", reference.Column),
                ("$referenceKind", reference.ReferenceKind),
                ("$snippet", reference.Snippet));
        }
    }

    private static void InsertCallSites(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long projectId,
        IReadOnlyList<MSBuildSymbolSnapshot> symbols,
        IReadOnlyList<MSBuildReferenceSnapshot> references)
    {
        foreach (MSBuildReferenceSnapshot reference in references.Where(IsCallReference))
        {
            MSBuildSymbolSnapshot? caller = FindContainingSymbol(symbols, reference.FilePath, reference.Line);
            if (caller is null)
            {
                continue;
            }

            Execute(connection, transaction, """
                insert into call_sites(project_id, caller_stable_key, caller_name, caller_kind, target_stable_key,
                                       file_path, line, column, call_kind, snippet)
                values ($projectId, $callerStableKey, $callerName, $callerKind, $targetStableKey,
                        $filePath, $line, $column, $callKind, $snippet);
                """,
                ("$projectId", projectId),
                ("$callerStableKey", caller.StableKey),
                ("$callerName", caller.Name),
                ("$callerKind", caller.Kind),
                ("$targetStableKey", reference.TargetStableKey),
                ("$filePath", reference.FilePath),
                ("$line", reference.Line),
                ("$column", reference.Column),
                ("$callKind", reference.ReferenceKind),
                ("$snippet", reference.Snippet));
        }
    }

    private static void InsertRelationships(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long projectId,
        IReadOnlyList<MSBuildSymbolSnapshot> symbols,
        IReadOnlyList<MSBuildReferenceSnapshot> references)
    {
        Dictionary<string, MSBuildSymbolSnapshot> symbolsByKey = symbols.ToDictionary(symbol => symbol.StableKey, StringComparer.Ordinal);
        foreach (MSBuildReferenceSnapshot reference in references.Where(reference => IsRelationshipKind(reference.ReferenceKind)))
        {
            if (!symbolsByKey.TryGetValue(reference.TargetStableKey, out MSBuildSymbolSnapshot? target))
            {
                continue;
            }

            MSBuildSymbolSnapshot? source = FindRelationshipSource(symbols, reference.FilePath, reference.Line);
            if (source is null)
            {
                continue;
            }

            Execute(connection, transaction, """
                insert into symbol_relationships(project_id, source_stable_key, source_name, source_kind,
                                                  target_stable_key, target_name, target_kind, relationship_kind,
                                                  file_path, line, column, snippet)
                values ($projectId, $sourceStableKey, $sourceName, $sourceKind,
                        $targetStableKey, $targetName, $targetKind, $relationshipKind,
                        $filePath, $line, $column, $snippet);
                """,
                ("$projectId", projectId),
                ("$sourceStableKey", source.StableKey),
                ("$sourceName", source.Name),
                ("$sourceKind", source.Kind),
                ("$targetStableKey", target.StableKey),
                ("$targetName", target.Name),
                ("$targetKind", target.Kind),
                ("$relationshipKind", reference.ReferenceKind),
                ("$filePath", reference.FilePath),
                ("$line", reference.Line),
                ("$column", reference.Column),
                ("$snippet", reference.Snippet));
        }
    }

    private static void InsertProjectReferences(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long projectId,
        IReadOnlyList<MSBuildProjectReferenceSnapshot> references)
    {
        foreach (MSBuildProjectReferenceSnapshot reference in references)
        {
            Execute(connection, transaction, """
                insert into project_references(project_id, include, full_path)
                values ($projectId, $include, $fullPath);
                """,
                ("$projectId", projectId),
                ("$include", reference.Include),
                ("$fullPath", reference.FullPath));
        }
    }

    private static void InsertPackageReferences(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long projectId,
        IReadOnlyList<MSBuildPackageReferenceSnapshot> references)
    {
        foreach (MSBuildPackageReferenceSnapshot reference in references)
        {
            Execute(connection, transaction, """
                insert into package_references(project_id, include, version)
                values ($projectId, $include, $version);
                """,
                ("$projectId", projectId),
                ("$include", reference.Include),
                ("$version", reference.Version));
        }
    }

    private static void InsertFrameworkReferences(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long projectId,
        IReadOnlyList<MSBuildFrameworkReferenceSnapshot> references)
    {
        foreach (MSBuildFrameworkReferenceSnapshot reference in references)
        {
            Execute(connection, transaction, """
                insert into framework_references(project_id, include)
                values ($projectId, $include);
                """,
                ("$projectId", projectId),
                ("$include", reference.Include));
        }
    }

    private static void InsertGlobalUsings(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long projectId,
        IReadOnlyList<MSBuildGlobalUsingSnapshot> usings)
    {
        foreach (MSBuildGlobalUsingSnapshot globalUsing in usings)
        {
            Execute(connection, transaction, """
                insert into global_usings(project_id, include, is_static, alias)
                values ($projectId, $include, $isStatic, $alias);
                """,
                ("$projectId", projectId),
                ("$include", globalUsing.Include),
                ("$isStatic", globalUsing.Static),
                ("$alias", globalUsing.Alias));
        }
    }

    private static void Execute(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string commandText,
        params (string Name, object? Value)[] parameters)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = commandText;
        foreach ((string name, object? value) in parameters)
        {
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }

        command.ExecuteNonQuery();
    }

    private static object? ExecuteScalar(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string commandText,
        params (string Name, object? Value)[] parameters)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = commandText;
        foreach ((string name, object? value) in parameters)
        {
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }

        return command.ExecuteScalar();
    }

    private static MSBuildSymbolSnapshot? FindContainingSymbol(
        IReadOnlyList<MSBuildSymbolSnapshot> symbols,
        string filePath,
        int line)
    {
        return symbols
            .Where(symbol => symbol.FilePath.Equals(filePath, StringComparison.OrdinalIgnoreCase)
                && symbol.StartLine <= line
                && symbol.EndLine >= line)
            .OrderByDescending(symbol => symbol.StartLine)
            .ThenBy(symbol => symbol.EndLine)
            .FirstOrDefault();
    }

    private static MSBuildSymbolSnapshot? FindRelationshipSource(
        IReadOnlyList<MSBuildSymbolSnapshot> symbols,
        string filePath,
        int line)
    {
        return symbols.FirstOrDefault(symbol =>
                symbol.FilePath.Equals(filePath, StringComparison.OrdinalIgnoreCase)
                && symbol.StartLine == line)
            ?? FindContainingSymbol(symbols, filePath, line);
    }

    private static bool IsCallReference(MSBuildReferenceSnapshot reference)
    {
        return reference.ReferenceKind.Equals("InvocationExpression", StringComparison.Ordinal)
            || reference.ReferenceKind.Equals("ObjectCreationExpression", StringComparison.Ordinal)
            || reference.ReferenceKind.Equals("ImplicitObjectCreationExpression", StringComparison.Ordinal);
    }

    private static bool IsRelationshipKind(string referenceKind)
    {
        return referenceKind is "partial_declaration"
            or "derived_type"
            or "inherits_from"
            or "overridden_by"
            or "overrides"
            or "implemented_by"
            or "implements_interface_member";
    }

    private static string NormalizeRelationshipDirection(string direction)
    {
        string normalized = string.IsNullOrWhiteSpace(direction) ? "both" : direction.Trim().ToLowerInvariant();
        return normalized is "incoming" or "outgoing" or "both"
            ? normalized
            : throw new ArgumentException("Relationship direction must be incoming, outgoing, or both.", nameof(direction));
    }
}
