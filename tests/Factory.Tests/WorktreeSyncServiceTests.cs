using Factory.Runtime;

namespace Factory.Tests;

public class WorktreeSyncServiceTests : IDisposable
{
    private readonly string _dir = TempDir.Create();
    private readonly WorktreeSyncService _sync = new();

    public void Dispose() => TempDir.Delete(_dir);

    private async Task<string> InitMainAsync()
    {
        await Shell.GitAsync(_dir, default, "init", "-q");
        await Shell.GitAsync(_dir, default, "config", "user.email", "factory@local");
        await Shell.GitAsync(_dir, default, "config", "user.name", "Software Factory");
        File.WriteAllText(Path.Combine(_dir, "base.txt"), "base\n");
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
    public async Task Mainline_unchanged_since_the_base_commit_is_a_no_op()
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
    public async Task Mainline_advanced_cleanly_is_synced_into_the_worktree()
    {
        var baseCommit = await InitMainAsync();
        var mainlineRef = await CurrentBranchAsync();
        var worktree = await AddWorktreeAsync("factory/item-b");

        File.WriteAllText(Path.Combine(_dir, "mainline-addition.txt"), "new on mainline\n");
        await Shell.GitAsync(_dir, default, "add", "-A");
        await Shell.GitAsync(_dir, default, "commit", "-q", "-m", "mainline advances");

        var outcome = await _sync.SyncAsync(_dir, mainlineRef, worktree, baseCommit);

        Assert.Equal(SyncOutcome.Synced, outcome);
        Assert.True(File.Exists(Path.Combine(worktree, "mainline-addition.txt")));
    }

    [Fact]
    public async Task A_genuine_conflict_aborts_the_merge_and_leaves_the_worktree_untouched()
    {
        var baseCommit = await InitMainAsync();
        var mainlineRef = await CurrentBranchAsync();
        var worktree = await AddWorktreeAsync("factory/item-c");

        // Conflicting edits to the same line, one on mainline and one already committed in the
        // worktree's own branch.
        File.WriteAllText(Path.Combine(_dir, "base.txt"), "changed on mainline\n");
        await Shell.GitAsync(_dir, default, "add", "-A");
        await Shell.GitAsync(_dir, default, "commit", "-q", "-m", "mainline edits base.txt");

        File.WriteAllText(Path.Combine(worktree, "base.txt"), "changed in the worktree\n");
        await Shell.GitAsync(worktree, default, "add", "-A");
        await Shell.GitAsync(worktree, default, "commit", "-q", "-m", "worktree edits base.txt");

        var beforeAttempt = File.ReadAllText(Path.Combine(worktree, "base.txt"));

        var outcome = await _sync.SyncAsync(_dir, mainlineRef, worktree, baseCommit);

        Assert.Equal(SyncOutcome.Conflict, outcome);
        Assert.False(File.Exists(Path.Combine(worktree, ".git", "MERGE_HEAD")),
            "a conflicting merge must have been aborted");
        Assert.Equal(beforeAttempt, File.ReadAllText(Path.Combine(worktree, "base.txt")));

        // Never touched the main checkout, only the worktree.
        var mainStatus = await Shell.GitAsync(_dir, default, "status", "--porcelain");
        Assert.True(string.IsNullOrWhiteSpace(mainStatus.Stdout));
    }
}
