using AIMonitor.Core;
using AIMonitor.Data;
using AIMonitor.MSBuild;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using System.Text.Json;

namespace AIMonitor.Integration.Tests;

// ClaudeSmokes — Phase 1 CMB-parity CI gate, authored by Claude (review+test role; no production edits).
//
// Standard smoke pattern (real AIMonitor.McpServer over stdio + McpClient, asserting the JSON an external Claude
// consumer sees), BUT seeded by REAL SolutionIndexBuilder.RebuildAsync extraction instead of a hand-built snapshot.
// The existing McpServerSmokeTests seed snapshots and bypass the extractor — these drive it, so they FAIL if Phase-1
// extraction regresses to a stub. Local-only CI gate; no production code is edited.
public sealed class ClaudeSmokesPhase1McpTests
{
    static ClaudeSmokesPhase1McpTests()
    {
        Environment.SetEnvironmentVariable("AIMONITOR_DISABLE_VALIDATION_DIALOG", "1");
    }

    [Fact]
    [Trait("Suite", "ClaudeSmokes")]
    public async Task ClaudeSmokes_mcp_index_tools_return_real_extracted_relationships_callers_and_references()
    {
        ClaudeSmokesFixture fixture = await BuildRealExtractedFixtureAsync();
        await using McpClient client = await CreateClientAsync(fixture);

        // External-consumer flow: discover the symbol, then query its callers/relationships/references.
        string runKey = FindStableKey(
            ExtractToolText(await client.CallToolAsync("find_indexed_symbols", new Dictionary<string, object?> { ["text"] = "Run" })),
            element => element.GetProperty("name").GetString() == "Run"
                && (element.GetProperty("containingType").GetString() ?? string.Empty).EndsWith("Derived", StringComparison.Ordinal));
        string qualifiedRunJson = ExtractToolText(await client.CallToolAsync(
            "find_indexed_symbols",
            new Dictionary<string, object?> { ["text"] = "Derived.Run", ["kind"] = "Method" }));
        Assert.Contains("\"name\":\"Run\"", qualifiedRunJson, StringComparison.Ordinal);
        Assert.Contains("Derived", qualifiedRunJson, StringComparison.Ordinal);
        Assert.DoesNotContain("Other", qualifiedRunJson, StringComparison.Ordinal);

        // find_indexed_callers — REAL caller identity (FindContainingSymbol), not ReferenceKind substring fakery.
        string callersJson = ExtractToolText(await client.CallToolAsync(
            "find_indexed_callers", new Dictionary<string, object?> { ["stableSymbolKey"] = runKey }));
        Assert.Contains("\"callKind\":\"InvocationExpression\"", callersJson, StringComparison.Ordinal);
        Assert.Contains("\"callerName\":\"CallSite\"", callersJson, StringComparison.Ordinal);

        // find_indexed_references: lean by default, with rich row evidence available on request.
        string referencesJson = ExtractToolText(await client.CallToolAsync(
            "find_indexed_references", new Dictionary<string, object?> { ["stableSymbolKey"] = runKey }));
        Assert.Contains("\"callerName\":\"CallSite\"", referencesJson, StringComparison.Ordinal);
        Assert.DoesNotContain("\"fileContentHash\"", referencesJson, StringComparison.Ordinal);
        Assert.DoesNotContain("\"projectPath\"", referencesJson, StringComparison.Ordinal);
        string richReferencesJson = ExtractToolText(await client.CallToolAsync(
            "find_indexed_references",
            new Dictionary<string, object?> { ["stableSymbolKey"] = runKey, ["responseShape"] = "rich" }));
        Assert.Contains("\"fileContentHash\"", richReferencesJson, StringComparison.Ordinal);
        Assert.Contains("\"projectPath\"", richReferencesJson, StringComparison.Ordinal);

        // find_indexed_relationships — real rows, not Array.Empty.
        string derivedKey = FindStableKey(
            ExtractToolText(await client.CallToolAsync("find_indexed_symbols", new Dictionary<string, object?> { ["text"] = "Derived", ["kind"] = "NamedType" })),
            element => element.GetProperty("name").GetString() == "Derived");
        string relationshipsJson = ExtractToolText(await client.CallToolAsync(
            "find_indexed_relationships", new Dictionary<string, object?> { ["stableSymbolKey"] = derivedKey }));
        Assert.Contains("\"relationshipKind\":\"inherits_from\"", relationshipsJson, StringComparison.Ordinal);

        // A .razor @code reference is indexed + mapped back to the .razor source (defensible Razor boundary).
        string greetKey = FindStableKey(
            ExtractToolText(await client.CallToolAsync("find_indexed_symbols", new Dictionary<string, object?> { ["text"] = "Greet" })),
            element => element.GetProperty("name").GetString() == "Greet");
        string razorRefsJson = ExtractToolText(await client.CallToolAsync(
            "find_indexed_references", new Dictionary<string, object?> { ["stableSymbolKey"] = greetKey }));
        Assert.Contains("Counter.razor", razorRefsJson, StringComparison.Ordinal);
        Assert.Contains("\"referenceKind\":\"razor", razorRefsJson, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Suite", "ClaudeSmokes")]
    public async Task ClaudeSmokes_mcp_file_outline_is_roslyn_for_cs_and_refuses_razor()
    {
        ClaudeSmokesFixture fixture = await BuildRealExtractedFixtureAsync();
        await using McpClient client = await CreateClientAsync(fixture);

        CallToolResult outline = await client.CallToolAsync(
            "get_file_outline", new Dictionary<string, object?> { ["path"] = fixture.DomainFilePath });
        Assert.False(outline.IsError == true);
        string outlineJson = ExtractToolText(outline);
        Assert.Contains("\"kind\":\"class\"", outlineJson, StringComparison.Ordinal);
        Assert.Contains("\"name\":\"Derived\"", outlineJson, StringComparison.Ordinal);
        Assert.Contains("\"kind\":\"method\"", outlineJson, StringComparison.Ordinal);

        // .razor must be a documented refusal, never fabricated markup symbols.
        CallToolResult razorOutline = await client.CallToolAsync(
            "get_file_outline", new Dictionary<string, object?> { ["path"] = fixture.CounterRazorPath });
        bool refused = razorOutline.IsError == true;
        if (!refused)
        {
            string razorText = ExtractToolText(razorOutline);
            refused = razorText.Contains(".cs", StringComparison.OrdinalIgnoreCase)
                || razorText.Contains("get_file_outline", StringComparison.OrdinalIgnoreCase)
                || razorText.Contains("Roslyn", StringComparison.OrdinalIgnoreCase);
        }
        Assert.True(refused, "get_file_outline should refuse .razor, not emit fabricated markup symbols.");
    }

    [Fact]
    [Trait("Suite", "ClaudeSmokes")]
    public async Task ClaudeSmokes_mcp_scoped_folder_query_uses_path_prefix_not_substring()
    {
        ClaudeSmokesFixture fixture = await BuildRealExtractedFixtureAsync();
        await using McpClient client = await CreateClientAsync(fixture);

        // Features/Orders vs sibling Features/OrdersExtra is the loose-Contains trap.
        string folderJson = ExtractToolText(await client.CallToolAsync(
            "query_solution_index",
            new Dictionary<string, object?> { ["scope"] = "folder", ["value"] = fixture.OrdersFolderPath }));
        Assert.Contains("Order.cs", folderJson, StringComparison.Ordinal);
        Assert.DoesNotContain("OrdersExtra", folderJson, StringComparison.Ordinal);
    }

    // ---- helpers ----

    private sealed record ClaudeSmokesFixture(
        string RepositoryRoot,
        string SettingsPath,
        string DomainFilePath,
        string CounterRazorPath,
        string OrdersFolderPath);

    private static async Task<ClaudeSmokesFixture> BuildRealExtractedFixtureAsync()
    {
        string repositoryRoot = FindRepositoryRoot();
        string tempRoot = Path.Combine(Path.GetTempPath(), "AIMonitorClaudeSmokesMcp", Guid.NewGuid().ToString("N"));
        string watchedRoot = Path.Combine(tempRoot, "Watched");
        string settingsPath = Path.Combine(tempRoot, "config", "appsettings.json");
        string runtimeRoot = Path.Combine(tempRoot, "runtime");
        string projectPath = Path.Combine(watchedRoot, "Watched.csproj");
        Directory.CreateDirectory(Path.Combine(watchedRoot, "Features", "Orders"));
        Directory.CreateDirectory(Path.Combine(watchedRoot, "Features", "OrdersExtra"));
        Directory.CreateDirectory(Path.GetDirectoryName(settingsPath)!);

        await File.WriteAllTextAsync(projectPath, """
            <Project Sdk="Microsoft.NET.Sdk.Razor">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
                <Nullable>enable</Nullable>
                <ImplicitUsings>enable</ImplicitUsings>
              </PropertyGroup>
              <ItemGroup><FrameworkReference Include="Microsoft.AspNetCore.App" /></ItemGroup>
            </Project>
            """);

        string domainFilePath = Path.Combine(watchedRoot, "Domain.cs");
        await File.WriteAllTextAsync(domainFilePath, """
            namespace ClaudeSmokesFixture;

            public interface IFoo { void Run(); }

            public class Base { public virtual void M() { } }

            public partial class Derived : Base, IFoo
            {
                public override void M() { }
                public void Run() { }
                public void CallSite()
                {
                    Run();
                    Widget widget = new Widget();
                }
            }

            public sealed class Widget { public Widget() { } }

            public class Greeter { public void Greet() { } }
            public class Other { public void Run() { } }
            """);
        await File.WriteAllTextAsync(Path.Combine(watchedRoot, "DomainPart.cs"), """
            namespace ClaudeSmokesFixture;
            public partial class Derived { public void Extra() { } }
            """);
        await File.WriteAllTextAsync(Path.Combine(watchedRoot, "_Imports.razor"), """
            @using Microsoft.AspNetCore.Components
            @using ClaudeSmokesFixture
            """);
        string counterRazorPath = Path.Combine(watchedRoot, "Counter.razor");
        await File.WriteAllTextAsync(counterRazorPath, """
            <h3>counter</h3>
            @code {
                private Greeter Model { get; } = new Greeter();
                private void Go() => Model.Greet();
            }
            """);
        await File.WriteAllTextAsync(Path.Combine(watchedRoot, "Features", "Orders", "Order.cs"), """
            namespace ClaudeSmokesFixture.Features.Orders;
            public sealed class Order { public int Id { get; set; } }
            """);
        await File.WriteAllTextAsync(Path.Combine(watchedRoot, "Features", "OrdersExtra", "ExtraThing.cs"), """
            namespace ClaudeSmokesFixture.Features.OrdersExtra;
            public sealed class ExtraThing { public int Id { get; set; } }
            """);

        MonitorSettingsLoader.SaveLocal(repositoryRoot, projectPath, runtimeRoot, settingsPath);
        MonitorSettings settings = MonitorSettingsLoader.Load(repositoryRoot, settingsPath);
        string indexDatabasePath = MonitorDataPaths.GetDefaultIndexDatabasePath(settings);
        SolutionIndexStore store = new(new SolutionIndexDatabase(indexDatabasePath));
        await new SolutionIndexBuilder(new MSBuildWorkspaceLoader(), store).RebuildAsync(settings);

        return new ClaudeSmokesFixture(
            repositoryRoot,
            settingsPath,
            domainFilePath,
            counterRazorPath,
            Path.Combine(watchedRoot, "Features", "Orders"));
    }

    private static string FindStableKey(string symbolsJson, Func<JsonElement, bool> predicate)
    {
        using JsonDocument document = JsonDocument.Parse(symbolsJson);
        JsonElement array = document.RootElement.ValueKind == JsonValueKind.Array
            ? document.RootElement
            : document.RootElement.EnumerateObject().First(p => p.Value.ValueKind == JsonValueKind.Array).Value;
        foreach (JsonElement element in array.EnumerateArray())
        {
            // find_indexed_symbols rows wrap the symbol fields under a "symbol" object.
            JsonElement symbol = element.TryGetProperty("symbol", out JsonElement inner) ? inner : element;
            if (predicate(symbol))
            {
                return symbol.GetProperty("stableKey").GetString() ?? string.Empty;
            }
        }

        throw new InvalidOperationException($"No indexed symbol matched in: {symbolsJson}");
    }

    private static async Task<McpClient> CreateClientAsync(ClaudeSmokesFixture fixture)
    {
        string serverDll = Path.Combine(
            fixture.RepositoryRoot, "src", "AIMonitor.McpServer", "bin", GetBuildConfiguration(), "net10.0", "AIMonitor.McpServer.dll");
        StdioClientTransportOptions options = new()
        {
            Name = "ai-monitor",
            Command = "dotnet",
            Arguments = [serverDll, "--repo-root", fixture.RepositoryRoot, "--config", fixture.SettingsPath],
            WorkingDirectory = fixture.RepositoryRoot
        };
        return await McpClient.CreateAsync(new StdioClientTransport(options));
    }

    private static string ExtractToolText(CallToolResult result)
    {
        string wrapperJson = JsonSerializer.Serialize(result);
        using JsonDocument document = JsonDocument.Parse(wrapperJson);
        foreach (JsonElement content in document.RootElement.GetProperty("content").EnumerateArray())
        {
            if (content.TryGetProperty("text", out JsonElement text) && text.ValueKind == JsonValueKind.String)
            {
                return text.GetString() ?? string.Empty;
            }
        }

        throw new InvalidOperationException("MCP tool result did not include text content.");
    }

    private static string GetBuildConfiguration()
    {
#if DEBUG
        return "Debug";
#else
        return "Release";
#endif
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
