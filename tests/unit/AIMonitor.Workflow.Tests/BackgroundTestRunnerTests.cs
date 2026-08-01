using AIMonitor.Core;

namespace AIMonitor.Workflow.Tests;

// BackgroundTestRunner launches `dotnet test` in the background and reads results from a TRX file
// (framework-agnostic). These deterministic tests cover TRX parsing, the build-error fallback, and the
// handle lifecycle (unknown id, missing-solution) without running the SDK. A real end-to-end run is
// intentionally left out — spinning up dotnet test in a unit test is the expensive cost we avoid.
public sealed class BackgroundTestRunnerTests : IDisposable
{
    private readonly List<string> tempFiles = new();

    [Fact]
    public void ParseTrx_reads_counts_and_failing_test_names()
    {
        string trx = WriteTrx("""
            <?xml version="1.0" encoding="UTF-8"?>
            <TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">
              <Results>
                <UnitTestResult testName="MyApp.Tests.WidgetTests.Passes" outcome="Passed" />
                <UnitTestResult testName="MyApp.Tests.WidgetTests.Fails_on_null" outcome="Failed" />
                <UnitTestResult testName="MyApp.Tests.WidgetTests.Skipped_case" outcome="NotExecuted" />
              </Results>
              <ResultSummary outcome="Failed">
                <Counters total="3" executed="2" passed="1" failed="1" />
              </ResultSummary>
            </TestRun>
            """);

        (int passed, int failed, int skipped, int total, IReadOnlyList<string> failures) = BackgroundTestRunner.ParseTrx(trx);

        Assert.Equal(1, passed);
        Assert.Equal(1, failed);
        Assert.Equal(1, skipped); // total - passed - failed
        Assert.Equal(3, total);
        Assert.Single(failures);
        Assert.Contains("MyApp.Tests.WidgetTests.Fails_on_null", failures);
    }

    [Fact]
    public void ExtractBuildErrors_pulls_error_lines_and_dedupes()
    {
        string output = string.Join('\n',
            "Determining projects to restore...",
            "C:\\proj\\Widget.cs(10,5): error CS0103: The name 'x' does not exist",
            "C:\\proj\\Widget.cs(10,5): error CS0103: The name 'x' does not exist",
            "Build FAILED.");

        IReadOnlyList<string> errors = BackgroundTestRunner.ExtractBuildErrors(output);

        Assert.Single(errors);
        Assert.Contains("CS0103", errors[0]);
    }

    [Fact]
    public void Get_and_Cancel_of_unknown_run_return_null()
    {
        BackgroundTestRunner runner = new();
        Assert.Null(runner.Get("nope"));
        Assert.Null(runner.Cancel("nope"));
        Assert.Equal(0, runner.CancelAll());
    }

    [Fact]
    public void Start_with_missing_solution_completes_immediately()
    {
        string temp = Path.Combine(Path.GetTempPath(), "cwb-bgtest-" + Guid.NewGuid().ToString("N"));
        MonitorSettings settings = MonitorSettings.Create(
            temp,
            Path.Combine(temp, "DoesNotExist.slnx"),
            Path.Combine(temp, "runtime"));

        BackgroundTestRunner runner = new();
        BackgroundTestRunner.RunSnapshot snapshot = runner.Start(settings);

        Assert.True(snapshot.Done);
        Assert.True(snapshot.IsError);
        Assert.Equal("missing-solution", snapshot.Status);
        // The run is retrievable by its handle.
        Assert.NotNull(runner.Get(snapshot.RunId));
    }

    private string WriteTrx(string content)
    {
        string path = Path.Combine(Path.GetTempPath(), "cwb-trx-" + Guid.NewGuid().ToString("N") + ".trx");
        File.WriteAllText(path, content);
        tempFiles.Add(path);
        return path;
    }

    public void Dispose()
    {
        foreach (string file in tempFiles)
        {
            try { File.Delete(file); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }
}
