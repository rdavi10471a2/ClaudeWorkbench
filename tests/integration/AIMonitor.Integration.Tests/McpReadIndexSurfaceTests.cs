using AIMonitor.Data;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace AIMonitor.Integration.Tests;

// AREA A of the MCP-surface suite: the read/index tools, exercised in-process against a real out-of-process
// AIMonitor.McpServer over a seeded hermetic index. These tools are read-only and do not require a planned
// session. Index contents are independently confirmed with SolutionIndexProbe (the product-grade SQLite probe)
// so the assertions are anchored to ground truth, not just the tool's own self-report.
public sealed class McpReadIndexSurfaceTests
{
    static McpReadIndexSurfaceTests()
    {
        Environment.SetEnvironmentVariable("AIMONITOR_DISABLE_VALIDATION_DIALOG", "1");
    }

    [Fact]
    [Trait("Suite", "McpSurface")]
    public async Task Get_monitor_status_reports_seeded_index_counts()
    {
        McpSurfaceFixture fixture = McpSurfaceFixture.CreateSingleProject();
        await using McpClient client = await McpSurfaceClient.ConnectAsync(fixture);

        // Ground truth from the probe first: one project, three symbols, two reference rows seeded.
        SolutionIndexCounts counts = fixture.CreateProbe().GetCounts();
        Assert.Equal(1, counts.Projects);
        Assert.Equal(3, counts.Symbols);
        Assert.Equal(2, counts.References);

        CallToolResult status = await client.CallToolAsync("get_monitor_status");
        Assert.False(status.IsError == true, McpSurfaceClient.Text(status));
        string statusJson = McpSurfaceClient.Text(status);
        Assert.Equal(1, McpSurfaceClient.JsonInt(statusJson, "projectCount"));
        Assert.Equal(3, McpSurfaceClient.JsonInt(statusJson, "symbolCount"));
        Assert.Equal(2, McpSurfaceClient.JsonInt(statusJson, "referenceCount"));
        Assert.Equal(0, McpSurfaceClient.JsonInt(statusJson, "staleFileCount"));
    }

    [Fact]
    [Trait("Suite", "McpSurface")]
    public async Task Query_solution_index_scopes_and_clamps_limits()
    {
        McpSurfaceFixture fixture = McpSurfaceFixture.CreateSingleProject();
        await using McpClient client = await McpSurfaceClient.ConnectAsync(fixture);

        CallToolResult solution = await client.CallToolAsync(
            "query_solution_index",
            new Dictionary<string, object?>
            {
                ["scope"] = "solution",
                ["maxFiles"] = 1000000,
                ["maxSymbols"] = 1000000
            });
        Assert.False(solution.IsError == true, McpSurfaceClient.Text(solution));
        string solutionJson = McpSurfaceClient.Text(solution);
        Assert.Equal(1, McpSurfaceClient.JsonInt(solutionJson, "totalFileCount"));
        Assert.Equal(3, McpSurfaceClient.JsonInt(solutionJson, "totalSymbolCount"));
        // Over-budget requests are clamped to the server-enforced ceilings.
        Assert.Equal(5000, McpSurfaceClient.JsonInt(solutionJson, "maxFiles"));
        Assert.Equal(50000, McpSurfaceClient.JsonInt(solutionJson, "maxSymbols"));
        Assert.True(McpSurfaceClient.JsonBool(solutionJson, "limitsClamped"));

        CallToolResult file = await client.CallToolAsync(
            "query_solution_index",
            new Dictionary<string, object?>
            {
                ["scope"] = "file",
                ["value"] = fixture.ProgramFilePath
            });
        Assert.False(file.IsError == true, McpSurfaceClient.Text(file));
        string fileJson = McpSurfaceClient.Text(file);
        Assert.Equal(1, McpSurfaceClient.JsonInt(fileJson, "totalFileCount"));
        Assert.Contains("Program.cs", fileJson, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Suite", "McpSurface")]
    public async Task Get_solution_index_tree_and_status_expose_projects_and_namespaces()
    {
        McpSurfaceFixture fixture = McpSurfaceFixture.CreateSingleProject();
        await using McpClient client = await McpSurfaceClient.ConnectAsync(fixture);

        CallToolResult tree = await client.CallToolAsync("get_solution_index_tree");
        Assert.False(tree.IsError == true, McpSurfaceClient.Text(tree));
        string treeJson = McpSurfaceClient.Text(tree);
        Assert.Contains("Example", treeJson, StringComparison.Ordinal);
        Assert.Contains("Program.cs", treeJson, StringComparison.Ordinal);
        // Namespaces section groups the seeded Example namespace.
        Assert.Contains("\"namespaces\"", treeJson, StringComparison.OrdinalIgnoreCase);

        CallToolResult status = await client.CallToolAsync("get_solution_index_status");
        Assert.False(status.IsError == true, McpSurfaceClient.Text(status));
        string statusJson = McpSurfaceClient.Text(status);
        Assert.Equal(1, McpSurfaceClient.JsonInt(statusJson, "projectCount"));
        Assert.Equal(3, McpSurfaceClient.JsonInt(statusJson, "symbolCount"));
    }

    [Fact]
    [Trait("Suite", "McpSurface")]
    public async Task Find_indexed_symbols_supports_name_and_qualified_type_member()
    {
        McpSurfaceFixture fixture = McpSurfaceFixture.CreateSingleProject();
        await using McpClient client = await McpSurfaceClient.ConnectAsync(fixture);

        // Plain name search returns the homonym fanout (both the Target method here).
        CallToolResult byName = await client.CallToolAsync(
            "find_indexed_symbols",
            new Dictionary<string, object?>
            {
                ["text"] = "Target"
            });
        Assert.False(byName.IsError == true, McpSurfaceClient.Text(byName));
        string byNameJson = McpSurfaceClient.Text(byName);
        Assert.Contains("symbol:target", byNameJson, StringComparison.Ordinal);
        Assert.Equal(1, McpSurfaceClient.JsonInt(byNameJson, "totalSymbolCount"));

        // Qualified Type.Member text is treated as a containing-type member lookup (exact name match).
        CallToolResult qualified = await client.CallToolAsync(
            "find_indexed_symbols",
            new Dictionary<string, object?>
            {
                ["text"] = "Program.Target"
            });
        Assert.False(qualified.IsError == true, McpSurfaceClient.Text(qualified));
        string qualifiedJson = McpSurfaceClient.Text(qualified);
        Assert.Contains("symbol:target", qualifiedJson, StringComparison.Ordinal);
        Assert.DoesNotContain("symbol:caller", qualifiedJson, StringComparison.Ordinal);
        Assert.Equal(1, McpSurfaceClient.JsonInt(qualifiedJson, "totalSymbolCount"));

        // A kind filter narrows the named type away from member homonyms.
        CallToolResult typeOnly = await client.CallToolAsync(
            "find_indexed_symbols",
            new Dictionary<string, object?>
            {
                ["text"] = "Program",
                ["kind"] = "NamedType"
            });
        Assert.False(typeOnly.IsError == true, McpSurfaceClient.Text(typeOnly));
        Assert.Contains("symbol:program", McpSurfaceClient.Text(typeOnly), StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Suite", "McpSurface")]
    public async Task Get_indexed_symbol_returns_one_row_by_stable_key()
    {
        McpSurfaceFixture fixture = McpSurfaceFixture.CreateSingleProject();
        await using McpClient client = await McpSurfaceClient.ConnectAsync(fixture);

        CallToolResult symbol = await client.CallToolAsync(
            "get_indexed_symbol",
            new Dictionary<string, object?>
            {
                ["stableSymbolKey"] = "symbol:target"
            });
        Assert.False(symbol.IsError == true, McpSurfaceClient.Text(symbol));
        string symbolJson = McpSurfaceClient.Text(symbol);
        Assert.Contains("symbol:target", symbolJson, StringComparison.Ordinal);
        Assert.Contains("Target", symbolJson, StringComparison.Ordinal);
        Assert.Contains("Example.Program.Target()", symbolJson, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Suite", "McpSurface")]
    public async Task Find_indexed_references_default_shape_is_lean_and_rich_opts_into_full_rows()
    {
        McpSurfaceFixture fixture = McpSurfaceFixture.CreateSingleProject();
        await using McpClient client = await McpSurfaceClient.ConnectAsync(fixture);

        // Probe confirms the seeded reference kinds are present before exercising the tool.
        SolutionIndexProbe probe = fixture.CreateProbe();
        Assert.True(probe.GetReferenceKindCounts().Any(kind => kind.Kind == "InvocationExpression"));

        CallToolResult lean = await client.CallToolAsync(
            "find_indexed_references",
            new Dictionary<string, object?>
            {
                ["stableSymbolKey"] = "symbol:target"
            });
        Assert.False(lean.IsError == true, McpSurfaceClient.Text(lean));
        string leanJson = McpSurfaceClient.Text(lean);
        Assert.Contains("\"targetName\":\"Target\"", leanJson, StringComparison.Ordinal);
        Assert.Contains("\"callerName\":\"Caller\"", leanJson, StringComparison.Ordinal);
        // Lean is the default and omits the token-heavy file content hash.
        Assert.DoesNotContain("\"fileContentHash\"", leanJson, StringComparison.Ordinal);

        CallToolResult rich = await client.CallToolAsync(
            "find_indexed_references",
            new Dictionary<string, object?>
            {
                ["stableSymbolKey"] = "symbol:target",
                ["responseShape"] = "rich"
            });
        Assert.False(rich.IsError == true, McpSurfaceClient.Text(rich));
        string richJson = McpSurfaceClient.Text(rich);
        Assert.Contains("\"targetName\":\"Target\"", richJson, StringComparison.Ordinal);
        Assert.Contains("\"fileContentHash\"", richJson, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Suite", "McpSurface")]
    public async Task Find_indexed_callers_and_relationships_return_seeded_rows()
    {
        McpSurfaceFixture fixture = McpSurfaceFixture.CreateSingleProject();
        await using McpClient client = await McpSurfaceClient.ConnectAsync(fixture);

        CallToolResult callers = await client.CallToolAsync(
            "find_indexed_callers",
            new Dictionary<string, object?>
            {
                ["stableSymbolKey"] = "symbol:target"
            });
        Assert.False(callers.IsError == true, McpSurfaceClient.Text(callers));
        string callersJson = McpSurfaceClient.Text(callers);
        Assert.Contains("\"callerStableKey\":\"symbol:caller\"", callersJson, StringComparison.Ordinal);
        Assert.Contains("\"callerName\":\"Caller\"", callersJson, StringComparison.Ordinal);
        Assert.Contains("\"callKind\":\"InvocationExpression\"", callersJson, StringComparison.Ordinal);

        CallToolResult relationships = await client.CallToolAsync(
            "find_indexed_relationships",
            new Dictionary<string, object?>
            {
                ["stableSymbolKey"] = "symbol:program"
            });
        Assert.False(relationships.IsError == true, McpSurfaceClient.Text(relationships));
        string relationshipsJson = McpSurfaceClient.Text(relationships);
        Assert.Contains("\"relationshipKind\":\"partial_declaration\"", relationshipsJson, StringComparison.Ordinal);
        Assert.Contains("\"sourceStableKey\":\"symbol:program\"", relationshipsJson, StringComparison.Ordinal);
        Assert.Contains("\"targetStableKey\":\"symbol:program\"", relationshipsJson, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Suite", "McpSurface")]
    public async Task Index_reference_tools_reject_source_map_selector_keys()
    {
        McpSurfaceFixture fixture = McpSurfaceFixture.CreateSingleProject();
        await using McpClient client = await McpSurfaceClient.ConnectAsync(fixture);
        string selectorKey = "Program.cs::Example::Program::method::Target()";

        CallToolResult references = await client.CallToolAsync(
            "find_indexed_references",
            new Dictionary<string, object?>
            {
                ["stableSymbolKey"] = selectorKey
            });
        Assert.False(references.IsError == true, McpSurfaceClient.Text(references));
        Assert.True(McpSurfaceClient.JsonBool(McpSurfaceClient.Text(references), "isError"));

        CallToolResult callers = await client.CallToolAsync(
            "find_indexed_callers",
            new Dictionary<string, object?>
            {
                ["stableSymbolKey"] = selectorKey
            });
        Assert.False(callers.IsError == true, McpSurfaceClient.Text(callers));
        Assert.True(McpSurfaceClient.JsonBool(McpSurfaceClient.Text(callers), "isError"));
    }

    // find_references_in_file is the INVERSE of find_indexed_references: file-keyed rather than
    // symbol-keyed. Both tools below existed only behind the retired AIMonitor.Cli — the engine
    // methods were there, with nothing on the live surface able to reach them.
    [Fact]
    [Trait("Suite", "McpSurface")]
    public async Task Find_references_in_file_returns_every_reference_inside_one_file()
    {
        McpSurfaceFixture fixture = McpSurfaceFixture.CreateSingleProject();
        await using McpClient client = await McpSurfaceClient.ConnectAsync(fixture);

        // Watched-relative path: the tool resolves against the watched folder, not the cwd.
        CallToolResult lean = await client.CallToolAsync(
            "find_references_in_file",
            new Dictionary<string, object?>
            {
                ["path"] = "Program.cs"
            });
        Assert.False(lean.IsError == true, McpSurfaceClient.Text(lean));
        string leanJson = McpSurfaceClient.Text(lean);

        // Both seeded references live in Program.cs and must come back — the symbol-keyed tool
        // could only return one of these per call.
        Assert.Contains("\"targetStableKey\":\"symbol:target\"", leanJson, StringComparison.Ordinal);
        Assert.Contains("\"targetStableKey\":\"symbol:program\"", leanJson, StringComparison.Ordinal);
        Assert.Contains("InvocationExpression", leanJson, StringComparison.Ordinal);
        Assert.Contains("partial_declaration", leanJson, StringComparison.Ordinal);

        // Same lean/rich contract as the symbol-keyed tool: lean drops fileContentHash.
        Assert.DoesNotContain("fileContentHash", leanJson, StringComparison.OrdinalIgnoreCase);

        CallToolResult rich = await client.CallToolAsync(
            "find_references_in_file",
            new Dictionary<string, object?>
            {
                ["path"] = "Program.cs",
                ["responseShape"] = "rich"
            });
        Assert.False(rich.IsError == true, McpSurfaceClient.Text(rich));
        Assert.Contains("fileContentHash", McpSurfaceClient.Text(rich), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    [Trait("Suite", "McpSurface")]
    public async Task List_package_references_returns_the_indexed_nuget_rows()
    {
        McpSurfaceFixture fixture = McpSurfaceFixture.CreateSingleProject();
        await using McpClient client = await McpSurfaceClient.ConnectAsync(fixture);

        CallToolResult packages = await client.CallToolAsync(
            "list_package_references",
            new Dictionary<string, object?>());
        Assert.False(packages.IsError == true, McpSurfaceClient.Text(packages));

        string json = McpSurfaceClient.Text(packages);
        Assert.Contains("Microsoft.Data.Sqlite", json, StringComparison.Ordinal);
        Assert.Contains("10.0.0", json, StringComparison.Ordinal);
        // The declaring project travels with the row — a package alone is not actionable.
        Assert.Contains("Example.csproj", json, StringComparison.OrdinalIgnoreCase);
    }
}
