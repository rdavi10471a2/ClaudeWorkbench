using System.Net.Http.Json;
using System.Text.Json;
using ClaudeWorkbench.Host.Console;
using Microsoft.Extensions.Hosting;

namespace ClaudeWorkbench.Host.Services;

// Polls the two auth sources on an interval and caches the result so the command-bar
// dots can render every frame without shelling a CLI. Each source lives where it
// belongs — Claude behind the sidecar's /auth (it owns the Claude CLI), GitHub in
// GitService (it owns `gh`) — and this service is only the aggregator + poller.
//
// Values are tri-state (null = unknown): a failed sidecar fetch or a missing CLI
// leaves that flag null rather than flipping it to a false "signed out", so the dot
// degrades to a neutral "checking" state instead of lying.
public sealed class AuthStatusProbe : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(15);
    private readonly IHttpClientFactory httpClientFactory;
    private readonly SidecarOptions options;
    private readonly GitService git;
    private readonly JsonSerializerOptions json = new(JsonSerializerDefaults.Web);

    public AuthStatusProbe(IHttpClientFactory httpClientFactory, SidecarOptions options, GitService git)
    {
        this.httpClientFactory = httpClientFactory;
        this.options = options;
        this.git = git;
    }

    public event Action? Changed;

    public AuthStatus Current { get; private set; } = AuthStatus.Unknown;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            await PollAsync(stoppingToken).ConfigureAwait(false);
            try
            {
                await Task.Delay(PollInterval, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task PollAsync(CancellationToken cancellationToken)
    {
        // Probe both concurrently; each degrades to null on its own failure so one
        // source being down never blanks the other.
        Task<bool?> claudeTask = ProbeClaudeAsync(cancellationToken);
        Task<bool?> githubTask = ProbeGitHubAsync(cancellationToken);
        AuthStatus next = new(await claudeTask.ConfigureAwait(false), await githubTask.ConfigureAwait(false));

        if (next != Current)
        {
            Current = next;
            Changed?.Invoke();
        }
    }

    private async Task<bool?> ProbeClaudeAsync(CancellationToken cancellationToken)
    {
        try
        {
            HttpClient client = httpClientFactory.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(12);
            ClaudeAuthResponse? payload = await client
                .GetFromJsonAsync<ClaudeAuthResponse>(options.BaseUrl + "/auth", json, cancellationToken)
                .ConfigureAwait(false);
            return payload?.LoggedIn;
        }
        catch (Exception)
        {
            // Sidecar down / not yet up — unknown, not signed out.
            return null;
        }
    }

    private async Task<bool?> ProbeGitHubAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await git.GetGitHubAuthAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private sealed record ClaudeAuthResponse(bool? LoggedIn);
}
