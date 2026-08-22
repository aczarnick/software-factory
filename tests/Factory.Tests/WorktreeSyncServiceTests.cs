using Factory.Runtime;

namespace Factory.Tests;

public sealed class WorktreeSyncServiceTests : IDisposable
{
    private static readonly string[] ExpectedConflictInvocations =
        ["rev-parse mainline", "merge-base basesha headsha", "merge mainline", "merge --abort"];

    private readonly string _dir = TempDir.Create();
    private readonly WorktreeSyncService _sync = new();

    public void Dispose() => TempDir.Delete(_dir);

    private async Task<string> InitMainAsync()
    {
        await Shell.GitAsync(_dir, default, "init", "-q");
        await Shell.GitAsync(_dir, default, "config", "user.email", "factory@local");
        await Shell.GitAsync(_dir, default, "config", "user.name", "Software Factory");
        await File.WriteAllTextAsync(Path.Combine(_dir, "base.txt"), "base\n");
        await Shell.GitAsync(_dir, default, "add", "-A");
        await Shell.GitAsync(_dir, default, "commit", "-q", "-m", "initial commit");
        return (await Shell.GitAsync(_dir, default, "rev-parse", "HEAD")).Stdout.Trim();
    }

    private async Task<string> AddWorktreeAsync(string branch)
    {
        var path = Path.Combine(Path.GetTempPath(), "factory-tests", Guid.NewGuid().ToString("n")[..10]);
        await Shell.GitAsync(_dir, default, "worktree", "add", "-b", branch, path, "HEAD");
        return path;
    }

    // Linked worktrees share the mainline's refs, but "HEAD" is per-worktree: resolved in the main
    // checkout it names mainline's tip, resolved in the worktree it names the worktree's own tip.
    // Merging by the mainline branch's actual name, not "HEAD", is what makes a merge run inside
    // the worktree pick up mainline's commits rather than being a no-op against itself.
    private async Task<string> CurrentBranchAsync() =>
        (await Shell.GitAsync(_dir, default, "branch", "--show-current")).Stdout.Trim();

    [Fact]
    public async Task MainlineUnchangedSinceTheBaseCommitIsANoOp()
    {
        var baseCommit = await InitMainAsync();
        var mainlineRef = await CurrentBranchAsync();
        var worktree = await AddWorktreeAsync("factory/item-a");

        var outcome = await _sync.SyncAsync(_dir, mainlineRef, worktree, baseCommit);

        Assert.Equal(SyncOutcome.NoOp, outcome);
        Assert.False(File.Exists(Path.Combine(worktree, ".git", "MERGE_HEAD")),
            "a no-op must never invoke git merge");
    }

    [Fact]
    public async Task MainlineAdvancedCleanlyIsSyncedIntoTheWorktree()
    {
        var baseCommit = await InitMainAsync();
        var mainlineRef = await CurrentBranchAsync();
        var worktree = await AddWorktreeAsync("factory/item-b");

        await File.WriteAllTextAsync(Path.Combine(_dir, "mainline-addition.txt"), "new on mainline\n");
        await Shell.GitAsync(_dir, default, "add", "-A");
        await Shell.GitAsync(_dir, default, "commit", "-q", "-m", "mainline advances");

        var outcome = await _sync.SyncAsync(_dir, mainlineRef, worktree, baseCommit);

        Assert.Equal(SyncOutcome.Synced, outcome);
        Assert.True(File.Exists(Path.Combine(worktree, "mainline-addition.txt")));
    }

    [Fact]
    public async Task AGenuineConflictAbortsTheMergeAndLeavesTheWorktreeUntouched()
    {
        var baseCommit = await InitMainAsync();
        var mainlineRef = await CurrentBranchAsync();
        var worktree = await AddWorktreeAsync("factory/item-c");

        // Conflicting edits to the same line, one on mainline and one already committed in the
        // worktree's own branch.
        await File.WriteAllTextAsync(Path.Combine(_dir, "base.txt"), "changed on mainline\n");
        await Shell.GitAsync(_dir, default, "add", "-A");
        await Shell.GitAsync(_dir, default, "commit", "-q", "-m", "mainline edits base.txt");

        await File.WriteAllTextAsync(Path.Combine(worktree, "base.txt"), "changed in the worktree\n");
        await Shell.GitAsync(worktree, default, "add", "-A");
        await Shell.GitAsync(worktree, default, "commit", "-q", "-m", "worktree edits base.txt");

        var beforeAttempt = await File.ReadAllTextAsync(Path.Combine(worktree, "base.txt"));

        var outcome = await _sync.SyncAsync(_dir, mainlineRef, worktree, baseCommit);

        Assert.Equal(SyncOutcome.Conflict, outcome);
        Assert.False(File.Exists(Path.Combine(worktree, ".git", "MERGE_HEAD")),
            "a conflicting merge must have been aborted");
        Assert.Equal(beforeAttempt, await File.ReadAllTextAsync(Path.Combine(worktree, "base.txt")));

        // Never touched the main checkout, only the worktree.
        var mainStatus = await Shell.GitAsync(_dir, default, "status", "--porcelain");
        Assert.True(string.IsNullOrWhiteSpace(mainStatus.Stdout));
    }

    private sealed class SpyGitShell : IGitShell
    {
        public readonly List<string> Invocations = new();

        public Task<ShellResult> GitAsync(string workingDirectory, CancellationToken ct, params string[] args)
        {
            var joined = string.Join(' ', args);
            Invocations.Add(joined);
            return Task.FromResult(joined switch
            {
                "rev-parse mainline" => new ShellResult(0, "headsha\n", "", false),
                "merge-base basesha headsha" => new ShellResult(0, "basesha\n", "", false),
                "merge mainline" => new ShellResult(1, "", "conflict", false),
                _ => new ShellResult(0, "", "", false)
            });
        }
    }

    [Fact]
    public async Task AConflictingMergeIssuesMergeThenMergeAbortThroughTheInjectedShell()
    {
        var spy = new SpyGitShell();
        var sync = new WorktreeSyncService(spy);

        var outcome = await sync.SyncAsync("main", "mainline", "worktree", "basesha");

        Assert.Equal(SyncOutcome.Conflict, outcome);
        Assert.Equal(ExpectedConflictInvocations, spy.Invocations);
    }
}
