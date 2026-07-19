using System.Text.Json;
using System.Text.Json.Serialization;

namespace AIMonitor.Core;

public static class MonitorSettingsLoader
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true
    };

    static MonitorSettingsLoader()
    {
        SerializerOptions.Converters.Add(new JsonStringEnumConverter());
    }

    public static MonitorSettings Load(string repositoryRoot, string? settingsPath = null)
    {
        string resolvedRepositoryRoot = Path.GetFullPath(repositoryRoot);
        string resolvedSettingsPath = ResolveSettingsPath(resolvedRepositoryRoot, settingsPath);

        if (!File.Exists(resolvedSettingsPath))
        {
            throw new FileNotFoundException("AIMonitor settings file was not found.", resolvedSettingsPath);
        }

        using FileStream stream = File.OpenRead(resolvedSettingsPath);
        using JsonDocument document = JsonDocument.Parse(stream);
        JsonElement monitor = document.RootElement.GetProperty("Monitor");
        string watchedSolutionPath = RequireString(monitor, "WatchedSolutionPath");
        string runtimeRoot = GetString(monitor, "RuntimeRoot") ?? "runtime";
        string settingsDirectory = Path.GetDirectoryName(resolvedSettingsPath) ?? resolvedRepositoryRoot;

        return MonitorSettings.Create(
            resolvedRepositoryRoot,
            ResolvePath(watchedSolutionPath, settingsDirectory),
            ResolvePath(runtimeRoot, resolvedRepositoryRoot));
    }

    public static string SaveLocal(
        string repositoryRoot,
        string watchedSolutionPath,
        string? runtimeRoot = null,
        string? settingsPath = null)
    {
        string resolvedRepositoryRoot = Path.GetFullPath(repositoryRoot);
        string resolvedSettingsPath = ResolveSettingsPath(resolvedRepositoryRoot, settingsPath);
        string resolvedWatchedSolutionPath = Path.GetFullPath(watchedSolutionPath);
        string resolvedRuntimeRoot = string.IsNullOrWhiteSpace(runtimeRoot)
            ? "runtime"
            : runtimeRoot;

        Directory.CreateDirectory(Path.GetDirectoryName(resolvedSettingsPath) ?? resolvedRepositoryRoot);
        LocalSettingsFile file = new(
            new LocalMonitorSettings(
                resolvedWatchedSolutionPath,
                resolvedRuntimeRoot));
        File.WriteAllText(resolvedSettingsPath, JsonSerializer.Serialize(file, SerializerOptions) + Environment.NewLine);
        return resolvedSettingsPath;
    }

    private static string ResolveSettingsPath(string resolvedRepositoryRoot, string? settingsPath)
    {
        return Path.GetFullPath(
            settingsPath ?? Path.Combine(resolvedRepositoryRoot, "config", "appsettings.json"));
    }

    private static string RequireString(JsonElement element, string propertyName)
    {
        string? value = GetString(element, propertyName);
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException($"Monitor:{propertyName} is required.");
        }

        return value;
    }

    private static string? GetString(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out JsonElement value)
            && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
    }

    private static string ResolvePath(string path, string baseDirectory)
    {
        return Path.IsPathRooted(path)
            ? Path.GetFullPath(path)
            : Path.GetFullPath(Path.Combine(baseDirectory, path));
    }

    private sealed record LocalSettingsFile(LocalMonitorSettings Monitor);

    private sealed record LocalMonitorSettings(
        string WatchedSolutionPath,
        string RuntimeRoot);
}
