using AIMonitor.Core;
using Xunit;

namespace AIMonitor.Workflow.Tests;

// Covers the in-memory-authoritative staged-record store: durability across a process
// "restart" (rehydrate from disk), atomic write-through (no torn/temp files surface),
// clone isolation, and thread-safety — the file-sharing race that used to 500 an accept
// when the accept path and a supersede/list overlapped on the same record JSON.
public sealed class WorkflowEditServiceRecordStoreTests
{
    [Fact]
    public void Staged_record_survives_a_new_service_instance_rehydrating_from_disk()
    {
        MonitorSettings settings = CreateSettings(out string programPath);
        WorkflowEditService writer = new(settings);
        StagedEditRecord staged = StageCandidate(writer, programPath);

        // Simulate a host restart: a brand-new instance over the same runtime must see the
        // durably-persisted record (write-through) without the original instance's memory.
        WorkflowEditService reopened = new(settings);
        StagedEditRecord reloaded = reopened.GetStagedRecord(staged.StagedRecordId);

        Assert.Equal(staged.StagedRecordId, reloaded.StagedRecordId);
        Assert.Equal(staged.StagedHash, reloaded.StagedHash);
        Assert.Contains(reopened.ListStagedRecords(), record => record.StagedRecordId == staged.StagedRecordId);
    }

    [Fact]
    public void Save_is_atomic_leaves_no_temp_and_list_ignores_a_crash_leftover_temp()
    {
        MonitorSettings settings = CreateSettings(out string programPath);
        WorkflowEditService service = new(settings);
        StageCandidate(service, programPath);

        string recordsRoot = new WorkflowEditPaths(settings).StagedRecordsRoot;
        // The atomic temp is renamed into place, never left behind.
        Assert.Empty(Directory.EnumerateFiles(recordsRoot, "*.writing"));

        // A crash could leave a half-written temp; a fresh instance must ignore it (it is
        // not "*.json") and never surface it as a record.
        File.WriteAllText(Path.Combine(recordsRoot, "leftover.json.writing"), "{ not valid json");
        WorkflowEditService reopened = new(settings);
        Assert.Single(reopened.ListStagedRecords());
    }

    [Fact]
    public void GetStagedRecord_returns_an_isolated_copy_so_unsaved_mutation_does_not_leak()
    {
        MonitorSettings settings = CreateSettings(out string programPath);
        WorkflowEditService service = new(settings);
        StagedEditRecord staged = StageCandidate(service, programPath);

        StagedEditRecord first = service.GetStagedRecord(staged.StagedRecordId);
        first.Message = "mutated locally without saving";

        StagedEditRecord second = service.GetStagedRecord(staged.StagedRecordId);
        Assert.NotEqual("mutated locally without saving", second.Message);
    }

    [Fact]
    public async Task Concurrent_reads_and_writes_do_not_throw_a_file_sharing_violation()
    {
        MonitorSettings settings = CreateSettings(out string programPath);
        WorkflowEditService service = new(settings);
        string id = StageCandidate(service, programPath).StagedRecordId;

        // Before the lock + in-memory store, overlapping File.Read/WriteAllText on the record
        // JSON threw "being used by another process" intermittently. Hammer it from many
        // threads: this must now complete cleanly.
        IEnumerable<Task> tasks = Enumerable.Range(0, 32).Select(worker => Task.Run(() =>
        {
            for (int j = 0; j < 20; j++)
            {
                service.ListStagedRecords();
                service.GetStagedRecord(id);
                service.RecordDiffLaunch(id, launched: true, "concurrent");
            }
        }));

        await Task.WhenAll(tasks);
        Assert.Equal(id, service.GetStagedRecord(id).StagedRecordId);
    }

    private static StagedEditRecord StageCandidate(WorkflowEditService service, string programPath)
    {
        EditSessionStatus refresh = service.Refresh(programPath);
        File.WriteAllText(
            refresh.WorkingFilePath,
            "namespace Example { internal static class Program { public static string Value => \"candidate\"; } }");
        return service.Stage(programPath);
    }

    private static MonitorSettings CreateSettings(out string programFilePath)
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), "AIMonitorRecordStoreTests", Guid.NewGuid().ToString("N"));
        string repositoryRoot = Path.Combine(tempRoot, "Repo");
        string runtimeRoot = Path.Combine(tempRoot, "Runtime");
        string watchedRoot = Path.Combine(tempRoot, "Watched");
        string projectPath = Path.Combine(watchedRoot, "Example.csproj");
        programFilePath = Path.Combine(watchedRoot, "Program.cs");

        Directory.CreateDirectory(watchedRoot);
        File.WriteAllText(projectPath, "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        File.WriteAllText(programFilePath, "namespace Example { internal static class Program { } }");

        return MonitorSettings.Create(repositoryRoot, projectPath, runtimeRoot);
    }
}
