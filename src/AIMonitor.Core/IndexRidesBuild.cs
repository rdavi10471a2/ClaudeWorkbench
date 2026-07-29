namespace AIMonitor.Core;

/// <summary>
/// ADR-0007 helper for the "index rides the build" ordering. There is no flag: the index is always a Roslyn
/// pass over a real build's OUTPUTS (source + the build's generated <c>.g.cs</c> + its resolved reference set).
/// The build is the single compile; the index reads it. The in-proc self-compiling loader survives only as a
/// last resort for a watched entry that resolves to no buildable project — never as a build-failure fallback
/// (a failed build preserves the last-good index, it does not trigger a second compile).
///
/// One place for the shared facts every layer must agree on — the build-after-accept
/// (<c>SolutionBuildService</c>), the reindex (<c>SolutionIndexBuilder</c>), and the accept flow
/// (<c>EngineReviewWorkflow</c>).
/// </summary>
public static class IndexRidesBuild
{
    /// <summary>
    /// The build configuration the index (and the accept-flow build-after-accept that feeds it) always uses.
    /// The index is a single, consistent Debug-configuration view of the code; Debug/Release is a Source-tab
    /// concern (build/run the app to test it), which never feeds the index. Kept here so the build-for-index
    /// and the read of its output can never diverge on configuration.
    /// </summary>
    public const string IndexBuildConfiguration = "Debug";

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
