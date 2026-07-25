using System.Net.Http;

namespace ClaudeWorkbench.E2E.Tests;

// Central knobs for the browser tests, all driven by environment variables so nothing is hard-coded to
// one machine:
//   AIMW_E2E_BASEURL  the running Host UI (default http://localhost:6100 - see Host appsettings.json)
//   AIMW_E2E_HEADED   "1"/"true" to watch the browser; anything else runs headless
//   AIMW_E2E_SLOWMO   optional milliseconds of slow-motion between actions, to make a run watchable
internal static class E2EEnvironment
{
    public static string BaseUrl =>
        Environment.GetEnvironmentVariable("AIMW_E2E_BASEURL") is { Length: > 0 } url
            ? url.TrimEnd('/')
            : "http://localhost:6100";

    public static bool Headed =>
        Environment.GetEnvironmentVariable("AIMW_E2E_HEADED") is "1" or "true" or "TRUE";

    public static float SlowMoMs =>
        float.TryParse(Environment.GetEnvironmentVariable("AIMW_E2E_SLOWMO"), out float value) ? value : 0f;

    // The live driver drives the REAL Claude agent (auth + tokens + non-deterministic), so it is
    // OPT-IN: it stays skipped unless this is set, even when a Host is up. Nothing spends tokens by
    // accident during `dotnet test`.
    public static bool LiveEnabled =>
        Environment.GetEnvironmentVariable("AIMW_E2E_LIVE") is "1" or "true" or "TRUE";

    // Seconds to keep the browser open after the turn completes, so a human can watch/accept in Merge
    // Review before the fixture tears the browser down. 0 = don't hold.
    public static int HoldSeconds =>
        int.TryParse(Environment.GetEnvironmentVariable("AIMW_E2E_HOLD"), out int value) ? value : 0;

    // Where recordings land (video .webm + trace.zip). Default under the OS temp dir.
    public static string ArtifactsDir =>
        Environment.GetEnvironmentVariable("AIMW_E2E_ARTIFACTS") is { Length: > 0 } dir
            ? dir
            : Path.Combine(Path.GetTempPath(), "ClaudeWorkbenchE2E");

    // AIMW_E2E_VIDEO=1 records a .webm of the whole run. AIMW_E2E_TRACE=1 records a Playwright trace
    // (screenshots + DOM snapshots + sources) you open with `playwright show-trace trace.zip`.
    public static bool RecordVideo =>
        Environment.GetEnvironmentVariable("AIMW_E2E_VIDEO") is "1" or "true" or "TRUE";

    public static bool RecordTrace =>
        Environment.GetEnvironmentVariable("AIMW_E2E_TRACE") is "1" or "true" or "TRUE";

    // Which test-prompt to send. AIMW_E2E_PROMPT (absolute or cwd-relative) wins; otherwise defaults to
    // the Calculator "add a method" prompt resolved from the repo tree.
    public static string PromptFile =>
        Environment.GetEnvironmentVariable("AIMW_E2E_PROMPT") is { Length: > 0 } path
            ? path
            : Path.Combine(RepoRoot(), "samples", "watched-solutions", "CalculatorSample", "test-prompts", "01-add-method.md");

    // The prompt text a human would paste: everything under the "## Prompt" heading of a test-prompt .md.
    public static string ReadPromptSection(string promptFilePath)
    {
        string[] lines = File.ReadAllLines(promptFilePath);
        int start = Array.FindIndex(lines, line => line.TrimStart().StartsWith("## Prompt", StringComparison.OrdinalIgnoreCase));
        IEnumerable<string> body = start < 0 ? lines : lines.Skip(start + 1);
        return string.Join("\n", body).Trim();
    }

    // Walk up from the test binary until the samples tree is found, so defaults work without config.
    private static string RepoRoot()
    {
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "samples", "watched-solutions")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        return AppContext.BaseDirectory;
    }

    // Cheap liveness probe used to SKIP (not fail) when no Host is running. Runs before any browser is
    // launched, so the skip path needs neither a live Host nor installed browser binaries.
    public static bool IsHostReachable()
    {
        try
        {
            using HttpClient client = new() { Timeout = TimeSpan.FromSeconds(2) };
            using HttpResponseMessage response = client.GetAsync(BaseUrl).GetAwaiter().GetResult();
            return (int)response.StatusCode < 500;
        }
        catch
        {
            return false;
        }
    }
}
