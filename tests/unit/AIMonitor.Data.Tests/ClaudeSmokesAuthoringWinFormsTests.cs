using AIMonitor.MSBuild;

namespace AIMonitor.Data.Tests;

// ClaudeSmokes — AUTHORING-over-WinForms ground-truth. Authored by Claude (review+test role; no src/ or samples/
// edits). LOCAL. Materializes a throwaway copy of samples/watched-solutions/WinFormsSample (CopyTree skipping
// bin/obj), writes a NEW Order repository + service + form into the COPY, indexes the COPY, and proves the index
// picks up the whole new graph WITHOUT dropping the existing Customer graph. The checked-in sample is never mutated.
//
// Mean-teacher: assertions are by EXACT StableKey identity (not "contains a row"), and the test bundles a
// REGRESSION check (Customer graph survives augmentation), a NEGATIVE check (a never-authored ProductRepository is
// absent), and an ISOLATION check (the Order caller resolves to the Order override, NOT the same-signature Customer
// override). It fails on any extraction regression: faked callers, confused same-signature overrides, dropped
// relationships, or call-site over-capture.
//
// Baked-in extraction gotchas (confirmed by grepping src/AIMonitor.MSBuild/MSBuildWorkspaceLoader.cs):
//   * No 'overrides' row is emitted for an override of a GENERIC base virtual (RepositoryBase<T>.GetByIdAsync); only
//     inherits_from + implements_interface_member appear. So 'overrides' is NOT asserted for the repository override.
//   * Both the base type AND the implemented interface in a base list are emitted as 'inherits_from' (the extractor
//     returns "inherits_from" for any named-type reference under a BaseListSyntax).
//   * ObjectCreationExpression call sites need an EXPLICIT constructor and a 'new' in a METHOD BODY — a field
//     initializer 'new' is not captured. Hence the asserted 'new OrderService()' lives in OnLoadClicked's body and
//     every new entity declares an explicit ctor.
//   * ContainingType is fully-qualified (SymbolDisplayFormat.CSharpErrorMessageFormat), e.g.
//     "WinFormsSample.Repositories.OrderRepository".
public sealed class ClaudeSmokesAuthoringWinFormsTests
{
    [Fact]
    [Trait("Suite", "ClaudeSmokes")]
    public async Task ClaudeSmokes_authoring_order_graph_over_winforms_copy_indexes_new_graph_without_dropping_customer()
    {
        string sampleRoot = Path.Combine(FindRepositoryRoot(), "samples", "watched-solutions", "WinFormsSample");
        Assert.True(Directory.Exists(sampleRoot), $"In-repo WinForms sample missing: {sampleRoot}");

        // Materialize a throwaway copy; the checked-in sample stays byte-identical (repeatable, never dirtied).
        string tempRoot = Path.Combine(
            Path.GetTempPath(), "AIMonitorClaudeSmokesAuthoringWinForms", Guid.NewGuid().ToString("N"));
        string workCopyRoot = Path.Combine(tempRoot, "WinFormsSample");
        CopyTree(sampleRoot, workCopyRoot);

        // Write the NEW Order graph into the COPY ONLY (mirroring existing repository conventions).
        WriteFile(Path.Combine(workCopyRoot, "Model", "Order.cs"), """
            namespace WinFormsSample.Model;

            public sealed class Order
            {
                public Order()
                {
                }

                public int Id { get; set; }

                public string Name { get; set; } = "";
            }
            """);

        WriteFile(Path.Combine(workCopyRoot, "Repositories", "IOrderRepository.cs"), """
            using WinFormsSample.Model;

            namespace WinFormsSample.Repositories;

            public interface IOrderRepository
            {
                Task<Order?> GetByIdAsync(int id);
            }
            """);

        WriteFile(Path.Combine(workCopyRoot, "Repositories", "OrderRepository.cs"), """
            using WinFormsSample.Model;

            namespace WinFormsSample.Repositories;

            public sealed class OrderRepository : RepositoryBase<Order>, IOrderRepository
            {
                public override Task<Order?> GetByIdAsync(int id)
                {
                    Log("get-order");
                    return base.GetByIdAsync(id);
                }
            }
            """);

        WriteFile(Path.Combine(workCopyRoot, "Repositories", "OrderService.cs"), """
            using WinFormsSample.Model;

            namespace WinFormsSample.Repositories;

            public sealed class OrderService
            {
                private readonly OrderRepository repository = new OrderRepository();

                public OrderService()
                {
                }

                public Task<Order?> LoadAsync(int id)
                {
                    return repository.GetByIdAsync(id);
                }

                public Order CreateDefault()
                {
                    Order order = new Order();
                    return order;
                }
            }
            """);

        WriteFile(Path.Combine(workCopyRoot, "Forms", "OrderForm.cs"), """
            using WinFormsSample.Model;
            using WinFormsSample.Repositories;

            namespace WinFormsSample.Forms;

            public sealed class OrderForm
            {
                public Task<Order?> OnLoadClicked()
                {
                    OrderService service = new OrderService();
                    return service.LoadAsync(7);
                }
            }
            """);

        // Index the COPY.
        string copyProject = Path.Combine(workCopyRoot, "WinFormsSample.csproj");
        MSBuildSolutionSnapshot snapshot = await new MSBuildWorkspaceLoader().OpenProjectAsync(copyProject);
        string databasePath = Path.Combine(
            Path.GetTempPath(), "AIMonitorClaudeSmokesAuthoringWinForms", Guid.NewGuid().ToString("N"), "index.sqlite");
        Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
        SolutionIndexStore store = new(new SolutionIndexDatabase(databasePath));
        store.SaveSnapshot(snapshot);

        System.Collections.Generic.IReadOnlyList<IndexedSymbolRow> symbols = store.ListSymbols();
        System.Collections.Generic.IReadOnlyList<IndexedRelationshipRow> relationships = store.ListRelationships();
        System.Collections.Generic.IReadOnlyList<IndexedCallSiteRow> callSites = store.ListCallSites();

        const string orderRepoType = "WinFormsSample.Repositories.OrderRepository";
        const string orderServiceType = "WinFormsSample.Repositories.OrderService";
        const string orderFormType = "WinFormsSample.Forms.OrderForm";
        const string customerRepoType = "WinFormsSample.Repositories.CustomerRepository";
        const string customerServiceType = "WinFormsSample.Repositories.CustomerService";

        // --- NEW SYMBOLS by exact (Name, Kind, fully-qualified ContainingType). ---
        Assert.Contains(symbols, s => s.Name == "OrderRepository" && s.Kind == "NamedType");
        string orderRepoGetById = Assert.Single(
            symbols, s => s.Name == "GetByIdAsync" && s.ContainingType == orderRepoType).StableKey;
        string orderLoadAsync = Assert.Single(
            symbols, s => s.Name == "LoadAsync" && s.ContainingType == orderServiceType).StableKey;
        string orderOnLoadClicked = Assert.Single(
            symbols, s => s.Name == "OnLoadClicked" && s.ContainingType == orderFormType).StableKey;

        // --- NEW RELATIONSHIPS: both base type AND implemented interface emit 'inherits_from'; the interface member
        // is also tracked. No 'overrides' is asserted (generic-base override gap, per the gotcha above). ---
        Assert.Contains(relationships, r =>
            r.RelationshipKind == "inherits_from" && r.SourceName == "OrderRepository" && r.TargetName == "RepositoryBase");
        Assert.Contains(relationships, r =>
            r.RelationshipKind == "inherits_from" && r.SourceName == "OrderRepository" && r.TargetName == "IOrderRepository");
        Assert.Contains(relationships, r =>
            r.RelationshipKind == "implements_interface_member" && r.SourceName == "GetByIdAsync" && r.SourceStableKey == orderRepoGetById);

        // --- CALLER IDENTITY: OrderService.LoadAsync's call to repository.GetByIdAsync resolves the REAL caller and
        // the CONCRETE Order override target (proves FindContainingSymbol + overload resolution, not a heuristic). ---
        IndexedCallSiteRow orderLoadCall = Assert.Single(
            callSites, c => c.CallKind == "InvocationExpression" && c.CallerStableKey == orderLoadAsync);
        Assert.Equal("LoadAsync", orderLoadCall.CallerName);
        Assert.Equal("Method", orderLoadCall.CallerKind);
        Assert.Equal(orderRepoGetById, orderLoadCall.TargetStableKey);

        // --- ISOLATION: two same-signature overrides must NOT be confused. The Order caller's target is the Order
        // override, never the Customer one. ---
        string customerRepoGetById = Assert.Single(
            symbols, s => s.Name == "GetByIdAsync" && s.ContainingType == customerRepoType).StableKey;
        Assert.NotEqual(customerRepoGetById, orderLoadCall.TargetStableKey);

        // --- OBJECT CREATION: 'new OrderService()' inside OnLoadClicked's body is captured (explicit ctor + method
        // body — the gotcha #2 conditions). The field-initializer 'new OrderRepository()' is intentionally NOT
        // asserted because field-initializer construction is not captured. ---
        IndexedCallSiteRow orderCreate = Assert.Single(
            callSites, c => c.CallKind == "ObjectCreationExpression" && c.CallerStableKey == orderOnLoadClicked);
        Assert.Equal("OnLoadClicked", orderCreate.CallerName);

        // --- REGRESSION: the existing Customer graph still indexes after augmentation. ---
        Assert.Contains(relationships, r =>
            r.RelationshipKind == "inherits_from" && r.SourceName == "CustomerRepository" && r.TargetName == "RepositoryBase");
        string customerLoadAsync = Assert.Single(
            symbols, s => s.Name == "LoadAsync" && s.ContainingType == customerServiceType).StableKey;
        IndexedCallSiteRow customerLoadCall = Assert.Single(
            callSites, c => c.CallKind == "InvocationExpression" && c.CallerStableKey == customerLoadAsync);
        Assert.Equal(customerRepoGetById, customerLoadCall.TargetStableKey);

        // --- NEGATIVE: a symbol that was never authored is absent. ---
        Assert.DoesNotContain(symbols, s => s.Name == "ProductRepository");
    }

    private static void WriteFile(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    private static void CopyTree(string source, string destination)
    {
        foreach (string dir in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
        {
            string name = Path.GetFileName(dir);
            if (name is "bin" or "obj")
            {
                continue;
            }

            Directory.CreateDirectory(dir.Replace(source, destination, StringComparison.Ordinal));
        }

        Directory.CreateDirectory(destination);
        foreach (string file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            if (file.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Any(part => part is "bin" or "obj"))
            {
                continue;
            }

            string target = file.Replace(source, destination, StringComparison.Ordinal);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: true);
        }
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "ClaudeWorkbench.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not find ClaudeWorkbench.slnx.");
    }
}
