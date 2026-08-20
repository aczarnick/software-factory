namespace Factory.Runtime;

/// <summary>
/// Detects whether mainline has advanced past an item's worktree branch base commit and, if so,
/// merges mainline into the worktree. Detection is by comparing the branch's recorded base
/// commit against mainline HEAD via <c>merge-base</c> — it never inspects the item's branch tip,
/// so a sync decision does not depend on how far the item's own work has progressed. Every merge
/// (and abort) runs inside the worktree; the main checkout is never written to.
/// </summary>
public sealed class WorktreeSyncService
{
    public async Task<SyncOutcome> SyncAsync(
        string mainCheckoutPath, string mainlineRef, string worktreePath, string baseCommit,
        CancellationToken ct = default)
    {
        var head = await Shell.GitAsync(mainCheckoutPath, ct, "rev-parse", mainlineRef).ConfigureAwait(false);
        if (!head.Ok) return SyncOutcome.NoOp;

        var headSha = head.Stdout.Trim();
        if (headSha == baseCommit) return SyncOutcome.NoOp;

        // baseCommit must be a strict ancestor of mainline HEAD for mainline to have "advanced
        // past" it — merge-base of the two is baseCommit itself exactly when that holds.
        var mergeBase = await Shell.GitAsync(mainCheckoutPath, ct, "merge-base", baseCommit, headSha)
            .ConfigureAwait(false);
        if (!mergeBase.Ok || mergeBase.Stdout.Trim() != baseCommit) return SyncOutcome.NoOp;

        var merge = await Shell.GitAsync(worktreePath, ct, "merge", mainlineRef).ConfigureAwait(false);
        if (merge.Ok) return SyncOutcome.Synced;

        await Shell.GitAsync(worktreePath, ct, "merge", "--abort").ConfigureAwait(false);
        return SyncOutcome.Conflict;
    }
}
