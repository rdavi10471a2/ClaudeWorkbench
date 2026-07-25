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
