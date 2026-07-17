using AIMonitor.Core;
using AIMonitor.Workflow;

namespace AIMonitor.Workflow.Tests;

// ClaudeSmokes — single-file authoring (completeness). Authored by Claude (review+test; no src edits). LOCAL.
//
// The degenerate case of the coupled multi-file page session: authoring ONE new watched file through the safe-edit
// workflow (new_file -> submit candidate -> stage). Proves the simplest authoring path works, that valid C# submits
// without throwing, that new-file review does NOT create watched source, and that the materialized backing store stays
// byte-identical (repeatable). Complements the 3-file coupled-page tests (ClaudeSmokesAuthoringWorkflowTests) and the
// broken-code-behind rejection case.
public sealed class ClaudeSmokesAuthoringSingleFileTests
{
    [Fact]
    [Trait("Suite", "ClaudeSmokes")]
    public void ClaudeSmokes_authoring_a_single_new_file_stages_and_never_touches_backing_store()
    {
        string sampleRoot = Path.Combine(FindRepositoryRoot(), "samples", "watched-solutions", "WinFormsSample");
        string backingStoreFile = Path.Combine(sampleRoot, "Repositories", "CustomerService.cs");
        string backingHashBefore = Sha256(backingStoreFile);

        // Materialize a pristine copy; author against the copy only.
        string tempRoot = Path.Combine(Path.GetTempPath(), "AIMonitorClaudeSmokesSingleFile", Guid.NewGuid().ToString("N"));
        string workCopyRoot = Path.Combine(tempRoot, "WinFormsSample");
        CopyTree(sampleRoot, workCopyRoot);

        string copyProject = Path.Combine(workCopyRoot, "WinFormsSample.csproj");
        string newFilePath = Path.Combine(workCopyRoot, "Repositories", "AuditRepository.cs");
        MonitorSettings settings = MonitorSettings.Create(
            Path.Combine(tempRoot, "_repo"), copyProject, Path.Combine(tempRoot, "_runtime"));
        WorkflowEditService service = new(settings);

        // Self-contained valid C# (no external usings, so overlay validation has nothing to grumble about and syntax is clean).
        const string content =
            "namespace WinFormsSample.Repositories;\n\n" +
            "public sealed class AuditRepository\n" +
            "{\n" +
            "    public AuditRepository() { }\n\n" +
            "    public string Describe() => \"audit\";\n" +
            "}\n";

        // Author ONE future watched file through the workflow: new_file -> submit candidate -> stage.
        EditSessionStatus session = service.NewFile(newFilePath);
        Assert.True(File.Exists(session.WorkingFilePath));      // a monitor-owned Working candidate exists...
        Assert.False(File.Exists(newFilePath));                 // ...but the watched file itself is NOT created yet.

        service.SubmitFile(newFilePath, content);               // valid C# must not throw (single-file syntax gate passes)
        StagedEditRecord staged = service.Stage(newFilePath, "single new file");

        // The staged candidate carries the authored content...
        string stagedText = File.ReadAllText(staged.StagedFilePath);
        Assert.Contains("AuditRepository", stagedText, StringComparison.Ordinal);
        Assert.Contains("Describe", stagedText, StringComparison.Ordinal);
        // ...new-file review still has NOT created watched source (the operator creates it via WinMerge on accept)...
        Assert.False(File.Exists(newFilePath));
        // ...and the checked-in backing-store sample is byte-identical (repeatable, never dirtied).
        Assert.Equal(backingHashBefore, Sha256(backingStoreFile));
    }

    private static void CopyTree(string source, string destination)
    {
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
        using (FileStream stream = File.OpenRead(path))
        {
            return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(stream));
        }
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
