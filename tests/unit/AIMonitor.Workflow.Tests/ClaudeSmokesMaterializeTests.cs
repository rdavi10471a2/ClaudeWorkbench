using System.Security.Cryptography;
using AIMonitor.Core;
using AIMonitor.Workflow;

namespace AIMonitor.Workflow.Tests;

// ClaudeSmokes — backing-store / materialize pattern. Authored by Claude (review+test; no src edits). LOCAL.
//
// A write-cycle (refresh -> edit Working candidate -> stage) must operate on a MATERIALIZED copy of the sample and
// leave the checked-in "backing store" sample byte-identical, so write smokes are repeatable and never dirty the
// committed fixtures. This is the pattern any future accept-cycle smoke will use.
public sealed class ClaudeSmokesMaterializeTests
{
    [Fact]
    [Trait("Suite", "ClaudeSmokes")]
    public void ClaudeSmokes_write_cycle_on_materialized_sample_leaves_backing_store_untouched()
    {
        string sampleRoot = Path.Combine(FindRepositoryRoot(), "samples", "watched-solutions", "WinFormsSample");
        string backingStoreFile = Path.Combine(sampleRoot, "Repositories", "CustomerService.cs");
        string backingHashBefore = Sha256(backingStoreFile);

        // Materialize a pristine copy to a temp dir (the "replace-from" backing store stays put).
        string tempRoot = Path.Combine(Path.GetTempPath(), "AIMonitorClaudeSmokesMaterialize", Guid.NewGuid().ToString("N"));
        string workCopyRoot = Path.Combine(tempRoot, "WinFormsSample");
        CopyTree(sampleRoot, workCopyRoot);

        string copyProject = Path.Combine(workCopyRoot, "WinFormsSample.csproj");
        string copyServiceFile = Path.Combine(workCopyRoot, "Repositories", "CustomerService.cs");
        MonitorSettings settings = MonitorSettings.Create(
            Path.Combine(tempRoot, "_repo"), copyProject, Path.Combine(tempRoot, "_runtime"));
        WorkflowEditService service = new(settings);

        // Edit + stage against the COPY.
        EditSessionStatus refresh = service.Refresh(copyServiceFile);
        string edited = File.ReadAllText(refresh.WorkingFilePath).Replace("LoadAsync", "LoadCustomerAsync");
        File.WriteAllText(refresh.WorkingFilePath, edited);
        StagedEditRecord staged = service.Stage(copyServiceFile);

        // The staged candidate carries the edit (the workflow ran on the copy)...
        Assert.Contains("LoadCustomerAsync", File.ReadAllText(staged.StagedFilePath), StringComparison.Ordinal);
        // ...the copy's own watched source is still unmodified (only the monitor-owned candidate changed)...
        Assert.DoesNotContain("LoadCustomerAsync", File.ReadAllText(copyServiceFile), StringComparison.Ordinal);
        // ...and the checked-in backing-store sample is byte-identical (repeatable, never dirtied).
        Assert.Equal(backingHashBefore, Sha256(backingStoreFile));
    }

    private static void CopyTree(string source, string destination)
    {
        foreach (string dir in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
        {
            string name = Path.GetFileName(dir);
            if (name is "bin" or "obj")
            {
                continue;
            }

            Directory.CreateDirectory(dir.Replace(source, destination, StringComparison.Ordinal));
        }

        Directory.CreateDirectory(destination);
        foreach (string file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            if (file.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Any(part => part is "bin" or "obj"))
            {
                continue;
            }

            string target = file.Replace(source, destination, StringComparison.Ordinal);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: true);
        }
    }

    private static string Sha256(string path)
    {
        using FileStream stream = File.OpenRead(path);
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(stream));
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

        throw new DirectoryNotFoundException("Could not find ClaudeWorkbench.slnx.");
    }
}
