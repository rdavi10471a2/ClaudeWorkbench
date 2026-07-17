using AIMonitor.Data;
using AIMonitor.MSBuild;
using Microsoft.Data.Sqlite;

namespace AIMonitor.Data.Tests;

public sealed class SolutionIndexStoreTests
{
    [Fact]
    public void SaveSnapshot_persists_projects_documents_and_diagnostics()
    {
        string databasePath = Path.Combine(Path.GetTempPath(), "AIMonitorTests", Guid.NewGuid().ToString("N"), "index.sqlite");
        SolutionIndexStore store = new(new SolutionIndexDatabase(databasePath));
        MSBuildSolutionSnapshot snapshot = new(
            @"C:\Example\Example.sln",
            [
                new MSBuildProjectSnapshot(
                    "project:test",
                    "Example",
                    @"C:\Example\Example.csproj",
                    "C#",
                    "net10.0",
                    "",
                    "Exe",
                    "Microsoft.NET.Sdk",
                    "Example",
                    "Example",
                    "enable",
                    "enable",
                    "latest",
                    [
                        new MSBuildDocumentSnapshot("document:test", "Program.cs", @"C:\Example\Program.cs", [], "abc123")
                    ],
                    [
                        new MSBuildSymbolSnapshot(
                            @"C:/Example/Program.cs::NamedType::Example.Program::3",
                            "Program",
                            "NamedType",
                            "Example",
                            "",
                            @"C:\Example\Program.cs",
                            3,
                            8,
                            "Example.Program"),
                        new MSBuildSymbolSnapshot(
                            "symbol:get-value",
                            "GetValue",
                            "Method",
                            "Example",
                            "Program",
                            @"C:\Example\Program.cs",
                            5,
                            7,
                            "Example.Program.GetValue()",
                            "Public",
                            false,
                            false,
                            false,
                            true,
                            false,
                            "Ordinary"),
                        new MSBuildSymbolSnapshot(
                            "symbol:target-method",
                            "TargetMethod",
                            "Method",
                            "Example",
                            "Program",
                            @"C:\Example\Program.cs",
                            10,
                            12,
                            "Example.Program.TargetMethod()")
                    ],
                    [
                        new MSBuildReferenceSnapshot(
                            @"C:/Example/Program.cs::NamedType::Example.Program::3",
                            @"C:\Example\Program.cs",
                            5,
                            13,
                            "IdentifierName",
                            "Program"),
                        new MSBuildReferenceSnapshot(
                            "symbol:target-method",
                            @"C:\Example\Program.cs",
                            6,
                            20,
                            "InvocationExpression",
                            "TargetMethod()"),
                        new MSBuildReferenceSnapshot(
                            @"C:/Example/Program.cs::NamedType::Example.Program::3",
                            @"C:\Example\Program.cs",
                            3,
                            1,
                            "partial_declaration",
                            "Program")
                    ],
                    [],
                    [new MSBuildProjectReferenceSnapshot(@"..\Lib\Lib.csproj", @"C:\Lib\Lib.csproj")],
                    [new MSBuildPackageReferenceSnapshot("Microsoft.Data.Sqlite", "10.0.8")],
                    [new MSBuildFrameworkReferenceSnapshot("Microsoft.WindowsDesktop.App.WindowsForms")],
                    [new MSBuildGlobalUsingSnapshot("System", "", "")],
                    ["DEBUG"])
            ],
            ["diagnostic"]);

        SolutionIndexSummary summary = store.SaveSnapshot(snapshot);
        IReadOnlyList<IndexedDocumentRow> documents = store.ListDocuments();
        IReadOnlyList<IndexedProjectRow> projects = store.ListProjects();
        IReadOnlyList<IndexedSymbolRow> symbols = store.ListSymbols();
        IReadOnlyList<IndexedReferenceRow> references = store.ListReferences(symbols[0].StableKey);
        IReadOnlyList<IndexedCallSiteRow> callSites = store.ListCallSites("symbol:target-method");
        IReadOnlyList<IndexedRelationshipRow> relationships = store.ListRelationships(symbols[0].StableKey);
        IReadOnlyList<IndexedPackageReferenceRow> packages = store.ListPackageReferences();

        Assert.Equal(1, summary.ProjectCount);
        Assert.Equal(1, summary.DocumentCount);
        Assert.Equal(1, summary.DiagnosticCount);
        Assert.Single(documents);
        Assert.Equal("Program.cs", documents[0].Name);
        Assert.Equal("abc123", documents[0].ContentHash);
        Assert.Single(projects);
        Assert.Equal("project:test", projects[0].StableKey);
        Assert.Equal("net10.0", projects[0].TargetFramework);
        Assert.Equal(3, symbols.Count);
        IndexedSymbolRow getValue = Assert.Single(symbols, symbol => symbol.StableKey == "symbol:get-value");
        Assert.Equal("Public", getValue.Accessibility);
        Assert.True(getValue.IsVirtual);
        Assert.False(getValue.IsSealed);
        Assert.Equal("Ordinary", getValue.MethodKind);
        Assert.Contains(references, reference => reference.ReferenceKind == "IdentifierName");
        Assert.Contains(references, reference => reference.ReferenceKind == "partial_declaration");
        IndexedCallSiteRow callSite = Assert.Single(callSites);
        Assert.Equal("symbol:get-value", callSite.CallerStableKey);
        Assert.Equal("GetValue", callSite.CallerName);
        Assert.Equal("symbol:target-method", callSite.TargetStableKey);
        IndexedRelationshipRow relationship = Assert.Single(relationships);
        Assert.Equal("partial_declaration", relationship.RelationshipKind);
        Assert.Equal(symbols[0].StableKey, relationship.SourceStableKey);
        Assert.Equal(symbols[0].StableKey, relationship.TargetStableKey);
        Assert.Single(packages);
        Assert.Equal("Microsoft.Data.Sqlite", packages[0].Include);
        Assert.False(TableExists(databasePath, "index_runs"));
        Assert.True(TableExists(databasePath, "solution_state"));
    }

    [Fact]
    public void SaveSnapshot_replaces_previous_snapshot_rows()
    {
        string databasePath = Path.Combine(Path.GetTempPath(), "AIMonitorTests", Guid.NewGuid().ToString("N"), "index.sqlite");
        SolutionIndexStore store = new(new SolutionIndexDatabase(databasePath));

        store.SaveSnapshot(CreateSnapshot(
            @"C:\Example\Example.sln",
            "project:old",
            "Old",
            @"C:\Example\Old.csproj",
            "Old.cs",
            @"C:\Example\Old.cs",
            "symbol:old",
            "OldType",
            "Old.Package",
            "old diagnostic"));

        SolutionIndexSummary summary = store.SaveSnapshot(CreateSnapshot(
            @"C:\Example\Example.sln",
            "project:new",
            "New",
            @"C:\Example\New.csproj",
            "New.cs",
            @"C:\Example\New.cs",
            "symbol:new",
            "NewType",
            "New.Package",
            "new diagnostic"));

        Assert.Equal(1, summary.ProjectCount);
        Assert.Equal(1, summary.DocumentCount);
        Assert.Equal(1, summary.DiagnosticCount);
        Assert.DoesNotContain(store.ListProjects(), project => project.StableKey == "project:old");
        Assert.DoesNotContain(store.ListDocuments(), document => document.Name == "Old.cs");
        Assert.DoesNotContain(store.ListSymbols(), symbol => symbol.StableKey == "symbol:old");
        Assert.DoesNotContain(store.ListReferences(), reference => reference.TargetStableKey == "symbol:old");
        Assert.DoesNotContain(store.ListPackageReferences(), package => package.Include == "Old.Package");
        Assert.Contains(store.ListProjects(), project => project.StableKey == "project:new");
        Assert.Contains(store.ListDocuments(), document => document.Name == "New.cs");
        Assert.Contains(store.ListSymbols(), symbol => symbol.StableKey == "symbol:new");
        Assert.Contains(store.ListReferences(), reference => reference.TargetStableKey == "symbol:new");
        Assert.Contains(store.ListPackageReferences(), package => package.Include == "New.Package");
    }

    [Fact]
    public void SaveSnapshot_zero_project_snapshot_does_not_clear_existing_index()
    {
        string databasePath = Path.Combine(Path.GetTempPath(), "AIMonitorTests", Guid.NewGuid().ToString("N"), "index.sqlite");
        SolutionIndexStore store = new(new SolutionIndexDatabase(databasePath));

        store.SaveSnapshot(CreateSnapshot(
            @"C:\Example\Example.sln",
            "project:old",
            "Old",
            @"C:\Example\Old.csproj",
            "Old.cs",
            @"C:\Example\Old.cs",
            "symbol:old",
            "OldType",
            "Old.Package",
            "old diagnostic"));

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() =>
            store.SaveSnapshot(new MSBuildSolutionSnapshot(
                @"C:\Example\Example.sln",
                [],
                ["degraded load"])));

        Assert.Contains("zero-project snapshot", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Single(store.ListProjects());
        Assert.Contains(store.ListDocuments(), document => document.Name == "Old.cs");
        Assert.Contains(store.ListSymbols(), symbol => symbol.StableKey == "symbol:old");
        Assert.Equal(1, store.GetSummary().ProjectCount);
    }

    [Fact]
    public void ReplaceProjectFiles_scoped_refresh_preserves_inbound_cross_project_references()
    {
        // HIGH #1 regression guard. Project B references a symbol declared in project A (an inbound cross-project
        // reference row owned by B whose target_stable_key points at A's symbol). A project-scoped refresh of A
        // deletes and re-inserts A's symbol rows. Now that the cross-symbol stable_key FK has been removed from the
        // schema, the scoped delete can no longer cascade-delete B's inbound reference rows: B's inbound reference
        // into A must survive the scoped refresh of A unchanged.
        string databasePath = Path.Combine(Path.GetTempPath(), "AIMonitorTests", Guid.NewGuid().ToString("N"), "index.sqlite");
        SolutionIndexStore store = new(new SolutionIndexDatabase(databasePath));

        const string projectAPath = @"C:\Example\A\A.csproj";
        const string projectBPath = @"C:\Example\B\B.csproj";
        const string symbolAKey = @"C:/Example/A/Widget.cs::NamedType::A.Widget::1";

        MSBuildProjectSnapshot projectA = new(
            "project:A",
            "A",
            projectAPath,
            "C#",
            "net10.0",
            "",
            "Library",
            "Microsoft.NET.Sdk",
            "A",
            "A",
            "enable",
            "enable",
            "latest",
            [new MSBuildDocumentSnapshot("document:A", "Widget.cs", @"C:\Example\A\Widget.cs", [], "hashA")],
            [
                new MSBuildSymbolSnapshot(
                    symbolAKey,
                    "Widget",
                    "NamedType",
                    "A",
                    "",
                    @"C:\Example\A\Widget.cs",
                    1,
                    20,
                    "A.Widget")
            ],
            [],
            [],
            [],
            [],
            [],
            [],
            []);

        // Project B references A.Widget — a cross-project reference row owned by B, targeting A's symbol stable key.
        MSBuildProjectSnapshot projectB = new(
            "project:B",
            "B",
            projectBPath,
            "C#",
            "net10.0",
            "",
            "Library",
            "Microsoft.NET.Sdk",
            "B",
            "B",
            "enable",
            "enable",
            "latest",
            [new MSBuildDocumentSnapshot("document:B", "Consumer.cs", @"C:\Example\B\Consumer.cs", [], "hashB")],
            [
                new MSBuildSymbolSnapshot(
                    @"C:/Example/B/Consumer.cs::NamedType::B.Consumer::1",
                    "Consumer",
                    "NamedType",
                    "B",
                    "",
                    @"C:\Example\B\Consumer.cs",
                    1,
                    20,
                    "B.Consumer")
            ],
            [
                new MSBuildReferenceSnapshot(
                    symbolAKey,
                    @"C:\Example\B\Consumer.cs",
                    5,
                    13,
                    "IdentifierName",
                    "Widget")
            ],
            [],
            [new MSBuildProjectReferenceSnapshot(@"..\A\A.csproj", projectAPath)],
            [],
            [],
            [],
            []);

        store.SaveSnapshot(new MSBuildSolutionSnapshot(@"C:\Example\Example.sln", [projectA, projectB], []));

        // Precondition: B's inbound cross-project reference into A exists.
        IReadOnlyList<IndexedReferenceRow> beforeRefresh = store.ListReferences(symbolAKey);
        Assert.Contains(
            beforeRefresh,
            reference => reference.TargetStableKey == symbolAKey
                && reference.ProjectPath == Path.GetFullPath(projectBPath));

        // Scope-refresh project A only, re-inserting A's symbol at the SAME stable key (as a real edit would).
        store.ReplaceProjectFiles(
            @"C:\Example\Example.sln",
            projectAPath,
            [@"C:\Example\A\Widget.cs"],
            [new MSBuildDocumentSnapshot("document:A", "Widget.cs", @"C:\Example\A\Widget.cs", [], "hashA2")],
            [
                new MSBuildSymbolSnapshot(
                    symbolAKey,
                    "Widget",
                    "NamedType",
                    "A",
                    "",
                    @"C:\Example\A\Widget.cs",
                    1,
                    25,
                    "A.Widget")
            ],
            []);

        // B's inbound reference into A must survive the scoped refresh of A.
        IReadOnlyList<IndexedReferenceRow> afterRefresh = store.ListReferences(symbolAKey);
        Assert.Contains(
            afterRefresh,
            reference => reference.TargetStableKey == symbolAKey
                && reference.ProjectPath == Path.GetFullPath(projectBPath));
    }

    private static MSBuildSolutionSnapshot CreateSnapshot(
        string inputPath,
        string projectKey,
        string projectName,
        string projectPath,
        string documentName,
        string documentPath,
        string symbolKey,
        string symbolName,
        string packageName,
        string diagnostic)
    {
        return new MSBuildSolutionSnapshot(
            inputPath,
            [
                new MSBuildProjectSnapshot(
                    projectKey,
                    projectName,
                    projectPath,
                    "C#",
                    "net10.0",
                    "",
                    "Library",
                    "Microsoft.NET.Sdk",
                    projectName,
                    projectName,
                    "enable",
                    "enable",
                    "latest",
                    [
                        new MSBuildDocumentSnapshot($"document:{documentName}", documentName, documentPath, [], documentName)
                    ],
                    [
                        new MSBuildSymbolSnapshot(
                            symbolKey,
                            symbolName,
                            "NamedType",
                            projectName,
                            "",
                            documentPath,
                            1,
                            1,
                            $"{projectName}.{symbolName}")
                    ],
                    [
                        new MSBuildReferenceSnapshot(
                            symbolKey,
                            documentPath,
                            1,
                            1,
                            "IdentifierName",
                            symbolName)
                    ],
                    [],
                    [],
                    [new MSBuildPackageReferenceSnapshot(packageName, "1.0.0")],
                    [],
                    [],
                    []),
            ],
            [diagnostic]);
    }

    private static bool TableExists(string databasePath, string tableName)
    {
        using SqliteConnection connection = new($"Data Source={databasePath}");
        connection.Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            select count(*)
            from sqlite_master
            where type = 'table' and name = $tableName;
            """;
        command.Parameters.AddWithValue("$tableName", tableName);

        object? result = command.ExecuteScalar();
        return Convert.ToInt32(result) > 0;
    }
}
