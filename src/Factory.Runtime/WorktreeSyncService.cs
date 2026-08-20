namespace Factory.Runtime;

/// <summary>Runs a git subcommand for <see cref="WorktreeSyncService"/>. The default implementation
/// delegates to <see cref="Shell.GitAsync"/>; tests can substitute a spy to observe the exact
/// subcommands a sync issues without shelling out to a real git process.</summary>
public interface IGitShell
{
    Task<ShellResult> GitAsync(string workingDirectory, CancellationToken ct, params string[] args);
}

/// <summary>Default <see cref="IGitShell"/>, forwarding to the real <see cref="Shell"/>.</summary>
public sealed class ProcessGitShell : IGitShell
{
    public Task<ShellResult> GitAsync(string workingDirectory, CancellationToken ct, params string[] args) =>
        Shell.GitAsync(workingDirectory, ct, args);
}

/// <summary>
/// Detects whether mainline has advanced past an item's worktree branch base commit and, if so,
/// merges mainline into the worktree. Detection is by comparing the branch's recorded base
/// commit against mainline HEAD via <c>merge-base</c> — it never inspects the item's branch tip,
/// so a sync decision does not depend on how far the item's own work has progressed. Every merge
/// (and abort) runs inside the worktree; the main checkout is never written to.
/// </summary>
public sealed class WorktreeSyncService
{
    private readonly IGitShell _shell;

    public WorktreeSyncService() : this(new ProcessGitShell()) { }

    public WorktreeSyncService(IGitShell shell) => _shell = shell;

    public async Task<SyncOutcome> SyncAsync(
        string mainCheckoutPath, string mainlineRef, string worktreePath, string baseCommit,
        CancellationToken ct = default)
    {
        var head = await _shell.GitAsync(mainCheckoutPath, ct, "rev-parse", mainlineRef).ConfigureAwait(false);
        if (!head.Ok) return SyncOutcome.NoOp;

        var headSha = head.Stdout.Trim();
        if (headSha == baseCommit) return SyncOutcome.NoOp;

        // baseCommit must be a strict ancestor of mainline HEAD for mainline to have "advanced
        // past" it — merge-base of the two is baseCommit itself exactly when that holds.
        var mergeBase = await _shell.GitAsync(mainCheckoutPath, ct, "merge-base", baseCommit, headSha)
            .ConfigureAwait(false);
        if (!mergeBase.Ok || mergeBase.Stdout.Trim() != baseCommit) return SyncOutcome.NoOp;

        var merge = await _shell.GitAsync(worktreePath, ct, "merge", mainlineRef).ConfigureAwait(false);
        if (merge.Ok) return SyncOutcome.Synced;

        await _shell.GitAsync(worktreePath, ct, "merge", "--abort").ConfigureAwait(false);
        return SyncOutcome.Conflict;
    }
}
