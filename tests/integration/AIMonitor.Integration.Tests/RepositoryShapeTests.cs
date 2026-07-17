namespace AIMonitor.Integration.Tests;

public sealed class RepositoryShapeTests
{
    [Fact]
    public void Source_tests_samples_and_docs_are_top_level_siblings()
    {
        string repoRoot = FindRepositoryRoot();

        Assert.True(Directory.Exists(Path.Combine(repoRoot, "src")));
        Assert.True(Directory.Exists(Path.Combine(repoRoot, "tests")));
        Assert.True(Directory.Exists(Path.Combine(repoRoot, "samples")));
        Assert.True(Directory.Exists(Path.Combine(repoRoot, "docs")));
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? current = new(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "ClaudeWorkbench.slnx")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new DirectoryNotFoundException("Could not find AIMonitor repository root.");
    }
}
