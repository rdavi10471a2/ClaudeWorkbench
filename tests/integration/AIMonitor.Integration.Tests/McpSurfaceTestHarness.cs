using AIMonitor.Core;
using AIMonitor.Data;
using AIMonitor.MSBuild;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace AIMonitor.Integration.Tests;

// Shared in-process MCP-surface harness for the planned-session MCP test suite.
//
// It mirrors the fixture machinery proven out in McpServerSmokeTests (hermetic watched project + seeded
// SQLite index + a real out-of-process AIMonitor.McpServer driven over stdio) but exposes the pieces the
// new comprehensive suite needs: a seeded multi-symbol/cross-project index for the read/index assertions,
// SolutionIndexProbe access for ground-truth index checks, and planned-session helpers for the E2E and
// negative-path tests. Kept here (not duplicated per file) so the two suites stay DRY.
internal sealed class McpSurfaceFixture
{
    private McpSurfaceFixture(
        string repositoryRoot,
        string settingsPath,
        string watchedProjectPath,
        string programFilePath,
        string runtimeRoot,
        string indexDatabasePath)
    {
        RepositoryRoot = repositoryRoot;
        SettingsPath = settingsPath;
        WatchedProjectPath = watchedProjectPath;
        ProgramFilePath = programFilePath;
        RuntimeRoot = runtimeRoot;
        IndexDatabasePath = indexDatabasePath;
    }

    public string RepositoryRoot { get; }

    public string SettingsPath { get; }

    public string WatchedProjectPath { get; }

    public string ProgramFilePath { get; }

    public string RuntimeRoot { get; }

    public string IndexDatabasePath { get; }

    public string WatchedDirectory => Path.GetDirectoryName(ProgramFilePath)!;

    public SolutionIndexProbe CreateProbe()
    {
        return new SolutionIndexProbe(new SolutionIndexDatabase(IndexDatabasePath));
    }

    // A single-project hermetic fixture. The watched Program.cs is the only planned-editable file by default.
    // The seeded index contains a Program type, a Caller method, and a Target method, plus a Caller->Target
    // call site and an InvocationExpression reference, so the read/index tools have real rows to return.
    public static McpSurfaceFixture CreateSingleProject()
    {
        FixturePaths paths = PreparePaths();
        WriteProject(paths.WatchedProjectPath);
        File.WriteAllText(paths.ProgramFilePath, "namespace Example { internal static class Program { } }");

        SaveSettings(paths);
        SolutionIndexStore store = new(new SolutionIndexDatabase(paths.IndexDatabasePath));
        store.SaveSnapshot(new MSBuildSolutionSnapshot(
            paths.WatchedProjectPath,
            [
                new MSBuildProjectSnapshot(
                    "project:example",
                    "Example",
                    paths.WatchedProjectPath,
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
                    [new MSBuildDocumentSnapshot("document:program", "Program.cs", paths.ProgramFilePath, [])],
                    [
                        new MSBuildSymbolSnapshot("symbol:program", "Program", "NamedType", "Example", "", paths.ProgramFilePath, 1, 10, "Example.Program"),
                        new MSBuildSymbolSnapshot("symbol:caller", "Caller", "Method", "Example", "Program", paths.ProgramFilePath, 3, 6, "Example.Program.Caller()"),
                        new MSBuildSymbolSnapshot("symbol:target", "Target", "Method", "Example", "Program", paths.ProgramFilePath, 8, 9, "Example.Program.Target()")
                    ],
                    [
                        new MSBuildReferenceSnapshot("symbol:target", paths.ProgramFilePath, 4, 20, "InvocationExpression", "Target()"),
                        new MSBuildReferenceSnapshot("symbol:program", paths.ProgramFilePath, 1, 1, "partial_declaration", "Program")
                    ],
                    [],
                    [],
                    [],
                    [],
                    [],
                    ["DEBUG"])
            ],
            []));

        return new McpSurfaceFixture(
            paths.RepositoryRoot,
            paths.SettingsPath,
            paths.WatchedProjectPath,
            paths.ProgramFilePath,
            paths.RuntimeRoot,
            paths.IndexDatabasePath);
    }

    private static void WriteProject(string watchedProjectPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(watchedProjectPath)!);
        File.WriteAllText(
            watchedProjectPath,
            """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
                <OutputType>Library</OutputType>
                <ImplicitUsings>enable</ImplicitUsings>
                <Nullable>enable</Nullable>
              </PropertyGroup>
            </Project>
            """);
    }

    private static void SaveSettings(FixturePaths paths)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(paths.SettingsPath)!);
        MonitorSettingsLoader.SaveLocal(paths.RepositoryRoot, paths.WatchedProjectPath, paths.RuntimeRoot, paths.SettingsPath);
    }

    private static FixturePaths PreparePaths()
    {
        string repositoryRoot = FindRepositoryRoot();
        string tempRoot = Path.Combine(Path.GetTempPath(), "AIMonitorMcpSurface", Guid.NewGuid().ToString("N"));
        string settingsPath = Path.Combine(tempRoot, "config", "appsettings.json");
        string watchedProjectPath = Path.Combine(tempRoot, "Watched", "Example.csproj");
        string programFilePath = Path.Combine(tempRoot, "Watched", "Program.cs");
        string runtimeRoot = Path.Combine(tempRoot, "runtime");

        MonitorSettings settings = MonitorSettings.Create(repositoryRoot, watchedProjectPath, runtimeRoot);
        string indexDatabasePath = MonitorDataPaths.GetDefaultIndexDatabasePath(settings);
        return new FixturePaths(repositoryRoot, settingsPath, watchedProjectPath, programFilePath, runtimeRoot, indexDatabasePath);
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

    private sealed record FixturePaths(
        string RepositoryRoot,
        string SettingsPath,
        string WatchedProjectPath,
        string ProgramFilePath,
        string RuntimeRoot,
        string IndexDatabasePath);
}

// Thin helpers over the MCP client for the suite: client creation, planned-session bootstrap, staged-record
// inspection, and JSON probing of tool results. The JSON helpers walk the result tree so assertions do not
// depend on the exact nesting of the MCP content envelope.
internal static class McpSurfaceClient
{
    public static async Task<McpClient> ConnectAsync(McpSurfaceFixture fixture)
    {
        string serverDll = Path.Combine(
            fixture.RepositoryRoot,
            "src",
            "AIMonitor.McpServer",
            "bin",
            GetBuildConfiguration(),
            "net10.0",
            "AIMonitor.McpServer.dll");

        StdioClientTransportOptions options = new()
        {
            Name = "ai-monitor-surface",
            Command = "dotnet",
            Arguments = [serverDll, "--repo-root", fixture.RepositoryRoot, "--config", fixture.SettingsPath],
            WorkingDirectory = fixture.RepositoryRoot
        };

        return await McpClient.CreateAsync(new StdioClientTransport(options));
    }

    public static async Task<string> StartPlannedSessionAsync(
        McpClient client,
        McpSurfaceFixture fixture,
        string purpose,
        params string[] filePaths)
    {
        object[] filesPlanned = filePaths
            .Select(filePath => new Dictionary<string, object?>
            {
                ["sourceFilePath"] = filePath,
                ["owningProjectPath"] = fixture.WatchedProjectPath
            })
            .Cast<object>()
            .ToArray();
        CallToolResult session = await client.CallToolAsync(
            "start_monitor_session",
            new Dictionary<string, object?>
            {
                ["purpose"] = purpose,
                ["filesPlanned"] = filesPlanned
            });
        Assert.False(session.IsError == true, Text(session));
        return JsonString(Text(session), "sessionId");
    }

    public static async Task<string> StagedRecordJsonAsync(McpClient client, string stagedRecordId)
    {
        CallToolResult record = await client.CallToolAsync(
            "get_staged_record",
            new Dictionary<string, object?>
            {
                ["stagedRecordId"] = stagedRecordId
            });
        Assert.False(record.IsError == true, Text(record));
        return Text(record);
    }

    public static string Text(CallToolResult result)
    {
        string wrapperJson = System.Text.Json.JsonSerializer.Serialize(result);
        using (System.Text.Json.JsonDocument document = System.Text.Json.JsonDocument.Parse(wrapperJson))
        {
            foreach (System.Text.Json.JsonElement content in document.RootElement.GetProperty("content").EnumerateArray())
            {
                if (content.TryGetProperty("text", out System.Text.Json.JsonElement text)
                    && text.ValueKind == System.Text.Json.JsonValueKind.String)
                {
                    return text.GetString() ?? string.Empty;
                }
            }
        }

        throw new InvalidOperationException("MCP tool result did not include text content.");
    }

    public static string JsonString(string json, string propertyName)
    {
        using (System.Text.Json.JsonDocument document = System.Text.Json.JsonDocument.Parse(json))
        {
            return FindString(document.RootElement, propertyName)
                ?? throw new InvalidOperationException($"Could not find JSON string property '{propertyName}'.");
        }
    }

    public static int JsonInt(string json, string propertyName)
    {
        using (System.Text.Json.JsonDocument document = System.Text.Json.JsonDocument.Parse(json))
        {
            return FindNumber(document.RootElement, propertyName)
                ?? throw new InvalidOperationException($"Could not find JSON number property '{propertyName}'.");
        }
    }

    public static bool JsonBool(string json, string propertyName)
    {
        using (System.Text.Json.JsonDocument document = System.Text.Json.JsonDocument.Parse(json))
        {
            return FindBool(document.RootElement, propertyName)
                ?? throw new InvalidOperationException($"Could not find JSON bool property '{propertyName}'.");
        }
    }

    public static string FakeDiffToolPath()
    {
        if (OperatingSystem.IsWindows())
        {
            string windowsPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "where.exe");
            if (File.Exists(windowsPath))
            {
                return windowsPath;
            }
        }

        foreach (string candidate in new[] { "/usr/bin/true", "/bin/true" })
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new FileNotFoundException("Unable to find a harmless executable for diff-launch tests.");
    }

    private static string GetBuildConfiguration()
    {
#if DEBUG
        return "Debug";
#else
        return "Release";
#endif
    }

    private static string? FindString(System.Text.Json.JsonElement element, string propertyName)
    {
        if (element.ValueKind == System.Text.Json.JsonValueKind.Object)
        {
            foreach (System.Text.Json.JsonProperty property in element.EnumerateObject())
            {
                if (property.NameEquals(propertyName)
                    && property.Value.ValueKind == System.Text.Json.JsonValueKind.String)
                {
                    return property.Value.GetString();
                }

                string? nested = FindString(property.Value, propertyName);
                if (nested is not null)
                {
                    return nested;
                }
            }
        }
        else if (element.ValueKind == System.Text.Json.JsonValueKind.Array)
        {
            foreach (System.Text.Json.JsonElement item in element.EnumerateArray())
            {
                string? nested = FindString(item, propertyName);
                if (nested is not null)
                {
                    return nested;
                }
            }
        }

        return null;
    }

    private static int? FindNumber(System.Text.Json.JsonElement element, string propertyName)
    {
        if (element.ValueKind == System.Text.Json.JsonValueKind.Object)
        {
            foreach (System.Text.Json.JsonProperty property in element.EnumerateObject())
            {
                if (property.NameEquals(propertyName)
                    && property.Value.ValueKind == System.Text.Json.JsonValueKind.Number
                    && property.Value.TryGetInt32(out int value))
                {
                    return value;
                }

                int? nested = FindNumber(property.Value, propertyName);
                if (nested.HasValue)
                {
                    return nested;
                }
            }
        }
        else if (element.ValueKind == System.Text.Json.JsonValueKind.Array)
        {
            foreach (System.Text.Json.JsonElement item in element.EnumerateArray())
            {
                int? nested = FindNumber(item, propertyName);
                if (nested.HasValue)
                {
                    return nested;
                }
            }
        }

        return null;
    }

    private static bool? FindBool(System.Text.Json.JsonElement element, string propertyName)
    {
        if (element.ValueKind == System.Text.Json.JsonValueKind.Object)
        {
            foreach (System.Text.Json.JsonProperty property in element.EnumerateObject())
            {
                if (property.NameEquals(propertyName)
                    && (property.Value.ValueKind == System.Text.Json.JsonValueKind.True
                        || property.Value.ValueKind == System.Text.Json.JsonValueKind.False))
                {
                    return property.Value.GetBoolean();
                }

                bool? nested = FindBool(property.Value, propertyName);
                if (nested.HasValue)
                {
                    return nested;
                }
            }
        }
        else if (element.ValueKind == System.Text.Json.JsonValueKind.Array)
        {
            foreach (System.Text.Json.JsonElement item in element.EnumerateArray())
            {
                bool? nested = FindBool(item, propertyName);
                if (nested.HasValue)
                {
                    return nested;
                }
            }
        }

        return null;
    }
}
