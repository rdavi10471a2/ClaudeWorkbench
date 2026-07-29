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

    /// <summary>
    /// Per-project reference dump file, written into each project's own obj so nothing clobbers. The build
    /// writes it; the index reads it. One name shared by the build-after-accept and the index loader.
    /// </summary>
    public const string PerProjectRefsFileName = "aimonitor-index-refs.txt";

    /// <summary>
    /// Writes the MSBuild target that dumps each project's resolved references (<c>@(ReferencePath)</c>) into
    /// its own <c>obj</c> (<c>$(IntermediateOutputPath)</c>) at <c>ResolveReferences</c> time — so it runs even
    /// when a project is up-to-date and CoreCompile is skipped (the warm-up / accept-rebuild case). Returns the
    /// <c>.targets</c> file path (a temp file; the caller deletes its parent dir when done). Pass it via
    /// <c>-p:CustomAfterMicrosoftCommonTargets=&lt;path&gt;</c> alongside <c>-p:EmitCompilerGeneratedFiles=true</c>
    /// so ONE build emits every project's generated <c>.g.cs</c> and per-project refs for the index to read.
    /// </summary>
    public static string WritePerProjectRefsTargetsFile()
    {
        string directory = Path.Combine(Path.GetTempPath(), "AIMonitorRideBuild", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string targetsFile = Path.Combine(directory, "dumprefs-per-project.targets");
        File.WriteAllText(targetsFile, $$"""
            <Project>
              <Target Name="DumpRefsForIndexPerProject" AfterTargets="ResolveReferences">
                <WriteLinesToFile File="$(IntermediateOutputPath){{PerProjectRefsFileName}}" Lines="@(ReferencePath)" Overwrite="true" />
              </Target>
            </Project>
            """);
        return targetsFile;
    }
}
