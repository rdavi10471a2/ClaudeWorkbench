using AIMonitor.MSBuild;

namespace AIMonitor.MSBuild.Tests;

// Salvaged from tests/smoke/AIMonitor.SmokeTests (Phase 5.1 of
// docs/plans/retire-legacy-test-harness.md). That runner swept every *.razor.cs in a watched
// solution and asserted each was indexed, with pure code-behind also contributing symbols —
// a real Blazor invariant. It was unrunnable: its two fixtures pointed at private solutions on
// one developer's C: drive, and on a clean machine it found zero samples and returned 0.
//
// Rehomed against the in-repo BlazorSample fixture so it runs anywhere.
//
// Why this matters: a .razor.cs is an ordinary C# file, but it only carries meaning as the
// partial half of a component whose other half is markup. If the loader drops it — or indexes
// the document but extracts no symbols from it — every question the agent asks about component
// state ("who writes LoadedCount?") silently returns nothing. That is the quiet-wrong failure
// mode: the index looks healthy, the counts look plausible, and the answer is empty.
public sealed class RazorCodeBehindIndexingTests
{
    [Fact]
    public async Task Razor_code_behind_is_indexed_as_a_document_and_contributes_symbols()
    {
        string projectPath = Path.Combine(
            FindRepositoryRoot(),
            "samples",
            "watched-solutions",
            "BlazorSample",
            "BlazorSample.csproj");
        Assert.True(File.Exists(projectPath), $"Fixture missing: {projectPath}");

        MSBuildSolutionSnapshot snapshot = await new MSBuildWorkspaceLoader().OpenProjectAsync(projectPath);

        MSBuildDocumentSnapshot[] codeBehind = snapshot.Projects
            .SelectMany(project => project.Documents)
            .Where(document => document.FilePath.EndsWith(".razor.cs", StringComparison.OrdinalIgnoreCase))
            .ToArray();

        // The sweep's first assertion: every .razor.cs on disk must be present as a document.
        // Enumerate the disk side independently rather than trusting the snapshot to be complete.
        string componentsRoot = Path.Combine(Path.GetDirectoryName(projectPath)!, "Components");
        string[] onDisk = Directory
            .EnumerateFiles(componentsRoot, "*.razor.cs", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)
                && !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
            .ToArray();

        Assert.NotEmpty(onDisk);
        foreach (string path in onDisk)
        {
            Assert.Contains(
                codeBehind,
                document => document.FilePath.Equals(path, StringComparison.OrdinalIgnoreCase));
        }

        // The sweep's second assertion, and the one that actually bites: presence is not enough.
        // A code-behind that indexes as a document but yields no symbols is worse than a missing
        // one, because the document count looks right.
        MSBuildSymbolSnapshot[] codeBehindSymbols = snapshot.Projects
            .SelectMany(project => project.Symbols)
            .Where(symbol => symbol.FilePath.EndsWith(".razor.cs", StringComparison.OrdinalIgnoreCase))
            .ToArray();

        Assert.NotEmpty(codeBehindSymbols);
        Assert.Contains(codeBehindSymbols, symbol => symbol.Name == "LoadedCount");
        Assert.Contains(codeBehindSymbols, symbol => symbol.Name == "RecordLoad");
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

        throw new InvalidOperationException("Could not locate the repository root (ClaudeWorkbench.slnx).");
    }
}
