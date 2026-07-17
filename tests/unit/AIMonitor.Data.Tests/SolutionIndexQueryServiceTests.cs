using AIMonitor.Core;
using AIMonitor.Data;
using AIMonitor.MSBuild;
using System.Security.Cryptography;

namespace AIMonitor.Data.Tests;

public sealed class SolutionIndexQueryServiceTests
{
    [Fact]
    public void GetMonitorStatus_does_not_create_database_when_index_is_missing()
    {
        string tempRoot = CreateTempRoot();
        MonitorSettings settings = MonitorSettings.Create(
            tempRoot,
            Path.Combine(tempRoot, "Watched", "Missing.sln"),
            Path.Combine(tempRoot, "runtime"));
        string databasePath = MonitorDataPaths.GetDefaultIndexDatabasePath(settings);
        SolutionIndexQueryService service = SolutionIndexQueryService.Create(settings);

        MonitorStatusResult status = service.GetMonitorStatus();

        Assert.False(status.DatabaseExists);
        Assert.False(File.Exists(databasePath));
        Assert.Equal(settings.WatchedSolutionPath, status.WatchedSolutionPath);
        Assert.Equal(databasePath, status.DatabasePath);
        Assert.Equal(0, status.ProjectCount);
        Assert.Equal(0, status.SymbolCount);
        Assert.Equal(0, status.ReferenceCount);
        Assert.Equal(0, status.CallSiteCount);
        Assert.Equal(0, status.RelationshipCount);
        Assert.Equal(0, status.StaleFileCount);
    }

    [Fact]
    public void Query_methods_filter_documents_symbols_references_and_status_counts()
    {
        string tempRoot = CreateTempRoot();
        string filePath = Path.Combine(tempRoot, "Watched", "Program.cs");
        Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
        File.WriteAllText(filePath, """
            namespace Example
            {
                internal static class Program
                {
                    static void Target() { }

                    static void Caller()
                    {
                        Target();
                    }
                }
            }
            """);
        MonitorSettings settings = MonitorSettings.Create(
            tempRoot,
            Path.Combine(tempRoot, "Watched", "Example.sln"),
            Path.Combine(tempRoot, "runtime"));
        SolutionIndexStore store = new(new SolutionIndexDatabase(MonitorDataPaths.GetDefaultIndexDatabasePath(settings)));
        store.SaveSnapshot(CreateSnapshot(settings.WatchedSolutionPath, filePath));
        SolutionIndexQueryService service = SolutionIndexQueryService.Create(settings);

        IReadOnlyList<IndexedDocumentRow> documents = service.ListDocuments(filePath: filePath);
        IReadOnlyList<IndexedSymbolRow> symbols = service.ListSymbols(filePath, "Program");
        IReadOnlyList<IndexedReferenceRow> references = service.ListReferencesInFile(filePath);
        IReadOnlyList<IndexedDocumentRow> relativeDocuments = service.ListDocuments(filePath: "Program.cs");
        IReadOnlyList<IndexedSymbolRow> relativeSymbols = service.ListSymbols("Program.cs", "Program");
        IReadOnlyList<IndexedReferenceRow> relativeReferences = service.ListReferencesInFile("Program.cs");
        MonitorStatusResult status = service.GetMonitorStatus();

        Assert.Single(documents);
        Assert.Single(relativeDocuments);
        Assert.False(string.IsNullOrWhiteSpace(documents[0].ContentHash));
        Assert.Single(symbols);
        Assert.Single(relativeSymbols);
        Assert.Equal(3, references.Count);
        Assert.Equal(3, relativeReferences.Count);
        Assert.Equal("symbol:program", Assert.Single(references, reference => reference.ReferenceKind == "IdentifierName").TargetStableKey);
        IndexedReferenceRow invocation = Assert.Single(references, reference => reference.ReferenceKind == "InvocationExpression");
        Assert.Equal("Target", invocation.TargetName);
        Assert.Equal("Method", invocation.TargetKind);
        Assert.Equal("symbol:caller", invocation.CallerStableKey);
        Assert.Equal("Caller", invocation.CallerName);
        Assert.Equal("Method", invocation.CallerKind);
        Assert.Equal(documents[0].ContentHash, invocation.FileContentHash);
        Assert.Equal(1, status.ProjectCount);
        Assert.Equal(1, status.DocumentCount);
        Assert.Equal(3, status.SymbolCount);
        Assert.Equal(3, status.ReferenceCount);
        Assert.Equal(1, status.CallSiteCount);
        Assert.Equal(1, status.RelationshipCount);
        Assert.Equal(0, status.StaleFileCount);

        File.AppendAllText(filePath, Environment.NewLine + "// stale");

        Assert.Equal(1, service.GetMonitorStatus().StaleFileCount);
    }

    [Fact]
    public void FindSymbols_scopes_qualified_member_text_to_containing_type()
    {
        string tempRoot = CreateTempRoot();
        string watchedRoot = Path.Combine(tempRoot, "Watched");
        string orderFilePath = Path.Combine(watchedRoot, "OrderRepository.cs");
        string customerFilePath = Path.Combine(watchedRoot, "CustomerRepository.cs");
        Directory.CreateDirectory(watchedRoot);
        File.WriteAllText(orderFilePath, "namespace Example.Data { public sealed class OrderRepository { public void GetByIdAsync() { } } }");
        File.WriteAllText(customerFilePath, "namespace Example.Data { public sealed class CustomerRepository { public void GetByIdAsync() { } } }");
        MonitorSettings settings = MonitorSettings.Create(
            tempRoot,
            Path.Combine(watchedRoot, "Example.sln"),
            Path.Combine(tempRoot, "runtime"));
        SolutionIndexStore store = new(new SolutionIndexDatabase(MonitorDataPaths.GetDefaultIndexDatabasePath(settings)));
        store.SaveSnapshot(new MSBuildSolutionSnapshot(
            settings.WatchedSolutionPath,
            [
                new MSBuildProjectSnapshot(
                    "project:example",
                    "Example",
                    Path.Combine(watchedRoot, "Example.csproj"),
                    "C#",
                    "net10.0",
                    "",
                    "Library",
                    "Microsoft.NET.Sdk",
                    "Example",
                    "Example.Data",
                    "enable",
                    "enable",
                    "latest",
                    [
                        new MSBuildDocumentSnapshot("document:order", "OrderRepository.cs", orderFilePath, [], ComputeFileHash(orderFilePath)),
                        new MSBuildDocumentSnapshot("document:customer", "CustomerRepository.cs", customerFilePath, [], ComputeFileHash(customerFilePath))
                    ],
                    [
                        new MSBuildSymbolSnapshot("symbol:order-type", "OrderRepository", "NamedType", "Example.Data", "", orderFilePath, 1, 1, "Example.Data.OrderRepository"),
                        new MSBuildSymbolSnapshot("symbol:order-get", "GetByIdAsync", "Method", "Example.Data", "OrderRepository", orderFilePath, 1, 1, "Example.Data.OrderRepository.GetByIdAsync()"),
                        new MSBuildSymbolSnapshot("symbol:customer-type", "CustomerRepository", "NamedType", "Example.Data", "", customerFilePath, 1, 1, "Example.Data.CustomerRepository"),
                        new MSBuildSymbolSnapshot("symbol:customer-get", "GetByIdAsync", "Method", "Example.Data", "CustomerRepository", customerFilePath, 1, 1, "Example.Data.CustomerRepository.GetByIdAsync()")
                    ],
                    [],
                    [],
                    [],
                    [],
                    [],
                    [],
                    [])
            ],
            []));
        SolutionIndexQueryService service = SolutionIndexQueryService.Create(settings);

        IndexedSymbolSearchResult broadResult = service.FindSymbols("GetByIdAsync", kind: "Method");
        IndexedSymbolSearchResult qualifiedResult = service.FindSymbols("OrderRepository.GetByIdAsync", kind: "Method");
        IndexedSymbolSearchResult explicitContainingTypeResult = service.FindSymbols("GetByIdAsync", kind: "Method", containingType: "Example.Data.CustomerRepository");

        Assert.Equal(2, broadResult.TotalSymbolCount);
        IndexedSymbolQueryItem qualifiedItem = Assert.Single(qualifiedResult.Symbols);
        Assert.Equal("symbol:order-get", qualifiedItem.Symbol.StableKey);
        IndexedSymbolQueryItem explicitItem = Assert.Single(explicitContainingTypeResult.Symbols);
        Assert.Equal("symbol:customer-get", explicitItem.Symbol.StableKey);
    }

    [Fact]
    public void FindSymbols_resolves_qualified_constructor_without_homonym_fanout()
    {
        string tempRoot = CreateTempRoot();
        string watchedRoot = Path.Combine(tempRoot, "Watched");
        string orderFilePath = Path.Combine(watchedRoot, "OrderRepository.cs");
        string customerFilePath = Path.Combine(watchedRoot, "CustomerRepository.cs");
        Directory.CreateDirectory(watchedRoot);
        File.WriteAllText(orderFilePath, "namespace Example.Data { public sealed class OrderRepository { public OrderRepository() { } } }");
        File.WriteAllText(customerFilePath, "namespace Example.Data { public sealed class CustomerRepository { public CustomerRepository() { } } }");
        MonitorSettings settings = MonitorSettings.Create(
            tempRoot,
            Path.Combine(watchedRoot, "Example.sln"),
            Path.Combine(tempRoot, "runtime"));
        SolutionIndexStore store = new(new SolutionIndexDatabase(MonitorDataPaths.GetDefaultIndexDatabasePath(settings)));
        store.SaveSnapshot(new MSBuildSolutionSnapshot(
            settings.WatchedSolutionPath,
            [
                new MSBuildProjectSnapshot(
                    "project:example",
                    "Example",
                    Path.Combine(watchedRoot, "Example.csproj"),
                    "C#",
                    "net10.0",
                    "",
                    "Library",
                    "Microsoft.NET.Sdk",
                    "Example",
                    "Example.Data",
                    "enable",
                    "enable",
                    "latest",
                    [
                        new MSBuildDocumentSnapshot("document:order", "OrderRepository.cs", orderFilePath, [], ComputeFileHash(orderFilePath)),
                        new MSBuildDocumentSnapshot("document:customer", "CustomerRepository.cs", customerFilePath, [], ComputeFileHash(customerFilePath))
                    ],
                    [
                        new MSBuildSymbolSnapshot("symbol:order-type", "OrderRepository", "NamedType", "Example.Data", "", orderFilePath, 1, 1, "Example.Data.OrderRepository"),
                        new MSBuildSymbolSnapshot("symbol:order-ctor", ".ctor", "Method", "Example.Data", "OrderRepository", orderFilePath, 1, 1, "Example.Data.OrderRepository.OrderRepository()"),
                        new MSBuildSymbolSnapshot("symbol:customer-type", "CustomerRepository", "NamedType", "Example.Data", "", customerFilePath, 1, 1, "Example.Data.CustomerRepository"),
                        new MSBuildSymbolSnapshot("symbol:customer-ctor", ".ctor", "Method", "Example.Data", "CustomerRepository", customerFilePath, 1, 1, "Example.Data.CustomerRepository.CustomerRepository()")
                    ],
                    [],
                    [],
                    [],
                    [],
                    [],
                    [],
                    [])
            ],
            []));
        SolutionIndexQueryService service = SolutionIndexQueryService.Create(settings);

        // Bare ".ctor" matches every constructor (the homonym fan-out the benchmark penalized).
        IndexedSymbolSearchResult bareResult = service.FindSymbols(".ctor", kind: "Method");
        // Qualified "ContainingType + '.' + Name" composes to a doubled dot for constructors; it must resolve to the one.
        IndexedSymbolSearchResult qualifiedResult = service.FindSymbols("OrderRepository..ctor", kind: "Method");

        Assert.Equal(2, bareResult.TotalSymbolCount);
        IndexedSymbolQueryItem qualifiedItem = Assert.Single(qualifiedResult.Symbols);
        Assert.Equal("symbol:order-ctor", qualifiedItem.Symbol.StableKey);
    }

    [Fact]
    public void QueryIndex_uses_path_aware_folder_scope_and_clamps_limits()
    {
        string tempRoot = CreateTempRoot();
        string watchedRoot = Path.Combine(tempRoot, "Watched");
        string targetFilePath = Path.Combine(watchedRoot, "Features", "Orders", "OrderView.cs");
        string siblingFilePath = Path.Combine(watchedRoot, "Features", "OrdersExtra", "OtherView.cs");
        Directory.CreateDirectory(Path.GetDirectoryName(targetFilePath)!);
        Directory.CreateDirectory(Path.GetDirectoryName(siblingFilePath)!);
        File.WriteAllText(targetFilePath, "namespace Example.Features.Orders { internal sealed class OrderView { } }");
        File.WriteAllText(siblingFilePath, "namespace Example.Features.OrdersExtra { internal sealed class OtherView { } }");
        MonitorSettings settings = MonitorSettings.Create(
            tempRoot,
            Path.Combine(watchedRoot, "Example.sln"),
            Path.Combine(tempRoot, "runtime"));
        SolutionIndexStore store = new(new SolutionIndexDatabase(MonitorDataPaths.GetDefaultIndexDatabasePath(settings)));
        store.SaveSnapshot(new MSBuildSolutionSnapshot(
            settings.WatchedSolutionPath,
            [
                CreateProjectSnapshot(settings.WatchedSolutionPath, targetFilePath, "symbol:order-view", "OrderView", "Example.Features.Orders", "document:order"),
                CreateProjectSnapshot(settings.WatchedSolutionPath, siblingFilePath, "symbol:other-view", "OtherView", "Example.Features.OrdersExtra", "document:other")
            ],
            []));
        SolutionIndexQueryService service = SolutionIndexQueryService.Create(settings);

        SolutionIndexQueryResult result = service.QueryIndex("folder", Path.Combine("Features", "Orders"), maxFiles: 1000000, maxSymbols: -1);

        Assert.Single(result.Files);
        Assert.Equal(targetFilePath, result.Files[0].FilePath);
        Assert.Equal(1, result.TotalFileCount);
        Assert.Equal(1, result.TotalSymbolCount);
        Assert.Equal(5000, result.MaxFiles);
        Assert.Equal(0, result.MaxSymbols);
        Assert.True(result.LimitsClamped);
        Assert.Empty(result.Symbols);
    }

    private static MSBuildSolutionSnapshot CreateSnapshot(string solutionPath, string filePath)
    {
        return new MSBuildSolutionSnapshot(
            solutionPath,
            [
                new MSBuildProjectSnapshot(
                    "project:example",
                    "Example",
                    Path.Combine(Path.GetDirectoryName(solutionPath)!, "Example.csproj"),
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
                        new MSBuildDocumentSnapshot("document:program", "Program.cs", filePath, [], ComputeFileHash(filePath))
                    ],
                    [
                        new MSBuildSymbolSnapshot(
                            "symbol:program",
                            "Program",
                            "NamedType",
                            "Example",
                            "",
                            filePath,
                            3,
                            10,
                            "Example.Program"),
                        new MSBuildSymbolSnapshot(
                            "symbol:caller",
                            "Caller",
                            "Method",
                            "Example",
                            "Program",
                            filePath,
                            7,
                            10,
                            "Example.Program.Caller()"),
                        new MSBuildSymbolSnapshot(
                            "symbol:target",
                            "Target",
                            "Method",
                            "Example",
                            "Program",
                            filePath,
                            5,
                            5,
                            "Example.Program.Target()")
                    ],
                    [
                        new MSBuildReferenceSnapshot(
                            "symbol:program",
                            filePath,
                            3,
                            27,
                            "IdentifierName",
                            "Program"),
                        new MSBuildReferenceSnapshot(
                            "symbol:target",
                            filePath,
                            9,
                            25,
                            "InvocationExpression",
                            "Target()"),
                        new MSBuildReferenceSnapshot(
                            "symbol:program",
                            filePath,
                            3,
                            5,
                            "partial_declaration",
                            "Program")
                    ],
                    [],
                    [],
                    [],
                    [],
                    [],
                    [])
            ],
            []);
    }

    private static MSBuildProjectSnapshot CreateProjectSnapshot(
        string solutionPath,
        string filePath,
        string symbolKey,
        string symbolName,
        string namespaceName,
        string documentKey)
    {
        return new MSBuildProjectSnapshot(
            "project:" + symbolName,
            symbolName,
            Path.Combine(Path.GetDirectoryName(solutionPath)!, symbolName + ".csproj"),
            "C#",
            "net10.0",
            "",
            "Exe",
            "Microsoft.NET.Sdk",
            symbolName,
            namespaceName,
            "enable",
            "enable",
            "latest",
            [new MSBuildDocumentSnapshot(documentKey, Path.GetFileName(filePath), filePath, [], ComputeFileHash(filePath))],
            [new MSBuildSymbolSnapshot(symbolKey, symbolName, "NamedType", namespaceName, "", filePath, 1, 1, namespaceName + "." + symbolName)],
            [new MSBuildReferenceSnapshot(symbolKey, filePath, 1, 1, "IdentifierName", symbolName)],
            [],
            [],
            [],
            [],
            [],
            []);
    }

    private static string CreateTempRoot()
    {
        return Path.Combine(Path.GetTempPath(), "AIMonitorDataTests", Guid.NewGuid().ToString("N"));
    }

    private static string ComputeFileHash(string filePath)
    {
        using FileStream stream = File.OpenRead(filePath);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }
}
