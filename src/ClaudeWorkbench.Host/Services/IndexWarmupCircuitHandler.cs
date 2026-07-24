using Microsoft.AspNetCore.Components.Server.Circuits;

namespace ClaudeWorkbench.Host.Services;

// Fires the one-time index warm-up once the Blazor circuit's connection is up — i.e. the operator
// console is actually loaded and interactive — so neither the freshness check nor a rebuild runs on
// the first-paint path. Run-once is enforced inside StartupIndexWarmup, so reconnects are no-ops.
public sealed class IndexWarmupCircuitHandler : CircuitHandler
{
    private readonly StartupIndexWarmup warmup;

    public IndexWarmupCircuitHandler(StartupIndexWarmup warmup)
    {
        this.warmup = warmup;
    }

    public override Task OnConnectionUpAsync(Circuit circuit, CancellationToken cancellationToken)
    {
        warmup.EnsureWarmOnce();
        return Task.CompletedTask;
    }
}
