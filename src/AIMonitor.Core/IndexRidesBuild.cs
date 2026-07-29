namespace AIMonitor.Core;

/// <summary>
/// ADR-0007 rollout switch. When <see cref="Enabled"/>, the index is a Roslyn pass over a real build's
/// OUTPUTS (source + the build's generated <c>.g.cs</c> + its resolved reference set) rather than its own
/// in-proc compile. Opt-in via the environment variable <c>CWB_INDEX_RIDES_BUILD</c> while the convergence
/// rolls out side-by-side with the existing loader.
///
/// One place to read the flag so every layer that has to agree on the ordering — the build-after-accept
/// (<c>SolutionBuildService</c>), the reindex (<c>SolutionIndexBuilder</c>), and the accept flow
/// (<c>EngineReviewWorkflow</c>) — reads the same truth, not three drifting env checks.
/// </summary>
public static class IndexRidesBuild
{
    public const string EnvironmentVariable = "CWB_INDEX_RIDES_BUILD";

    /// <summary>True when the ADR-0007 build-output index path is switched on.</summary>
    public static bool Enabled =>
        Environment.GetEnvironmentVariable(EnvironmentVariable) is "1" or "true" or "TRUE";
}
