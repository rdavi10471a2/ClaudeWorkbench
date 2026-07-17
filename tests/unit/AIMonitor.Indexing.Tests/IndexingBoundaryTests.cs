using AIMonitor.Indexing;

namespace AIMonitor.Indexing.Tests;

public sealed class IndexingBoundaryTests
{
    [Fact]
    public void Contract_names_msbuild_as_source_of_project_truth()
    {
        Assert.Contains("MSBuild-loaded projects", IndexingBoundary.Contract);
    }
}
