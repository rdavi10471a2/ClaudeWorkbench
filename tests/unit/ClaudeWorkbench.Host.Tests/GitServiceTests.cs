using ClaudeWorkbench.Host.Services;

namespace ClaudeWorkbench.Host.Tests;

// Hermetic tests for the host-side git wrapper. Every test operates on a throwaway
// temp repo; the one push test pushes to a local bare repo (a file path), so nothing
// touches the network. No fixtures depend on the machine's real repos.
public sealed class GitServiceTests : IDisposable
{
    private readonly GitService git = new();
    private readonly List<string> tempDirs = new();

    // --- fixtures ----------------------------------------------------------

    private string NewTempDir()
    {
        string dir = Path.Combine(Path.GetTempPath(), "cwb-git-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        tempDirs.Add(dir);
        return dir;
    }

    private async Task<string> NewRepoAsync()
    {
        string dir = NewTempDir();
        GitResult init = await git.InitAsync(dir);
        Assert.True(init.Ok, init.StdErr);
        // Deterministic identity so commits work on a bare CI box.
        await git.RunAsync(dir, ["config", "user.email", "arena@test.local"]);
        await git.RunAsync(dir, ["config", "user.name", "Governed Barbarian"]);
        await git.RunAsync(dir, ["config", "commit.gpgsign", "false"]);
        return dir;
    }

    private static void Write(string dir, string name, string content)
        => File.WriteAllText(Path.Combine(dir, name), content);

    // --- repository detection ---------------------------------------------

    [Fact]
    public async Task IsRepository_is_false_for_a_plain_directory()
    {
        string dir = NewTempDir();

        Assert.False(await git.IsRepositoryAsync(dir));

        GitStatus status = await git.GetStatusAsync(dir);
        Assert.False(status.IsRepository);
        Assert.True(status.GitAvailable);
    }

    [Fact]
    public async Task Init_creates_a_repo_on_the_default_branch()
    {
        string dir = await NewRepoAsync();

        Assert.True(await git.IsRepositoryAsync(dir));

        GitStatus status = await git.GetStatusAsync(dir);
        Assert.True(status.IsRepository);
        Assert.Equal("main", status.Branch);
        Assert.False(status.HasRemote);
        Assert.False(status.HasChanges);
    }

    [Fact]
    public async Task GetStatus_reports_unavailable_when_git_cannot_be_launched()
    {
        // A GitService pointed at a non-existent executable: process.Start throws,
        // which must surface as GitAvailable=false, NOT a misleading "not a repo".
        GitService missing = new(Path.Combine(Path.GetTempPath(), "no-git-" + Guid.NewGuid().ToString("N")));
        string dir = NewTempDir();

        GitStatus status = await missing.GetStatusAsync(dir);

        Assert.False(status.GitAvailable);
        Assert.False(status.IsRepository);
        Assert.False(string.IsNullOrWhiteSpace(status.Error));
    }

    // --- staged / unstaged parsing ----------------------------------------

    [Fact]
    public async Task Status_classifies_untracked_then_staged()
    {
        string dir = await NewRepoAsync();
        Write(dir, "a.txt", "hello\n");

        GitStatus untracked = await git.GetStatusAsync(dir);
        Assert.True(untracked.HasChanges);
        Assert.False(untracked.HasStaged);
        GitChange u = Assert.Single(untracked.Unstaged);
        Assert.True(u.IsUntracked);

        Assert.True((await git.StageAsync(dir, "a.txt")).Ok);

        GitStatus staged = await git.GetStatusAsync(dir);
        Assert.True(staged.HasStaged);
        GitChange s = Assert.Single(staged.Staged);
        Assert.False(s.IsUntracked);
        Assert.False(s.IsUnstaged);
        Assert.Empty(staged.Unstaged);
    }

    [Fact]
    public async Task Modified_tracked_file_shows_as_unstaged_then_stageable()
    {
        string dir = await NewRepoAsync();
        Write(dir, "a.txt", "one\n");
        Assert.True((await git.CommitAsync(dir, "seed", stageAll: true)).Ok);

        Write(dir, "a.txt", "one\ntwo\n");
        GitStatus mod = await git.GetStatusAsync(dir);
        GitChange c = Assert.Single(mod.Unstaged);
        Assert.Equal('M', c.WorkTree);
        Assert.False(c.IsStaged);

        Assert.True((await git.StageAsync(dir, "a.txt")).Ok);
        GitStatus staged = await git.GetStatusAsync(dir);
        Assert.True(staged.HasStaged);
        Assert.Empty(staged.Unstaged);
    }

    // --- commit semantics --------------------------------------------------

    [Fact]
    public async Task Commit_with_stageAll_commits_everything_and_leaves_a_clean_tree()
    {
        string dir = await NewRepoAsync();
        Write(dir, "a.txt", "a\n");
        Write(dir, "b.txt", "b\n");

        GitResult commit = await git.CommitAsync(dir, "first blood", stageAll: true);
        Assert.True(commit.Ok, commit.StdErr);

        Assert.False((await git.GetStatusAsync(dir)).HasChanges);
        IReadOnlyList<GitCommit> log = await git.LogAsync(dir);
        Assert.Equal("first blood", Assert.Single(log).Subject);
    }

    [Fact]
    public async Task Commit_without_stageAll_commits_only_the_staged_set()
    {
        string dir = await NewRepoAsync();
        Write(dir, "a.txt", "a\n");
        Write(dir, "b.txt", "b\n");
        Assert.True((await git.StageAsync(dir, "a.txt")).Ok);

        // stageAll:false -> only a.txt is committed; b.txt stays as a pending change.
        GitResult commit = await git.CommitAsync(dir, "only a", stageAll: false);
        Assert.True(commit.Ok, commit.StdErr);

        GitStatus after = await git.GetStatusAsync(dir);
        Assert.True(after.HasChanges);
        Assert.Contains(after.Changes, change => change.Path.EndsWith("b.txt", StringComparison.Ordinal));
        Assert.DoesNotContain(after.Changes, change => change.Path.EndsWith("a.txt", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Commit_rejects_an_empty_message_without_calling_git()
    {
        string dir = await NewRepoAsync();
        Write(dir, "a.txt", "a\n");

        GitResult commit = await git.CommitAsync(dir, "   ", stageAll: true);

        Assert.False(commit.Ok);
        Assert.Empty(await git.LogAsync(dir));
    }

    // --- diff / show -------------------------------------------------------

    [Fact]
    public async Task Diff_and_show_reflect_a_modification()
    {
        string dir = await NewRepoAsync();
        Write(dir, "a.txt", "one\n");
        Assert.True((await git.CommitAsync(dir, "seed", stageAll: true)).Ok);
        Write(dir, "a.txt", "one\ntwo\n");

        string diff = await git.DiffTextAsync(dir, "a.txt", staged: false, untracked: false);
        Assert.Contains("+two", diff);

        string committed = await git.ShowAsync(dir, "HEAD:a.txt");
        Assert.Equal("one", committed.Trim());
    }

    // --- branches ----------------------------------------------------------

    [Fact]
    public async Task Create_switch_and_list_branches()
    {
        string dir = await NewRepoAsync();
        Write(dir, "a.txt", "a\n");
        Assert.True((await git.CommitAsync(dir, "seed", stageAll: true)).Ok);

        Assert.True((await git.CreateBranchAsync(dir, "feature/blade")).Ok);
        Assert.Equal("feature/blade", (await git.GetStatusAsync(dir)).Branch);

        IReadOnlyList<string> branches = await git.ListBranchesAsync(dir);
        Assert.Contains("feature/blade", branches);
        Assert.Contains("main", branches);

        Assert.True((await git.SwitchBranchAsync(dir, "main")).Ok);
        Assert.Equal("main", (await git.GetStatusAsync(dir)).Branch);
    }

    // --- push (hermetic: local bare remote, no network) --------------------

    [Fact]
    public async Task Push_sets_upstream_on_first_push_to_a_bare_remote()
    {
        string work = await NewRepoAsync();
        Write(work, "a.txt", "a\n");
        Assert.True((await git.CommitAsync(work, "seed", stageAll: true)).Ok);

        string bare = NewTempDir();
        Assert.True((await git.RunAsync(bare, ["init", "--bare"])).Ok);
        Assert.True((await git.RunAsync(work, ["remote", "add", "origin", bare])).Ok);

        GitStatus before = await git.GetStatusAsync(work);
        Assert.True(before.HasRemote);
        Assert.Null(before.Upstream);

        // remote + branch supplied -> push -u origin <branch>
        GitResult push = await git.PushAsync(work, "origin", before.Branch);
        Assert.True(push.Ok, push.StdErr);

        // the bare repo now holds the branch, and the work repo has an upstream.
        Assert.True((await git.RunAsync(bare, ["rev-parse", "main"])).Ok);
        GitStatus after = await git.GetStatusAsync(work);
        Assert.Equal("origin/main", after.Upstream);
        Assert.Equal(0, after.Ahead);
    }

    // --- pure logic: commit-message drafting -------------------------------

    [Theory]
    [InlineData(new[] { "src/Foo.cs" }, "Update Foo.cs")]
    [InlineData(new[] { "a.cs", "b.cs" }, "Update a.cs, b.cs")]
    [InlineData(new[] { "a.cs", "b.cs", "c.cs", "d.cs" }, "Update 4 files")]
    public void DraftCommitMessage_summarizes_the_changed_files(string[] paths, string expected)
    {
        List<GitChange> changes = paths.Select(path => new GitChange(' ', 'M', path)).ToList();
        GitStatus status = new(true, "main", null, 0, 0, false, changes);

        Assert.Equal(expected, GitWorkspaceService.DraftCommitMessage(status));
    }

    [Fact]
    public void DraftCommitMessage_is_empty_when_there_is_nothing_to_commit()
    {
        GitStatus clean = new(true, "main", "origin/main", 0, 0, true, []);
        Assert.Equal(string.Empty, GitWorkspaceService.DraftCommitMessage(clean));
    }

    // --- cleanup -----------------------------------------------------------

    public void Dispose()
    {
        foreach (string dir in tempDirs)
        {
            try
            {
                ForceDelete(dir);
            }
            catch
            {
                // Best-effort temp cleanup; a leftover temp dir must never fail a test run.
            }
        }
    }

    private static void ForceDelete(string dir)
    {
        if (!Directory.Exists(dir))
        {
            return;
        }

        // git marks pack/object files read-only; clear attributes so Delete succeeds on Windows.
        foreach (string file in Directory.GetFiles(dir, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(file, FileAttributes.Normal);
        }

        Directory.Delete(dir, recursive: true);
    }
}
